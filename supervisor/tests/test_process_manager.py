"""Unit tests for ProcessManager's spawn/stop lifecycle (P02) -- real
subprocesses, but tiny/fast ones (sleep, a short Python script), not vLLM."""
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

from supervisor.process_manager import ProcessManager


def test_spawn_tracks_pid_argv_and_started_at():
    pm = ProcessManager()
    info = pm.spawn(["sleep", "5"])

    assert info.pid > 0
    assert info.argv == ["sleep", "5"]
    assert info.started_at > 0
    assert pm.is_running()

    pm.stop()


def test_spawn_twice_without_stopping_raises():
    pm = ProcessManager()
    pm.spawn(["sleep", "5"])
    try:
        import pytest

        with pytest.raises(RuntimeError):
            pm.spawn(["sleep", "5"])
    finally:
        pm.stop()


def test_stop_terminates_a_running_child():
    pm = ProcessManager()
    pm.spawn(["sleep", "30"])
    assert pm.is_running()

    pm.stop(graceful_timeout=2)

    assert not pm.is_running()


def test_exit_code_reflects_a_process_that_already_exited():
    pm = ProcessManager()
    pm.spawn([sys.executable, "-c", "import sys; sys.exit(7)"])
    time.sleep(0.5)

    assert not pm.is_running()
    assert pm.exit_code() == 7


def test_stop_on_an_already_exited_child_is_a_no_op():
    pm = ProcessManager()
    pm.spawn([sys.executable, "-c", "pass"])
    time.sleep(0.5)
    assert not pm.is_running()

    pm.stop()  # must not raise
