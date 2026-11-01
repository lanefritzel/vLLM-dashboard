"""Pushes state transitions, failure reports and batched log lines to the
dashboard (P03c) -- never blocking the state machine. Everything goes
through a bounded in-memory queue drained by a background sender task with
retry/exponential backoff; the state machine only ever calls `push()`,
which is synchronous and non-blocking (append to a deque, return).
Disabled cleanly -- no queue, no background task, no network calls at all
-- when `url` is falsy (DASHBOARD_EVENT_URL unset in the compose).
"""
from __future__ import annotations

import asyncio
import json
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Optional

DEFAULT_MAX_QUEUE_SIZE = 1000
DEFAULT_LOG_BATCH_INTERVAL_SECONDS = 1.0
INITIAL_BACKOFF_SECONDS = 1.0
MAX_BACKOFF_SECONDS = 30.0
SEND_TIMEOUT_SECONDS = 5.0


@dataclass
class PushItem:
    deployment_id: str
    seq: int
    ts: float
    kind: str  # "state" | "report" | "log"
    payload: dict
    droppable: bool  # True only for "log" -- state/report are never dropped

    def to_dict(self) -> dict:
        return {
            "deployment_id": self.deployment_id,
            "seq": self.seq,
            "ts": self.ts,
            "kind": self.kind,
            **self.payload,
        }


class EventPusher:
    def __init__(
        self,
        url: Optional[str],
        token: Optional[str],
        max_queue_size: int = DEFAULT_MAX_QUEUE_SIZE,
    ) -> None:
        self._url = url
        self._token = token
        self._max_queue_size = max_queue_size
        self._queue: list[PushItem] = []
        self._wake: Optional[asyncio.Event] = None
        self._task: Optional[asyncio.Task] = None

    @property
    def enabled(self) -> bool:
        return bool(self._url)

    def start(self) -> None:
        """Must be called from inside a running event loop (the sender task
        needs one) -- a no-op when disabled."""
        if not self.enabled or self._task is not None:
            return
        self._wake = asyncio.Event()
        self._task = asyncio.create_task(self._run())

    def push(self, item: PushItem) -> None:
        if not self.enabled:
            return
        if len(self._queue) >= self._max_queue_size:
            self._make_room_for(item)
        else:
            self._queue.append(item)
        if self._wake is not None:
            self._wake.set()

    def _make_room_for(self, incoming: PushItem) -> None:
        """At capacity: evict the OLDEST droppable ("log") item anywhere in
        the queue to make room, even for an incoming state/report item.
        If nothing droppable exists, the incoming item is dropped instead
        if it's itself droppable; a non-droppable incoming item is never
        dropped -- the queue grows past the cap rather than lose a state
        or report event."""
        for i, existing in enumerate(self._queue):
            if existing.droppable:
                del self._queue[i]
                self._queue.append(incoming)
                return
        if not incoming.droppable:
            self._queue.append(incoming)

    async def _run(self) -> None:
        assert self._wake is not None
        backoff = INITIAL_BACKOFF_SECONDS
        while True:
            if not self._queue:
                self._wake.clear()
                await self._wake.wait()
                continue
            item = self._queue[0]
            sent = await asyncio.to_thread(self._send, item)
            if sent:
                if self._queue and self._queue[0] is item:
                    self._queue.pop(0)
                backoff = INITIAL_BACKOFF_SECONDS
            else:
                await asyncio.sleep(backoff)
                backoff = min(backoff * 2, MAX_BACKOFF_SECONDS)

    def _send(self, item: PushItem) -> bool:
        body = json.dumps(item.to_dict()).encode("utf-8")
        req = urllib.request.Request(
            self._url,
            data=body,
            method="POST",
            headers={
                "Content-Type": "application/json",
                "Authorization": f"Bearer {self._token}" if self._token else "",
            },
        )
        try:
            with urllib.request.urlopen(req, timeout=SEND_TIMEOUT_SECONDS) as resp:
                return 200 <= resp.status < 300
        except (urllib.error.URLError, OSError, ValueError):
            return False
