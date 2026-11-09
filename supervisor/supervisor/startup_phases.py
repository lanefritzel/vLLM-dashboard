"""Reads vLLM's own startup output and says which phase it is really in.

Why this exists: the supervisor used to flip the UI's "Load weights" step to done about a
second after spawning vLLM. Real vLLM 0.33 output (captured on an RTX 3060, see
tests/fixtures/vllm_0.33_gemma4_awq_startup.log) shows that a startup is four distinct phases,
and only the third one is the weights actually being read:

  starting   spawn -> "Loading model from scratch..."     (~75 s of Python imports / config on a
                                                            slow CPU; nothing about weights yet)
  loading    "Loading model from scratch..." -> "Model loading took N GiB memory and S seconds"
             (shard bar "Loading safetensors checkpoint shards: P% Completed | i/n" in between)
  warming_up "Model loading took" -> "Starting vLLM server on ..."
             (profiling, KV-cache sizing, multimodal warmup, CUDA-graph capture when enabled --
              40 s for the Gemma 4 run, none of it weight loading)
  server_up  "Starting vLLM server on ..." / "Application startup complete" -> /health

Only the log text is used (the supervisor's ring buffer), so it works the same on the Intel and
NVIDIA images as long as vLLM prints these lines; a stage whose marker never appears is simply
never reported, rather than being faked.
"""
import re
from dataclasses import dataclass
from typing import Optional

# Stage markers (first match wins for the stage; later markers can only move the stage forward).
LOAD_START = re.compile(r"Loading model from scratch|Starting to load model")
SHARD_BAR = re.compile(r"Loading safetensors checkpoint shards:\s+(\d+)% Completed\s*\|\s*(\d+)/(\d+)")
LOAD_DONE = re.compile(r"Model loading took [^\n]*? seconds")
SERVER_UP = re.compile(r"Starting vLLM server on|Application startup complete")
GRAPH_BAR = re.compile(r"Capturing CUDA graphs[^\n]*?(\d+)%\|")

STAGES = ("starting", "loading", "warming_up", "server_up")

# Human-readable sub-phase notes. For each stage, (pattern, text); the one that appears LAST in
# the log wins, so the note tracks what vLLM printed most recently.
_NOTES = {
    "starting": [
        (re.compile(r"Resolved architecture"), "model config read"),
        (re.compile(r"Initializing a V1 LLM engine"), "engine core starting"),
        (re.compile(r"Using .* backend"), "selecting kernels"),
    ],
    "loading": [
        (re.compile(r"Initializing model took"), "model allocated, reading weights"),
        (re.compile(r"Loading weights took"), "weights read, finishing up"),
    ],
    "warming_up": [
        (re.compile(r"Encoder cache will be initialized"), "profiling the multimodal encoder"),
        (re.compile(r"Multi-modal warmup completed"), "multimodal warmup done"),
        (re.compile(r"Available KV cache memory"), "KV cache sized"),
        (re.compile(r"Capturing CUDA graphs"), "capturing CUDA graphs"),
        (re.compile(r"Graph capturing finished"), "CUDA graphs captured"),
        (re.compile(r"init engine \([^)]*\) took"), "engine initialised"),
    ],
}


@dataclass(frozen=True)
class StartupSnapshot:
    stage: str = "starting"
    weights_pct: Optional[int] = None  # shard bar, only while/after loading
    shards: Optional[str] = None  # "1/2"
    graphs_pct: Optional[int] = None
    note: Optional[str] = None

    def detail(self) -> dict:
        """The event `detail` for this snapshot (only the fields that are known)."""
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


class StartupTracker:
    """Feed it the child's full output text each poll; it returns the current snapshot.

    The stage never moves backwards, even if the ring buffer later drops old lines.
    """

    def __init__(self) -> None:
        self._stage_idx = 0

    def update(self, text: str) -> StartupSnapshot:
        shard_matches = list(SHARD_BAR.finditer(text))
        stage_idx = 0
        if LOAD_START.search(text) or shard_matches:
            stage_idx = 1
        if LOAD_DONE.search(text):
            stage_idx = 2
        if SERVER_UP.search(text):
            stage_idx = 3
        self._stage_idx = max(self._stage_idx, stage_idx)
        stage = STAGES[self._stage_idx]

        weights_pct = shards = None
        if shard_matches:
            last = shard_matches[-1]
            weights_pct, shards = int(last.group(1)), f"{last.group(2)}/{last.group(3)}"
        if self._stage_idx >= 2 and weights_pct is not None:
            weights_pct = 100  # the load finished; a missed final bar update must not leave it at 50

        graphs_pct = None
        graph_matches = list(GRAPH_BAR.finditer(text))
        if graph_matches and self._stage_idx >= 2:
            graphs_pct = int(graph_matches[-1].group(1))

        return StartupSnapshot(
            stage=stage, weights_pct=weights_pct, shards=shards, graphs_pct=graphs_pct,
            note=_last_note(stage, text),
        )
