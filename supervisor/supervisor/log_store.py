"""Log + event persistence (P05): a bounded-by-bytes ring buffer for the
CURRENT child's output, and a capped log file + persisted event stream per
PAST deployment, rotated together (last 50 kept). Logs/events for the
current process live in memory only (the ring buffer) -- rotation and
restart-survival only matter for the per-deployment archive.
"""
from __future__ import annotations

import json
import threading
from collections import deque
from pathlib import Path
from typing import Optional

DEFAULT_STATE_DIR = Path("/state")
RING_BUFFER_MAX_BYTES = 10 * 1024 * 1024  # ~10MB tail of the current process
DEPLOYMENT_LOG_MAX_BYTES = 10 * 1024 * 1024  # ~10MB cap per deployment's log file
MAX_DEPLOYMENTS_KEPT = 50
DEFAULT_LOG_LINES = 200


class RingBuffer:
    """Bounded by total bytes, not line count -- fed line-by-line by the
    one thread that reads the child's stdout pipe (ProcessManager)."""

    def __init__(self, max_bytes: int = RING_BUFFER_MAX_BYTES) -> None:
        self._max_bytes = max_bytes
        self._lines: deque[str] = deque()
        self._total_bytes = 0
        self._lock = threading.Lock()

    def append(self, line: str) -> None:
        with self._lock:
            self._lines.append(line)
            self._total_bytes += len(line) + 1
            while self._total_bytes > self._max_bytes and len(self._lines) > 1:
                removed = self._lines.popleft()
                self._total_bytes -= len(removed) + 1

    def tail(self, lines: Optional[int] = None) -> str:
        with self._lock:
            data = list(self._lines)
        if lines is not None:
            data = data[-lines:]
        return "\n".join(data)

    def clear(self) -> None:
        with self._lock:
            self._lines.clear()
            self._total_bytes = 0


class DeploymentArchive:
    """One capped log file + one persisted event stream per deployment,
    under the state volume -- survives a supervisor restart. Rotated
    together: the last MAX_DEPLOYMENTS_KEPT deployment ids are kept
    (tracked in a small index file); registering one past the cap deletes
    the oldest deployment's log + events.
    """

    def __init__(self, state_dir: Path = DEFAULT_STATE_DIR) -> None:
        self.logs_dir = state_dir / "logs"
        self.events_dir = state_dir / "events"
        self.index_path = state_dir / "deployment_index.json"
        self.logs_dir.mkdir(parents=True, exist_ok=True)
        self.events_dir.mkdir(parents=True, exist_ok=True)

    def register_deployment(self, deployment_id: str) -> None:
        """Call once per NEW deployment id, before writing anything else
        for it -- adds it to the rotation index, evicting the oldest
        deployment's files if now over the cap."""
        ids = self._load_index()
        if deployment_id in ids:
            return
        ids.append(deployment_id)
        while len(ids) > MAX_DEPLOYMENTS_KEPT:
            oldest = ids.pop(0)
            self._remove_deployment_files(oldest)
        self._save_index(ids)

    def append_log_line(self, deployment_id: str, line: str) -> None:
        path = self._log_path(deployment_id)
        try:
            current_size = path.stat().st_size if path.exists() else 0
        except OSError:
            current_size = 0
        if current_size >= DEPLOYMENT_LOG_MAX_BYTES:
            return  # capped -- stop writing, never grows unbounded
        try:
            with path.open("a", encoding="utf-8") as f:
                f.write(line + "\n")
        except OSError:
            pass

    def read_log_tail(self, deployment_id: str, lines: Optional[int] = None) -> str:
        path = self._log_path(deployment_id)
        if not path.exists():
            return ""
        all_lines = path.read_text(encoding="utf-8").splitlines()
        if lines is not None:
            all_lines = all_lines[-lines:]
        return "\n".join(all_lines)

    def append_event(self, deployment_id: str, event_dict: dict) -> None:
        path = self._events_path(deployment_id)
        try:
            with path.open("a", encoding="utf-8") as f:
                f.write(json.dumps(event_dict) + "\n")
        except OSError:
            pass

    def load_events(self, deployment_id: str) -> list[dict]:
        path = self._events_path(deployment_id)
        if not path.exists():
            return []
        events = []
        for line in path.read_text(encoding="utf-8").splitlines():
            try:
                events.append(json.loads(line))
            except json.JSONDecodeError:
                continue
        return events

    def _load_index(self) -> list[str]:
        try:
            return json.loads(self.index_path.read_text())
        except (OSError, json.JSONDecodeError):
            return []

    def _save_index(self, ids: list[str]) -> None:
        self.index_path.write_text(json.dumps(ids))

    def _remove_deployment_files(self, deployment_id: str) -> None:
        for path in (self._log_path(deployment_id), self._events_path(deployment_id)):
            try:
                path.unlink()
            except OSError:
                pass

    def _log_path(self, deployment_id: str) -> Path:
        return self.logs_dir / f"{deployment_id}.log"

    def _events_path(self, deployment_id: str) -> Path:
        return self.events_dir / f"{deployment_id}.jsonl"
