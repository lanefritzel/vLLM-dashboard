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
        max_queue_size: int = DEFAULT_MAX_QUEUE_SIZE,
    ) -> None:
        self._url = url
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
            headers={"Content-Type": "application/json"},
        )
        try:
            with urllib.request.urlopen(req, timeout=SEND_TIMEOUT_SECONDS) as resp:
                ok = 200 <= resp.status < 300
                if not ok:
                    print(f"event_push: non-2xx response kind={item.kind} seq={item.seq} status={resp.status}", flush=True)
                return ok
        except urllib.error.HTTPError as exc:
            # A 400 here almost always means a non-integer deployment_id --
            # the dashboard's receiver only accepts a real `deployments.id`
            # (see DeployRunner._should_push, which exists specifically to
            # stop this from ever being pushed in the first place for a
            # boot-replay's self-generated id). A non-droppable item that
            # keeps hitting this gets retried forever by the queue below,
            # blocking every real event behind it -- loud logging here is
            # the whole point, not noise.
            # TEMPORARY P16a DIAGNOSTIC (round 2) -- this specific 400 is NOT
            # explained by the boot-replay/UUID cause already fixed; remove
            # once this new one is found.
            try:
                detail = exc.read().decode("utf-8", errors="replace")
            except Exception:
                detail = "<could not read body>"
            print(
                f"event_push: HTTPError kind={item.kind} seq={item.seq} status={exc.code} "
                f"body={detail!r} sent_body={body.decode('utf-8', errors='replace')!r}",
                flush=True,
            )
            return False
        except (urllib.error.URLError, OSError, ValueError) as exc:
            print(f"event_push: send failed kind={item.kind} seq={item.seq} exc_type={type(exc).__name__}", flush=True)
            return False
