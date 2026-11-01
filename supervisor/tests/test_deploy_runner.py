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

from supervisor.deploy_runner import DeployInProgressError, DeployRunner
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
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"))
    return runner, pm


def test_deploy_from_idle_skips_stopping_and_releasing_gpu(tmp_path):
    runner, pm = new_runner(tmp_path)
    port = free_port()
    try:
        ok = run(runner.deploy_and_wait(fake_argv(port, "model-a"), deployment_id="d1", health_timeout=10))
        assert ok is True

        states = [e.state for e in runner.events_for("d1")]
        assert states == ["starting", "loading", "waiting_health", "ready"]
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
        assert states == ["stopping", "releasing_gpu", "starting", "loading", "waiting_health", "ready"]
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
        assert states == ["starting", "loading", "waiting_health", "failed"]

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
        assert states == ["starting", "loading", "waiting_health", "failed"]

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
        assert states == ["starting", "loading", "waiting_health", "failed"]

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
        assert states[-2:] == ["ready", "crashed"]
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
