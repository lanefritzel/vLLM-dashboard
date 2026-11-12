"""Per-engine behaviour the supervisor needs, looked up by the ENGINE env var (P2).

One place, so DeployRunner and the admin API never hardcode vLLM: which startup tracker
reads the child's output, which argv flag carries the served model name, and what the
status block reports. Default is "vllm" so every existing deployment is unchanged.
"""
from __future__ import annotations

import json
import urllib.error
import urllib.request
from typing import Mapping, Optional

from .startup_phases import StartupTracker
from .startup_phases_llamacpp import LlamaCppStartupTracker

VLLM = "vllm"
LLAMACPP = "llamacpp"
ENGINES = (VLLM, LLAMACPP)

_TRACKERS = {VLLM: StartupTracker, LLAMACPP: LlamaCppStartupTracker}
_SERVED_NAME_FLAG = {VLLM: "--served-model-name", LLAMACPP: "--alias"}


def engine_from_env(env: Mapping[str, str]) -> str:
    value = (env.get("ENGINE") or VLLM).strip().lower()
    if value not in ENGINES:
        raise ValueError(f"ENGINE={value!r} is not one of {', '.join(ENGINES)}")
    return value


def tracker_for(engine: str):
    """A fresh tracker (they hold per-deploy state)."""
    return _TRACKERS[engine]()


def extract_value(argv: list[str], flag: str) -> Optional[str]:
    """The token after `flag` in argv, or None. The one argv-flag lookup the supervisor uses."""
    if flag in argv:
        idx = argv.index(flag)
        if idx + 1 < len(argv):
            return argv[idx + 1]
    return None


def served_model_name(argv: list[str], engine: str) -> Optional[str]:
    return extract_value(argv, _SERVED_NAME_FLAG[engine])


def fetch_version(engine: str, port: int, timeout: float = 0.5) -> Optional[str]:
    """The running child's own build string, or None if it can't be read (not up yet, no such
    endpoint). llama-server reports it as `build_info` on GET /props (confirmed against a real
    build: "b12242-3b4d6ab3f"); vLLM's source isn't checked yet, so it stays None."""
    if engine != LLAMACPP:
        return None
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/props", timeout=timeout) as resp:
            payload = json.loads(resp.read())
        value = payload.get("build_info") if isinstance(payload, dict) else None  # a non-object body is "unreadable"
        return value if isinstance(value, str) and value else None
    except (urllib.error.URLError, OSError, ValueError, TimeoutError):
        return None


def engine_info(engine: str, version: Optional[str] = None) -> dict:
    return {"name": engine, "version": version}
