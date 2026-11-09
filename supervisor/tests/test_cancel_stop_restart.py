"""Cancel + Stop + Restart (P07) -- real fake-vllm subprocesses, no Docker.
"""
import asyncio
import json
import socket
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

from supervisor.deploy_runner import DeployInProgressError, DeployRunner, NoDeployInProgressError, NotServingError
from supervisor.gpu_backend import FakeBackend, GpuBackend
from supervisor.log_store import DeploymentArchive
from supervisor.process_manager import ProcessManager
from supervisor.state import StateStore

FAKE_VLLM = str(Path(__file__).parent.parent.parent / "tests" / "fake-vllm" / "fake_vllm.py")


def free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def fake_argv(port: int, served_model_name: str, *extra: str) -> list[str]:
    return [sys.executable, FAKE_VLLM, "--port", str(port), "--served-model-name", served_model_name, *extra]


def run(coro):
    return asyncio.run(coro)


def new_runner(tmp_path, gpu_backend=None) -> tuple[DeployRunner, ProcessManager]:
    pm = ProcessManager()
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), gpu_backend=gpu_backend, archive=DeploymentArchive(tmp_path))
    return runner, pm


def test_cancel_before_the_new_deploy_runs_at_all_leaves_old_model_untouched(tmp_path):
    """Phase 1: cancel immediately after admission, before the deploy task
    has had a chance to run a single line -- the old model must still be
    the one serving, completely untouched, and state reverts to ready."""
    runner, pm = new_runner(tmp_path)
    port_a, port_b = free_port(), free_port()

    async def scenario():
        ok = await runner.deploy_and_wait(fake_argv(port_a, "model-a"), deployment_id="d1", health_timeout=10)
        assert ok is True
        old_pid = pm.info.pid

        deployment_id = runner.start_deploy(fake_argv(port_b, "model-b"), deployment_id="d2", health_timeout=10)
        result = await runner.cancel_deploy(deployment_id)
        assert result == {"deployment_id": "d2", "state": "ready"}
        assert runner.state == "ready"
        assert pm.info.pid == old_pid, "the OLD child must still be running, never touched"
        assert pm.is_running()

        events = runner.events_for("d2")
        assert events[-1].state == "cancelled"
        assert events[-1].detail == {"phase": "pre_stop"}
        assert runner.current_deployment_id is None, "the lock must be released"

    try:
        run(scenario())
    finally:
        pm.stop()


def test_cancel_while_the_new_child_is_loading_kills_it_and_goes_idle(tmp_path):
    """Phase 2: cancel after the old model has already been stopped and the
    new one is loading (slow start) -- the loading child is killed, state
    goes idle (not back to the old model, which is already gone), nothing
    auto-restarts."""
    runner, pm = new_runner(tmp_path)
    port_a, port_b = free_port(), free_port()

    async def scenario():
        ok = await runner.deploy_and_wait(fake_argv(port_a, "model-a"), deployment_id="d1", health_timeout=10)
        assert ok is True

        deployment_id = runner.start_deploy(
            fake_argv(port_b, "model-b", "--fake-slow-start", "30"), deployment_id="d2", health_timeout=60,
        )
        for _ in range(100):
            if runner.state in ("starting", "waiting_health"):
                break
            await asyncio.sleep(0.05)
        else:
            raise AssertionError("never observed the new deploy start loading")

        result = await runner.cancel_deploy(deployment_id)
        assert result == {"deployment_id": "d2", "state": "idle"}
        assert runner.state == "idle"
        assert not pm.is_running(), "the loading child must be killed"

        # Nothing auto-restarts: give it a moment, state must stay idle.
        await asyncio.sleep(1)
        assert runner.state == "idle"

        events = runner.events_for("d2")
        assert events[-1].state == "cancelled"
        assert events[-1].detail == {"phase": "post_stop"}

    try:
        run(scenario())
    finally:
        pm.stop()


def test_cancel_is_responsive_even_with_a_long_health_timeout(tmp_path):
    """Cancel must not wait out health_timeout -- the cancel_event wakes
    the blocked health-poll thread within about a second (P07)."""
    runner, pm = new_runner(tmp_path)
    port = free_port()

    async def scenario():
        deployment_id = runner.start_deploy(
            fake_argv(port, "model-a", "--fake-slow-start", "300"), deployment_id="d1", health_timeout=300,
        )
        # The fake prints no vLLM start-up markers, so it stays in "starting" (the supervisor
        # does not claim a phase it has not seen) until /health answers, which --fake-slow-start
        # delays for 300 s -- the cancel has to wake the start-up watch within about a second.
        for _ in range(100):
            if runner.state == "starting":
                break
            await asyncio.sleep(0.05)
        else:
            raise AssertionError("never reached starting")
        await asyncio.sleep(1.5)  # let the start-up watch run a pass or two

        start = time.monotonic()
        await runner.cancel_deploy(deployment_id)
        elapsed = time.monotonic() - start
        assert elapsed < 5, f"cancel should be responsive, took {elapsed:.1f}s"

    try:
        run(scenario())
    finally:
        pm.stop()


def test_cancel_with_nothing_in_flight_raises(tmp_path):
    runner, pm = new_runner(tmp_path)
    try:
        try:
            run(runner.cancel_deploy("nope"))
            assert False, "expected NoDeployInProgressError"
        except NoDeployInProgressError:
            pass
    finally:
        pm.stop()


def test_cancel_with_a_stale_id_raises(tmp_path):
    runner, pm = new_runner(tmp_path)
    port = free_port()

    async def scenario():
        runner.start_deploy(fake_argv(port, "model-a", "--fake-slow-start", "30"), deployment_id="d1", health_timeout=60)
        try:
            await runner.cancel_deploy("some-other-id")
            assert False, "expected NoDeployInProgressError"
        except NoDeployInProgressError:
            pass
        # Clean up the still-in-flight real deploy for this test.
        await runner.cancel_deploy("d1")

    try:
        run(scenario())
    finally:
        pm.stop()


def test_stop_kills_the_child_and_marks_state_json_stopped_not_healthy(tmp_path):
    runner, pm = new_runner(tmp_path)
    port = free_port()

    async def scenario():
        ok = await runner.deploy_and_wait(fake_argv(port, "model-a"), deployment_id="d1", health_timeout=10)
        assert ok is True

        result = await runner.stop()
        assert result["state"] == "idle"
        assert result["vram"]["released"] is True  # generic backend -- "assume released"
        assert runner.state == "idle"
        assert not pm.is_running()

        store = StateStore(tmp_path / "state.json")
        state = store.load()
        assert state.last_run_outcome == "stopped"
        assert state.last_good_argv == fake_argv(port, "model-a")  # untouched -- redeploy-last-working still works

    try:
        run(scenario())
    finally:
        pm.stop()


def test_stop_with_nothing_running_is_idempotent(tmp_path):
    runner, pm = new_runner(tmp_path)
    try:
        result = run(runner.stop())
        assert result == {"state": "idle", "vram": {"released": True, "used_mb": None, "total_mb": None}}
        assert runner.state == "idle"
    finally:
        pm.stop()


def test_stop_conflicts_with_an_in_flight_deploy(tmp_path):
    runner, pm = new_runner(tmp_path)
    port = free_port()

    async def scenario():
        runner.start_deploy(fake_argv(port, "model-a", "--fake-slow-start", "30"), deployment_id="d1", health_timeout=60)
        try:
            await runner.stop()
            assert False, "expected DeployInProgressError"
        except DeployInProgressError as exc:
            assert exc.deployment_id == "d1"
        await runner.cancel_deploy("d1")  # clean up

    try:
        run(scenario())
    finally:
        pm.stop()


def test_stop_reports_vram_still_held_then_recheck_flips_once_cleared(tmp_path):
    fake_path = tmp_path / "fake_gpu.json"
    fake_path.write_text(json.dumps({"vendor": "fake", "count": 1, "total_mb": 12288, "used_mb": 8000}))
    runner, pm = new_runner(tmp_path, gpu_backend=FakeBackend(fake_path))
    port = free_port()

    async def scenario():
        ok = await runner.deploy_and_wait(fake_argv(port, "model-a"), deployment_id="d1", health_timeout=10)
        assert ok is True

        result = await runner.stop()
        assert result["vram"] == {"released": False, "used_mb": 8000, "total_mb": 12288}

        # "recheck" is just another GPU query -- flips once VRAM clears.
        fake_path.write_text(json.dumps({"vendor": "fake", "count": 1, "total_mb": 12288, "used_mb": 10}))
        recheck = runner.gpu_backend.query()
        assert recheck.used_mb == 10

    try:
        run(scenario())
    finally:
        pm.stop()


def test_restart_gets_a_new_pid_with_the_same_argv_and_reaches_ready(tmp_path):
    runner, pm = new_runner(tmp_path)
    port = free_port()

    async def scenario():
        ok = await runner.deploy_and_wait(fake_argv(port, "model-a"), deployment_id="d1", health_timeout=10)
        assert ok is True
        old_pid = pm.info.pid
        old_argv = pm.info.argv

        deployment_id = runner.restart()
        for _ in range(100):
            if runner.state == "ready" and runner.current_deployment_id is None:
                break
            await asyncio.sleep(0.1)
        else:
            raise AssertionError("restart never reached ready")

        assert pm.info.pid != old_pid
        assert pm.info.argv == old_argv
        states = [e.state for e in runner.events_for(deployment_id)]
        assert states == ["stopping", "releasing_gpu", "starting", "waiting_health", "ready", "smoke"]

    try:
        run(scenario())
    finally:
        pm.stop()


def test_restart_honors_a_caller_supplied_deployment_id(tmp_path):
    """P10a: a caller (the dashboard) needs to correlate a restart with its
    own id, exactly like /admin/deploy's own optional deployment_id."""
    runner, pm = new_runner(tmp_path)
    port = free_port()

    async def scenario():
        ok = await runner.deploy_and_wait(fake_argv(port, "model-a"), deployment_id="d1", health_timeout=10)
        assert ok is True

        deployment_id = runner.restart(deployment_id="dashboard-row-42")
        assert deployment_id == "dashboard-row-42"
        for _ in range(100):
            if runner.state == "ready" and runner.current_deployment_id is None:
                break
            await asyncio.sleep(0.1)
        else:
            raise AssertionError("restart never reached ready")

        states = [e.state for e in runner.events_for("dashboard-row-42")]
        assert states == ["stopping", "releasing_gpu", "starting", "waiting_health", "ready", "smoke"]

    try:
        run(scenario())
    finally:
        pm.stop()


def test_restart_while_idle_raises_not_serving(tmp_path):
    runner, pm = new_runner(tmp_path)
    try:
        try:
            runner.restart()
            assert False, "expected NotServingError"
        except NotServingError as exc:
            assert exc.state == "idle"
    finally:
        pm.stop()
