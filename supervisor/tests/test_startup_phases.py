"""StartupTracker against REAL vLLM 0.33 startup output (captured on serenity, RTX 3060, Gemma 4
12B AWQ): the fixture's own timestamps show ~75 s of silent imports, 8 s of weight loading and
40 s of warm-up -- the phases the UI used to collapse into one instant "Load weights" step."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

from supervisor.startup_phases import StartupTracker

FIXTURE = Path(__file__).parent / "fixtures" / "vllm_0.33_gemma4_awq_startup.log"
LINES = FIXTURE.read_text().splitlines()


def upto(marker: str, inclusive: bool = True) -> str:
    """The log text as it stood when `marker` was the newest line."""
    idx = next(i for i, line in enumerate(LINES) if marker in line)
    return "\n".join(LINES[: idx + (1 if inclusive else 0)])


def test_before_any_loading_marker_is_still_starting():
    snap = StartupTracker().update(upto("Initializing a V1 LLM engine"))
    assert snap.stage == "starting"
    assert snap.weights_pct is None
    assert snap.note == "engine core starting"


def test_empty_output_is_starting():
    assert StartupTracker().update("").stage == "starting"


def test_loading_begins_at_loading_model_from_scratch_not_at_spawn():
    assert StartupTracker().update(upto("Loading model from scratch", inclusive=False)).stage == "starting"
    assert StartupTracker().update(upto("Loading model from scratch")).stage == "loading"


def test_shard_bar_reports_real_progress_while_loading():
    snap = StartupTracker().update(upto("0% Completed"))
    assert snap.stage == "loading"
    assert (snap.weights_pct, snap.shards) == (0, "0/1")


def test_model_loading_took_ends_loading_and_starts_warmup():
    snap = StartupTracker().update(upto("Model loading took"))
    assert snap.stage == "warming_up"
    assert snap.weights_pct == 100


def test_warmup_notes_follow_the_latest_vllm_line():
    assert StartupTracker().update(upto("Encoder cache will be initialized")).note == "profiling the multimodal encoder"
    assert StartupTracker().update(upto("Available KV cache memory")).note == "KV cache sized"
    assert StartupTracker().update(upto("init engine (profile")).note == "engine initialised"


def test_server_start_marks_server_up():
    assert StartupTracker().update(upto("Starting vLLM server on")).stage == "server_up"
    assert StartupTracker().update("\n".join(LINES)).stage == "server_up"


def test_replaying_the_whole_log_visits_every_stage_in_order_and_never_goes_backwards():
    tracker = StartupTracker()
    seen: list[str] = []
    for i in range(1, len(LINES) + 1):
        stage = tracker.update("\n".join(LINES[:i])).stage
        if not seen or seen[-1] != stage:
            seen.append(stage)
    assert seen == ["starting", "loading", "warming_up", "server_up"]


def test_stage_does_not_regress_if_the_ring_buffer_drops_old_lines():
    tracker = StartupTracker()
    assert tracker.update(upto("Model loading took")).stage == "warming_up"
    # The buffer later holds only the tail -- no loading markers left in it.
    assert tracker.update("some unrelated later line").stage == "warming_up"


def test_multi_shard_bar_uses_the_latest_bar():
    text = (
        "(EngineCore pid=1) INFO 00:00:00 [model_runner.py:399] Loading model from scratch...\n"
        "Loading safetensors checkpoint shards:   0% Completed | 0/2 [00:00<?, ?it/s]\r"
        "Loading safetensors checkpoint shards:  50% Completed | 1/2 [00:58<00:58, 58.71s/it]\r"
    )
    snap = StartupTracker().update(text)
    assert (snap.stage, snap.weights_pct, snap.shards) == ("loading", 50, "1/2")


def test_older_vllm_wording_for_load_start_is_recognised():
    assert StartupTracker().update("INFO ... Starting to load model /models/x...").stage == "loading"


def test_cuda_graph_capture_progress_is_reported_during_warmup():
    text = (
        "Loading model from scratch...\n"
        "Model loading took 8.1 GiB memory and 9.2 seconds\n"
        "Capturing CUDA graphs (mixed prefill-decode, PIECEWISE):  40%|████      | 27/67 [00:01<00:02, 20.1it/s]\r"
    )
    snap = StartupTracker().update(text)
    assert snap.stage == "warming_up"
    assert snap.graphs_pct == 40
    assert snap.note == "capturing CUDA graphs"


def test_detail_only_contains_the_fields_that_are_known():
    detail = StartupTracker().update(upto("0% Completed")).detail()
    assert detail["weights_pct"] == 0
    assert detail["shards"] == "0/1"
    assert set(detail) <= {"weights_pct", "shards", "graphs_pct", "note"}
    assert StartupTracker().update("").detail() == {}
