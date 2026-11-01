"""Atomic state.json read/write, and the pure boot-replay decision logic
(P02). vLLM-only, dumb process manager: never reads Postgres, never resolves
recipes -- it's handed a fully-resolved argv and just remembers it.
"""
from __future__ import annotations

import json
import os
import tempfile
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Optional

DEFAULT_STATE_PATH = Path("/state/state.json")

# Consecutive boot replays that don't reach healthy before the supervisor
# gives up and stays idle -- the backstop against a bad argv + Docker's
# `restart: unless-stopped` becoming an endless crash loop.
RETRY_CAP = 3


@dataclass
class Settings:
    AUTO_START: str = "last_good"  # "last_good" | "off"


@dataclass
class SupervisorState:
    last_argv: Optional[list[str]] = None
    last_good_argv: Optional[list[str]] = None
    last_run_outcome: Optional[str] = None  # "healthy" | "failed" | "crashed" | None
    consecutive_failed_replays: int = 0
    settings: Settings = field(default_factory=Settings)

    def to_dict(self) -> dict:
        return asdict(self)

    @classmethod
    def from_dict(cls, d: dict) -> "SupervisorState":
        settings_dict = d.get("settings") or {}
        return cls(
            last_argv=d.get("last_argv"),
            last_good_argv=d.get("last_good_argv"),
            last_run_outcome=d.get("last_run_outcome"),
            consecutive_failed_replays=d.get("consecutive_failed_replays", 0),
            settings=Settings(AUTO_START=settings_dict.get("AUTO_START", "last_good")),
        )


class StateStore:
    """Reads/writes state.json. Single-process use only (one supervisor per
    container) -- atomicity here is against a hard kill mid-write, via a
    write-to-temp-file + os.replace (atomic on POSIX), not against concurrent
    writers."""

    def __init__(self, path: Path | str = DEFAULT_STATE_PATH) -> None:
        self.path = Path(path)

    def exists(self) -> bool:
        return self.path.exists()

    def load(self) -> Optional[SupervisorState]:
        if not self.path.exists():
            return None
        with self.path.open("r", encoding="utf-8") as f:
            return SupervisorState.from_dict(json.load(f))

    def save(self, state: SupervisorState) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        fd, tmp_path = tempfile.mkstemp(dir=str(self.path.parent), prefix=".state-", suffix=".tmp")
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                json.dump(state.to_dict(), f, indent=2)
                f.flush()
                os.fsync(f.fileno())
            os.replace(tmp_path, self.path)
        except Exception:
            try:
                os.unlink(tmp_path)
            except OSError:
                pass
            raise


def normalize_argv(argv: list[str]) -> list[str]:
    """The cutover compose drops `entrypoint: ["vllm"]` (it would override
    the supervisor's own), so a `command` that used to start at "serve"
    arrives without its "vllm" prefix. Prepend it back -- but ONLY when the
    first element is literally "serve"; any other first element (e.g.
    "python", for test doubles) is used as-is, so tests can pass through
    their own command unchanged."""
    if argv and argv[0] == "serve":
        return ["vllm", *argv]
    return list(argv)


@dataclass
class BootDecision:
    should_replay: bool
    argv: Optional[list[str]]
    reason: str


def decide_boot_action(
    state: Optional[SupervisorState], fallback_argv: Optional[list[str]]
) -> tuple[BootDecision, SupervisorState]:
    """Pure decision logic for container start. `state` is whatever
    StateStore.load() returned (None if state.json didn't exist yet);
    `fallback_argv` is the compose `command`, passed through by entrypoint.sh,
    used only when there's no state file at all. Returns the decision plus
    the state that should be persisted next (unchanged, or newly created on
    first boot -- never mutated for a replay decision itself; the caller
    records the actual outcome after trying to spawn)."""
    if state is None:
        if not fallback_argv:
            return (
                BootDecision(False, None, "no state file and no fallback argv -- staying idle"),
                SupervisorState(),
            )
        argv = normalize_argv(fallback_argv)
        return (
            BootDecision(True, argv, "no state file -- using the compose command as the initial argv"),
            SupervisorState(last_argv=argv),
        )

    if state.settings.AUTO_START == "off":
        return BootDecision(False, None, "AUTO_START=off"), state

    if state.last_run_outcome != "healthy":
        return (
            BootDecision(False, None, f"last run outcome was {state.last_run_outcome!r}, not healthy -- staying idle"),
            state,
        )

    if state.consecutive_failed_replays >= RETRY_CAP:
        return (
            BootDecision(False, None, f"retry cap ({RETRY_CAP}) already reached -- staying idle"),
            state,
        )

    if not state.last_argv:
        return BootDecision(False, None, "no last_argv recorded -- staying idle"), state

    return BootDecision(True, state.last_argv, "AUTO_START=last_good and last run was healthy -- replaying"), state
