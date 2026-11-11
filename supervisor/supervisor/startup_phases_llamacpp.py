"""Reads llama.cpp's own startup output and says which phase it is really in.

Spike (multi-engine support proposal): same job as startup_phases.py's StartupTracker, but
for llama-server. Captured for real -- docker run ghcr.io/ggml-org/llama.cpp:server (build
b12242-3b4d6ab3f, CPU-only) serving Qwen2.5-0.5B-Instruct-GGUF, see
tests/fixtures/llamacpp_qwen2.5-0.5b_startup.log -- not guessed from general knowledge.

The finding that matters: vLLM's four-phase startup (starting/loading/warming_up/server_up)
does not hold for llama.cpp. There is no weight-loading percentage (no shard bar) and no
separate warm-up/CUDA-graph-capture phase at all in this build's log -- the whole thing is
starting -> loading -> server_up. weights_pct and graphs_pct are therefore never populated,
by design, the same way LlamaCppMetricsParser never populates KvPct.

  starting   spawn -> "load_model: loading model '<path>'"      (process init, config read)
  loading    "loading model" -> "listening on http://..."       (weights read + slot/KV setup,
                                                                   no percentage available)
  server_up  "listening on http://..."                          (HTTP server actually up)
"""
import re
from dataclasses import dataclass
from typing import Optional

LOAD_START = re.compile(r"load_model: loading model")
SERVER_UP = re.compile(r"listening on http")

STAGES = ("starting", "loading", "server_up")

_NOTES = {
    "loading": [
        (re.compile(r"llama threadpool init"), "thread pool initialized"),
        (re.compile(r"initializing, n_slots"), "allocating kv cache slots"),
    ],
}


@dataclass(frozen=True)
class LlamaCppStartupSnapshot:
    stage: str = "starting"
    weights_pct: Optional[int] = None  # never populated -- no shard-loading percentage exists
    shards: Optional[str] = None  # never populated, same reason
    graphs_pct: Optional[int] = None  # never populated -- no warm-up/graph-capture phase exists
    note: Optional[str] = None

    def detail(self) -> dict:
        out: dict = {}
        if self.weights_pct is not None:
            out["weights_pct"] = self.weights_pct
        if self.shards is not None:
            out["shards"] = self.shards
        if self.graphs_pct is not None:
            out["graphs_pct"] = self.graphs_pct
        if self.note is not None:
            out["note"] = self.note
        return out


def _last_note(stage: str, text: str) -> Optional[str]:
    best_end, best = -1, None
    for pattern, note in _NOTES.get(stage, []):
        last = None
        for last in pattern.finditer(text):
            pass
        if last is not None and last.end() > best_end:
            best_end, best = last.end(), note
    return best


class LlamaCppStartupTracker:
    """Feed it the child's full output text each poll; it returns the current snapshot.

    The stage never moves backwards, same contract as vLLM's StartupTracker.
    """

    def __init__(self) -> None:
        self._stage_idx = 0

    def update(self, text: str) -> LlamaCppStartupSnapshot:
        stage_idx = 0
        if LOAD_START.search(text):
            stage_idx = 1
        if SERVER_UP.search(text):
            stage_idx = 2
        self._stage_idx = max(self._stage_idx, stage_idx)
        stage = STAGES[self._stage_idx]

        return LlamaCppStartupSnapshot(stage=stage, note=_last_note(stage, text))
