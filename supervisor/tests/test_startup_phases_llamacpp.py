"""LlamaCppStartupTracker against REAL llama-server startup output (captured 2026-11-10,
docker run ghcr.io/ggml-org/llama.cpp:server build b12242-3b4d6ab3f, CPU-only, serving
Qwen2.5-0.5B-Instruct-GGUF). Spike (multi-engine support proposal): same job as vLLM's
StartupTracker (supervisor/startup_phases.py), written against a different engine's real
log shape rather than vLLM's.

The headline finding: llama.cpp's startup has no percentage progress of any kind -- no
shard bar, no warm-up/CUDA-graph phase at all on this build. vLLM's four phases
(starting/loading/warming_up/server_up) collapse to three here; weights_pct and graphs_pct
stay None always, same spirit as the C# LlamaCppMetricsParser's KvPct finding.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

from supervisor.startup_phases_llamacpp import LlamaCppStartupTracker

FIXTURE = Path(__file__).parent / "fixtures" / "llamacpp_qwen2.5-0.5b_startup.log"
LINES = FIXTURE.read_text().splitlines()


def upto(marker: str, inclusive: bool = True) -> str:
    idx = next(i for i, line in enumerate(LINES) if marker in line)
    return "\n".join(LINES[: idx + (1 if inclusive else 0)])


def test_before_any_loading_marker_is_still_starting():
    snap = LlamaCppStartupTracker().update(upto("initializing ...", inclusive=False))
    assert snap.stage == "starting"


def test_empty_output_is_starting():
    assert LlamaCppStartupTracker().update("").stage == "starting"


def test_loading_begins_at_loading_model_line():
    assert LlamaCppStartupTracker().update(upto("loading model", inclusive=False)).stage == "starting"
    assert LlamaCppStartupTracker().update(upto("loading model")).stage == "loading"


def test_model_loaded_marks_server_up_only_once_listening_appears():
    # "model loaded" happens before the HTTP server is actually accepting connections --
    # the UI's health step shouldn't flip green until "listening on" is seen.
    snap = LlamaCppStartupTracker().update(upto("server_run_i: model loaded"))
    assert snap.stage == "loading"

    snap = LlamaCppStartupTracker().update(upto("listening on"))
    assert snap.stage == "server_up"


def test_replaying_the_whole_log_visits_only_starting_loading_server_up_in_order():
    tracker = LlamaCppStartupTracker()
    seen: list[str] = []
    for i in range(1, len(LINES) + 1):
        stage = tracker.update("\n".join(LINES[:i])).stage
        if not seen or seen[-1] != stage:
            seen.append(stage)
    assert seen == ["starting", "loading", "server_up"]


def test_stage_does_not_regress_if_the_ring_buffer_drops_old_lines():
    tracker = LlamaCppStartupTracker()
    assert tracker.update(upto("listening on")).stage == "server_up"
    assert tracker.update("some unrelated later line").stage == "server_up"


def test_weights_pct_and_graphs_pct_are_always_none():
    """The whole point of this test: unlike vLLM, llama.cpp's startup log has no
    shard-loading percentage and no CUDA-graph-capture phase at all -- never guessed
    or synthesized, same as this spike's metrics-parser finding."""
    for i in range(1, len(LINES) + 1):
        snap = LlamaCppStartupTracker().update("\n".join(LINES[:i]))
        assert snap.weights_pct is None
        assert snap.graphs_pct is None


def test_note_tracks_the_latest_loading_submessage():
    assert LlamaCppStartupTracker().update(upto("llama threadpool init")).note == "thread pool initialized"
    assert LlamaCppStartupTracker().update(upto("initializing, n_slots")).note == "allocating kv cache slots"


def test_detail_only_contains_the_fields_that_are_known():
    detail = LlamaCppStartupTracker().update(upto("llama threadpool init")).detail()
    assert set(detail) <= {"weights_pct", "shards", "graphs_pct", "note"}
    assert detail["note"] == "thread pool initialized"
    assert LlamaCppStartupTracker().update("").detail() == {}
