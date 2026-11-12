"""Integration-flavored unit tests for the deploy state machine (P03a) --
real subprocesses (the fake vLLM test double from P00), real HTTP health
checks, but no Docker: everything runs as plain local processes.
"""
import asyncio
import socket
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

from supervisor.deploy_runner import DeployInProgressError, DeployRunner, NotServingError
from supervisor.log_store import DeploymentArchive
from supervisor.process_manager import ProcessManager
from supervisor.state import StateStore

FAKE_VLLM = str(Path(__file__).parent.parent.parent / "tests" / "fake-vllm" / "fake_vllm.py")


def free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def fake_argv(port: int, served_model_name: str) -> list[str]:
    return [sys.executable, FAKE_VLLM, "--port", str(port), "--served-model-name", served_model_name]


def run(coro):
    return asyncio.run(coro)


def new_runner(tmp_path) -> tuple[DeployRunner, ProcessManager]:
    pm = ProcessManager()
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), archive=DeploymentArchive(tmp_path))
    return runner, pm


def test_deploy_from_idle_skips_stopping_and_releasing_gpu(tmp_path):
    runner, pm = new_runner(tmp_path)
    port = free_port()
    try:
        ok = run(runner.deploy_and_wait(fake_argv(port, "model-a"), deployment_id="d1", health_timeout=10))
        assert ok is True

        states = [e.state for e in runner.events_for("d1")]
        assert states == ["starting", "waiting_health", "ready", "smoke"]
        assert runner.state == "ready"
    finally:
        pm.stop()


def test_redeploy_goes_through_stop_and_release_and_updates_last_good_argv(tmp_path):
    runner, pm = new_runner(tmp_path)
    port_a, port_b = free_port(), free_port()
    try:
        run(runner.deploy_and_wait(fake_argv(port_a, "model-a"), deployment_id="d1", health_timeout=10))
        old_pid = pm.info.pid

        ok = run(runner.deploy_and_wait(fake_argv(port_b, "model-b"), deployment_id="d2", health_timeout=10))
        assert ok is True

        states = [e.state for e in runner.events_for("d2")]
        assert states == ["stopping", "releasing_gpu", "starting", "waiting_health", "ready", "smoke"]
        assert pm.info.pid != old_pid
        assert pm.info.argv == fake_argv(port_b, "model-b")

        store = StateStore(tmp_path / "state.json")
        assert store.load().last_good_argv == fake_argv(port_b, "model-b")
    finally:
        pm.stop()


def test_deploy_with_env_reaches_the_child(tmp_path):
    import json
    import urllib.request

    runner, pm = new_runner(tmp_path)
    port = free_port()
    try:
        ok = run(runner.deploy_and_wait(
            fake_argv(port, "model-a"), env={"FOO": "bar"}, deployment_id="d1", health_timeout=10,
        ))
        assert ok is True

        with urllib.request.urlopen(f"http://127.0.0.1:{port}/debug/env", timeout=2) as resp:
            env = json.loads(resp.read())
        assert env.get("FOO") == "bar"
    finally:
        pm.stop()


def test_concurrent_deploy_raises_with_the_in_flight_id(tmp_path):
    runner, pm = new_runner(tmp_path)
    port = free_port()

    async def scenario():
        # start_deploy() calls asyncio.create_task() internally, which
        # needs a running loop -- same reason the admin API's /admin/deploy
        # route must be `async def`, not a plain `def` (see admin_api.py).
        first_id = runner.start_deploy(fake_argv(port, "model-a"), deployment_id="d1", health_timeout=10)

        try:
            runner.start_deploy(fake_argv(free_port(), "model-b"), deployment_id="d2")
            assert False, "expected DeployInProgressError"
        except DeployInProgressError as exc:
            assert exc.deployment_id == first_id

        for _ in range(100):
            if runner.state == "ready":
                return
            await asyncio.sleep(0.1)
        raise AssertionError("first deploy never became ready")

    try:
        run(scenario())
    finally:
        pm.stop()


def test_events_after_filters_to_newer_ones_with_strictly_increasing_seq(tmp_path):
    runner, pm = new_runner(tmp_path)
    port = free_port()
    try:
        run(runner.deploy_and_wait(fake_argv(port, "model-a"), deployment_id="d1", health_timeout=10))

        all_events = runner.events_for("d1")
        seqs = [e.seq for e in all_events]
        assert seqs == sorted(seqs)
        assert len(set(seqs)) == len(seqs)

        after_first = runner.events_for("d1", after=all_events[0].seq)
        assert after_first == all_events[1:]
    finally:
        pm.stop()


def test_slow_start_beyond_health_timeout_fails_with_health_timeout_and_kills_child(tmp_path):
    runner, pm = new_runner(tmp_path)
    port = free_port()
    try:
        argv = [sys.executable, FAKE_VLLM, "--port", str(port), "--served-model-name", "model-a", "--fake-slow-start", "10"]
        start = time.monotonic()
        ok = run(runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=2))
        elapsed = time.monotonic() - start

        assert ok is False
        assert runner.state == "failed"
        assert elapsed < 5, "should fail at ~health_timeout, not hang"
        states = [e.state for e in runner.events_for("d1")]
        assert states == ["starting", "waiting_health", "failed"]

        report = runner.events_for("d1")[-1].detail
        assert report["reason"] == "health_timeout"

        time.sleep(0.5)
        assert not pm.is_running(), "the still-alive-but-unhealthy child must be killed, not left running"
    finally:
        pm.stop()


def test_child_exiting_during_loading_fails_fast_not_at_health_timeout(tmp_path):
    runner, pm = new_runner(tmp_path)
    try:
        # A nonexistent flag makes the real argparse-based fake vLLM exit
        # almost immediately with exit code 2 -- this must fail in seconds,
        # nowhere near the 300s health_timeout below.
        argv = [sys.executable, FAKE_VLLM, "--this-flag-does-not-exist"]
        start = time.monotonic()
        ok = run(runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=300))
        elapsed = time.monotonic() - start

        assert ok is False
        assert elapsed < 10, f"fail-fast should take seconds, took {elapsed:.1f}s"
        states = [e.state for e in runner.events_for("d1")]
        assert states == ["starting", "waiting_health", "failed"]

        report = runner.events_for("d1")[-1].detail
        assert report["reason"] == "bad_args"
        assert report["exit_code"] == 2
        assert report["log_tail"]  # non-empty
    finally:
        pm.stop()


def test_fake_crash_during_loading_fails_fast_with_process_exited(tmp_path):
    """P03b's DoD, literally: a child that exits during loading -> failed
    in seconds with reason=process_exited, the exit code, and a non-empty
    log_tail (the --fake-crash case; --this-flag-does-not-exist above hits
    the bad_args path instead -- both are "fails fast", different reasons)."""
    runner, pm = new_runner(tmp_path)
    try:
        argv = [sys.executable, FAKE_VLLM, "--fake-crash"]
        start = time.monotonic()
        ok = run(runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=300))
        elapsed = time.monotonic() - start

        assert ok is False
        assert elapsed < 10, f"fail-fast should take seconds, took {elapsed:.1f}s"
        states = [e.state for e in runner.events_for("d1")]
        assert states == ["starting", "waiting_health", "failed"]

        report = runner.events_for("d1")[-1].detail
        assert report["reason"] == "process_exited"
        assert report["exit_code"] == 1
        assert report["log_tail"]
    finally:
        pm.stop()


def test_exit_after_ready_is_crashed_not_failed(tmp_path):
    runner, pm = new_runner(tmp_path)
    port = free_port()

    async def scenario():
        argv = [sys.executable, FAKE_VLLM, "--port", str(port), "--served-model-name", "model-a", "--fake-exit-after", "2"]
        ok = await runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=10)
        assert ok is True
        assert runner.state == "ready"

        # The post-ready watcher task is scheduled on THIS event loop (by
        # deploy_and_wait, above) -- it must keep running after
        # deploy_and_wait returns, within the same asyncio.run() call, or
        # there's nothing left to observe the crash (asyncio.run() tears
        # down its loop, and anything still scheduled on it, the moment its
        # own top-level coroutine returns).
        for _ in range(100):
            if runner.state == "crashed":
                return
            await asyncio.sleep(0.2)
        raise AssertionError("never observed a crashed transition")

    try:
        run(scenario())

        states = [e.state for e in runner.events_for("d1")]
        assert states[-3:] == ["ready", "smoke", "crashed"]
        report = runner.events_for("d1")[-1].detail
        assert report["exit_code"] is not None

        store = StateStore(tmp_path / "state.json")
        assert store.load().last_run_outcome == "crashed"
    finally:
        pm.stop()


def test_failure_report_persists_in_state_for_a_fresh_store_to_read(tmp_path):
    runner, pm = new_runner(tmp_path)
    try:
        argv = [sys.executable, FAKE_VLLM, "--this-flag-does-not-exist"]
        run(runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=10))

        # Simulates reading it after a restart: a brand new StateStore
        # object, no shared in-memory state with `runner` at all.
        reloaded = StateStore(tmp_path / "state.json").load()
        assert reloaded.last_run_outcome == "failed"
        assert reloaded.last_failure_report["reason"] == "bad_args"
    finally:
        pm.stop()


def test_events_for_a_past_deployment_survive_a_restart(tmp_path):
    """P05 DoD: restart the supervisor -> events?after=<seq> for a past
    deployment still returns the right events. Simulated with a brand new
    DeployRunner (no shared in-memory state at all) pointed at the same
    archive directory on disk."""
    runner, pm = new_runner(tmp_path)
    try:
        argv = [sys.executable, FAKE_VLLM, "--fake-crash"]
        run(runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=10))
    finally:
        pm.stop()

    fresh_pm = ProcessManager()
    fresh_runner = DeployRunner(fresh_pm, StateStore(tmp_path / "state.json"), archive=DeploymentArchive(tmp_path))

    all_events = fresh_runner.events_for("d1")
    assert [e.state for e in all_events] == ["starting", "waiting_health", "failed"]

    after = fresh_runner.events_for("d1", after=all_events[0].seq)
    assert [e.state for e in after] == ["waiting_health", "failed"]


def test_a_fast_deploy_still_archives_its_full_startup_log(tmp_path):
    """A deploy that reaches ready inside ~1s can conclude before the
    log-batch task's own sleep(1) ever elapses -- without a final drain on
    conclusion, whatever output arrived in that window never reaches the
    per-deployment archive at all (found by hand against a real
    container: GET .../logs showed only 1 of 3 lines the ring buffer
    had)."""
    port = free_port()
    runner, pm = new_runner(tmp_path)
    try:
        ok = run(runner.deploy_and_wait(fake_argv(port, "model-a"), deployment_id="d1", health_timeout=10))
        assert ok is True

        archived = runner.archive.read_log_tail("d1")
        assert "fake-vllm: listening" in archived
        assert "GET /health" in archived
        assert "GET /v1/models" in archived
    finally:
        pm.stop()


def test_successful_deploy_runs_smoke_test_by_default_and_passes(tmp_path):
    runner, pm = new_runner(tmp_path)
    port = free_port()
    try:
        ok = run(runner.deploy_and_wait(fake_argv(port, "model-a"), deployment_id="d1", health_timeout=10))
        assert ok is True
        assert runner.state == "ready", "a failed smoke test must not change the terminal deploy state"
        assert runner.last_smoke_ok is True

        states = [e.state for e in runner.events_for("d1")]
        assert states == ["starting", "waiting_health", "ready", "smoke"]
        smoke_event = runner.events_for("d1")[-1]
        assert smoke_event.detail == {"outcome": "pass"}
    finally:
        pm.stop()


def test_smoke_test_failure_is_warn_and_ready_not_a_deploy_failure(tmp_path):
    """Owner-confirmed 2026-11-01: a bad smoke answer still ends in ready,
    with smoke_ok=false and a 'smoke ... fail' event -- never torn down,
    never reported as a deploy failure."""
    runner, pm = new_runner(tmp_path)
    port = free_port()
    try:
        argv = [sys.executable, FAKE_VLLM, "--port", str(port), "--served-model-name", "model-a", "--fake-bad-output"]
        ok = run(runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=10))
        assert ok is True
        assert runner.state == "ready"
        assert runner.last_smoke_ok is False
        assert pm.is_running(), "a failed smoke test must not kill a model that's actually up"

        states = [e.state for e in runner.events_for("d1")]
        assert states == ["starting", "waiting_health", "ready", "smoke"]
        smoke_event = runner.events_for("d1")[-1]
        assert smoke_event.detail["outcome"] == "fail"
        assert "excerpt" in smoke_event.detail
    finally:
        pm.stop()


def test_smoke_skip_true_omits_the_smoke_event(tmp_path):
    runner, pm = new_runner(tmp_path)
    port = free_port()
    try:
        ok = run(runner.deploy_and_wait(
            fake_argv(port, "model-a"), deployment_id="d1", health_timeout=10, smoke={"skip": True},
        ))
        assert ok is True
        assert runner.last_smoke_ok is None

        states = [e.state for e in runner.events_for("d1")]
        assert states == ["starting", "waiting_health", "ready"]
    finally:
        pm.stop()


def test_run_smoke_test_now_works_while_serving(tmp_path):
    runner, pm = new_runner(tmp_path)
    port = free_port()
    try:
        run(runner.deploy_and_wait(
            fake_argv(port, "model-a"), deployment_id="d1", health_timeout=10, smoke={"skip": True},
        ))
        assert runner.last_smoke_ok is None  # auto smoke was skipped

        result = run(runner.run_smoke_test_now())
        assert result.ok is True
        assert runner.last_smoke_ok is True
    finally:
        pm.stop()


def test_run_smoke_test_now_raises_not_serving_when_idle(tmp_path):
    runner, pm = new_runner(tmp_path)
    try:
        try:
            run(runner.run_smoke_test_now())
            assert False, "expected NotServingError"
        except NotServingError as exc:
            assert exc.state == "idle"
    finally:
        pm.stop()


# --- real start-up progress (replays a captured vLLM 0.33 log through the fake server) ---------

STARTUP_LOG = Path(__file__).parent / "fixtures" / "vllm_0.33_gemma4_awq_startup.log"


def _startup_script(tmp_path, gap: float = 1.6) -> str:
    """The real captured log, cut into the phases it really contains with a pause between them
    (longer than the supervisor's one-second watch interval, so each phase is seen on its own)."""
    lines = STARTUP_LOG.read_text().splitlines()

    def idx(marker: str) -> int:
        return next(i for i, line in enumerate(lines) if marker in line)

    cuts = [idx("Loading model from scratch"), idx("0% Completed") + 1, idx("Model loading took") + 1,
            idx("Starting vLLM server on")]
    out, prev = [], 0
    for cut in cuts:
        out += lines[prev:cut]
        out.append(f"#sleep {gap}")
        prev = cut
    out += lines[prev:]
    path = tmp_path / "startup.log"
    path.write_text("\n".join(out) + "\n")
    return str(path)


def test_real_startup_log_drives_loading_and_warmup_states_in_order(tmp_path):
    runner, pm = new_runner(tmp_path)
    port = free_port()
    argv = fake_argv(port, "model-a") + ["--fake-startup-log", _startup_script(tmp_path)]
    try:
        ok = run(runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=60))
        assert ok is True
        events = runner.events_for("d1")
        states = [e.state for e in events]
        assert states[0] == "starting"
        # loading is reported only once vLLM says it is loading the model, warming_up only after
        # "Model loading took", and waiting_health only once its server is starting.
        order = [s for i, s in enumerate(states) if s not in states[:i]]
        assert order == ["starting", "loading", "warming_up", "waiting_health", "ready", "smoke"], states
        loading = [e for e in events if e.state == "loading"]
        assert any((e.detail or {}).get("weights_pct") == 0 for e in loading)
        warming = [e for e in events if e.state == "warming_up"]
        assert (warming[0].detail or {}).get("weights_pct") == 100
    finally:
        pm.stop()


def test_loading_is_not_reported_while_vllm_is_still_importing(tmp_path):
    """The bug: 'Load weights' used to finish ~1s after spawn. A start-up that has printed only its
    import/config lines must still be 'starting'."""
    lines = STARTUP_LOG.read_text().splitlines()
    cut = next(i for i, line in enumerate(lines) if "Loading model from scratch" in line)
    script = tmp_path / "importing.log"
    script.write_text("\n".join(lines[:cut] + ["#sleep 30"]) + "\n")

    runner, pm = new_runner(tmp_path)
    port = free_port()
    argv = fake_argv(port, "model-a") + ["--fake-startup-log", str(script)]

    async def scenario():
        runner.start_deploy(argv, deployment_id="d1", health_timeout=60)
        await asyncio.sleep(4)  # several watch passes while it is "still importing"
        return [e.state for e in runner.events_for("d1")], runner.state

    try:
        states, current = run(scenario())
    finally:
        pm.stop()
    assert current == "starting"
    assert "loading" not in states and "waiting_health" not in states


# --- P2: engine-aware deploys (llama.cpp) ---

FAKE_LLAMACPP = str(Path(__file__).parent.parent.parent / "tests" / "fake-llamacpp" / "fake_llamacpp.py")
LLAMACPP_LOG = str(Path(__file__).parent / "fixtures" / "llamacpp_qwen2.5-0.5b_startup.log")


def llamacpp_argv(port: int, alias: str) -> list[str]:
    return [sys.executable, FAKE_LLAMACPP, "--port", str(port), "--alias", alias, "--fake-startup-log", LLAMACPP_LOG]


def test_llamacpp_deploy_uses_its_own_tracker_and_reaches_ready(tmp_path):
    pm = ProcessManager()
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), archive=DeploymentArchive(tmp_path), engine="llamacpp")
    try:
        ok = run(runner.deploy_and_wait(llamacpp_argv(free_port(), "model-a"), deployment_id="d1", health_timeout=15))
        assert ok is True
        states = [e.state for e in runner.events_for("d1")]
        assert "warming_up" not in states  # vLLM-only phase must never be reported for llama.cpp
        assert states[0] == "starting" and states[-2:] == ["ready", "smoke"]
        assert runner.state == "ready"
    finally:
        pm.stop()


def test_default_runner_engine_is_vllm(tmp_path):
    runner, _ = new_runner(tmp_path)
    assert runner.engine == "vllm"


def test_llamacpp_smoke_test_sends_the_alias_as_the_model(tmp_path):
    pm = ProcessManager()
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), archive=DeploymentArchive(tmp_path), engine="llamacpp")
    try:
        argv = llamacpp_argv(free_port(), "model-a") + ["--fake-require-model", "model-a"]
        ok = run(runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=15))
        assert ok is True
        assert runner.last_smoke_ok is True  # fake answers OK only when the request's model == alias
    finally:
        pm.stop()


def test_failed_version_fetch_is_not_retried_on_every_status_poll(tmp_path, monkeypatch):
    """engine_info() runs on every GET /admin/status; an unreadable /props must back off
    instead of costing a blocking probe per poll for the deployment's whole life."""
    calls = []
    clock = [1000.0]
    monkeypatch.setattr("supervisor.deploy_runner.fetch_version", lambda engine, port: calls.append(port) or None)
    monkeypatch.setattr("supervisor.deploy_runner._now", lambda: clock[0])
    pm = ProcessManager()
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), archive=DeploymentArchive(tmp_path), engine="llamacpp")
    try:
        assert run(runner.deploy_and_wait(llamacpp_argv(free_port(), "m"), deployment_id="d1", health_timeout=15))
        for _ in range(10):
            assert runner.engine_info["version"] is None
        assert len(calls) == 1  # one probe, then backed off

        clock[0] += 3600  # long past any backoff window
        runner.engine_info
        assert len(calls) == 2  # eventually retried, so a late /props is still picked up
    finally:
        pm.stop()


def test_version_fetch_backoff_resets_for_a_new_child(tmp_path, monkeypatch):
    calls = []
    monkeypatch.setattr("supervisor.deploy_runner.fetch_version", lambda engine, port: calls.append(port) or None)
    monkeypatch.setattr("supervisor.deploy_runner._now", lambda: 1000.0)
    pm = ProcessManager()
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), archive=DeploymentArchive(tmp_path), engine="llamacpp")
    try:
        run(runner.deploy_and_wait(llamacpp_argv(free_port(), "a"), deployment_id="d1", health_timeout=15))
        runner.engine_info
        run(runner.deploy_and_wait(llamacpp_argv(free_port(), "b"), deployment_id="d2", health_timeout=15))
        runner.engine_info
        assert len(calls) == 2  # the new pid is probed immediately, not held back by the old child's backoff
    finally:
        pm.stop()
