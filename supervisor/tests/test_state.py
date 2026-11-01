"""Unit tests for the pure boot-replay decision logic + atomic state.json
read/write (P02)."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

from supervisor.state import RETRY_CAP, Settings, StateStore, SupervisorState, decide_boot_action, normalize_argv


def test_normalize_argv_prepends_vllm_only_for_bare_serve():
    assert normalize_argv(["serve", "/models/foo", "--port", "9999"]) == ["vllm", "serve", "/models/foo", "--port", "9999"]


def test_normalize_argv_leaves_other_first_elements_alone():
    assert normalize_argv(["python", "fake_vllm.py", "--port", "9999"]) == ["python", "fake_vllm.py", "--port", "9999"]
    assert normalize_argv(["vllm", "serve", "/models/foo"]) == ["vllm", "serve", "/models/foo"]


def test_normalize_argv_handles_empty():
    assert normalize_argv([]) == []


def test_decide_boot_action_no_state_no_fallback_stays_idle():
    decision, state = decide_boot_action(None, None)
    assert decision.should_replay is False
    assert state.last_argv is None


def test_decide_boot_action_no_state_uses_fallback_as_initial_argv():
    decision, state = decide_boot_action(None, ["serve", "/models/foo"])
    assert decision.should_replay is True
    assert decision.argv == ["vllm", "serve", "/models/foo"]
    assert state.last_argv == ["vllm", "serve", "/models/foo"]


def test_decide_boot_action_auto_start_off_always_idle():
    state = SupervisorState(last_argv=["vllm", "serve", "x"], last_run_outcome="healthy", settings=Settings(AUTO_START="off"))
    decision, _ = decide_boot_action(state, None)
    assert decision.should_replay is False
    assert "AUTO_START=off" in decision.reason


def test_decide_boot_action_replays_last_argv_when_healthy():
    state = SupervisorState(last_argv=["vllm", "serve", "x"], last_run_outcome="healthy")
    decision, _ = decide_boot_action(state, None)
    assert decision.should_replay is True
    assert decision.argv == ["vllm", "serve", "x"]


def test_decide_boot_action_does_not_replay_when_failed():
    state = SupervisorState(last_argv=["vllm", "serve", "x"], last_run_outcome="failed")
    decision, _ = decide_boot_action(state, None)
    assert decision.should_replay is False


def test_decide_boot_action_does_not_replay_when_crashed():
    state = SupervisorState(last_argv=["vllm", "serve", "x"], last_run_outcome="crashed")
    decision, _ = decide_boot_action(state, None)
    assert decision.should_replay is False


def test_decide_boot_action_does_not_replay_when_never_run():
    state = SupervisorState(last_argv=["vllm", "serve", "x"], last_run_outcome=None)
    decision, _ = decide_boot_action(state, None)
    assert decision.should_replay is False


def test_decide_boot_action_stops_after_retry_cap():
    state = SupervisorState(
        last_argv=["vllm", "serve", "x"], last_run_outcome="healthy", consecutive_failed_replays=RETRY_CAP,
    )
    decision, _ = decide_boot_action(state, None)
    assert decision.should_replay is False
    assert "retry cap" in decision.reason


def test_decide_boot_action_below_retry_cap_still_replays():
    state = SupervisorState(
        last_argv=["vllm", "serve", "x"], last_run_outcome="healthy", consecutive_failed_replays=RETRY_CAP - 1,
    )
    decision, _ = decide_boot_action(state, None)
    assert decision.should_replay is True


def test_state_store_round_trips_atomically(tmp_path):
    store = StateStore(tmp_path / "state.json")
    assert store.load() is None
    assert not store.exists()

    state = SupervisorState(
        last_argv=["vllm", "serve", "x"],
        last_good_argv=["vllm", "serve", "x"],
        last_run_outcome="healthy",
        consecutive_failed_replays=0,
        settings=Settings(AUTO_START="last_good"),
    )
    store.save(state)
    assert store.exists()

    loaded = store.load()
    assert loaded == state


def test_state_store_creates_parent_directory(tmp_path):
    store = StateStore(tmp_path / "nested" / "state.json")
    store.save(SupervisorState())
    assert store.exists()
