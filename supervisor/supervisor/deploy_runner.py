"""The deploy state machine (P03a + P03b): idle -> stopping -> releasing_gpu
-> starting -> loading -> waiting_health -> ready, with fail-fast (the
child exiting at any point before ready goes straight to "failed" -- never
waits out health_timeout) and a structured failure report attached to
"failed"/"crashed". One deploy in flight at a time: a second attempt while
one is running is rejected with the in-flight deployment's id, not queued.

Used by both the HTTP POST /admin/deploy handler (fire-and-forget via
start_deploy) and the supervisor's own boot-replay sequence (which awaits
the outcome directly via deploy_and_wait, so it can decide whether to
retry) -- one state machine implementation, two callers.
"""
from __future__ import annotations

import asyncio
import itertools
import json
import time
import urllib.error
import urllib.request
import uuid
from dataclasses import dataclass
from typing import Optional

from .event_push import EventPusher, PushItem
from .failure_report import FailureReport, build_report
from .gpu_backend import GpuBackend, GpuInfo
from .process_manager import ProcessManager
from .state import StateStore, SupervisorState

RELEASING_GPU_INITIAL_WAIT_SECONDS = 2.0
RELEASING_GPU_MAX_WAIT_SECONDS = 10.0
RELEASING_GPU_POLL_INTERVAL_SECONDS = 2.0
# "Released" = VRAM dropped to at most 10% of its pre-stop level, or under
# this many MB outright, whichever is larger -- a judgment call (doc 5
# doesn't give an exact threshold), picked so a near-total drop counts as
# released without needing VRAM to hit exactly zero.
RELEASED_FRACTION_THRESHOLD = 0.1
RELEASED_ABSOLUTE_FLOOR_MB = 100
HEALTH_POLL_INTERVAL_SECONDS = 1.0
CRASH_WATCH_POLL_INTERVAL_SECONDS = 1.0
LOG_BATCH_INTERVAL_SECONDS = 1.0
DEFAULT_CHILD_PORT = 9999


@dataclass
class DeploymentEvent:
    deployment_id: str
    seq: int
    ts: float
    state: str
    detail: Optional[dict] = None

    def to_dict(self) -> dict:
        return {
            "deployment_id": self.deployment_id,
            "seq": self.seq,
            "ts": self.ts,
            "state": self.state,
            "detail": self.detail,
        }


class DeployInProgressError(Exception):
    def __init__(self, deployment_id: str) -> None:
        super().__init__(f"a deploy is already in flight: {deployment_id}")
        self.deployment_id = deployment_id


class DeployRunner:
    def __init__(
        self,
        process_manager: ProcessManager,
        state_store: StateStore,
        event_pusher: Optional[EventPusher] = None,
        gpu_backend: Optional[GpuBackend] = None,
    ) -> None:
        self._process_manager = process_manager
        self._state_store = state_store
        self._event_pusher = event_pusher or EventPusher(url=None, token=None)
        self._gpu_backend = gpu_backend or GpuBackend()  # the base class IS the "generic" no-op backend
        self._state = "idle"
        self._current_deployment_id: Optional[str] = None
        self._events: dict[str, list[DeploymentEvent]] = {}
        self._seq_counters: dict[str, "itertools.count[int]"] = {}
        self._last_failure_report: Optional[FailureReport] = None
        # Watches the child AFTER a successful deploy, to tell "crashed"
        # (reached ready, died later) from "failed" (never got there) --
        # cancelled at the start of the next deploy, since an intentional
        # stop for a redeploy is not a crash.
        self._post_ready_watcher: Optional[asyncio.Task] = None

    @property
    def state(self) -> str:
        return self._state

    @property
    def current_deployment_id(self) -> Optional[str]:
        return self._current_deployment_id

    @property
    def last_failure_report(self) -> Optional[FailureReport]:
        return self._last_failure_report

    @property
    def gpu_backend(self) -> GpuBackend:
        return self._gpu_backend

    def events_for(self, deployment_id: str, after: int = 0) -> list[DeploymentEvent]:
        return [e for e in self._events.get(deployment_id, []) if e.seq > after]

    def start_deploy(
        self,
        argv: list[str],
        env: Optional[dict[str, str]] = None,
        deployment_id: Optional[str] = None,
        graceful_timeout: float = 5.0,
        health_timeout: float = 600.0,
    ) -> str:
        """Admits the deploy synchronously (so a caller gets an immediate
        409-equivalent if one's already running) and schedules the actual
        work as a background task. Returns the deployment id right away."""
        deployment_id = self._begin(deployment_id)
        asyncio.create_task(self._run_deploy(argv, deployment_id, env, graceful_timeout, health_timeout))
        return deployment_id

    async def deploy_and_wait(
        self,
        argv: list[str],
        env: Optional[dict[str, str]] = None,
        deployment_id: Optional[str] = None,
        graceful_timeout: float = 5.0,
        health_timeout: float = 600.0,
    ) -> bool:
        """Same admission + state machine, but awaited inline -- for the
        boot sequence, which needs to know the outcome before deciding
        whether to retry (P02's retry cap)."""
        deployment_id = self._begin(deployment_id)
        return await self._run_deploy(argv, deployment_id, env, graceful_timeout, health_timeout)

    def _begin(self, deployment_id: Optional[str]) -> str:
        if self._current_deployment_id is not None:
            raise DeployInProgressError(self._current_deployment_id)
        if self._post_ready_watcher is not None:
            self._post_ready_watcher.cancel()
            self._post_ready_watcher = None
        self._event_pusher.start()  # no-op if disabled or already started
        deployment_id = deployment_id or str(uuid.uuid4())
        self._current_deployment_id = deployment_id
        self._events[deployment_id] = []
        self._seq_counters[deployment_id] = itertools.count(1)
        return deployment_id

    def _next_seq(self, deployment_id: str) -> int:
        # Shared across state/report/log pushes for a deployment (doc 5:
        # "seq increases monotonically per deployment across all kinds").
        return next(self._seq_counters[deployment_id])

    def _record(self, deployment_id: str, state: str, detail: Optional[dict] = None) -> None:
        now = time.time()
        seq = self._next_seq(deployment_id)
        event = DeploymentEvent(deployment_id=deployment_id, seq=seq, ts=now, state=state, detail=detail)
        self._events[deployment_id].append(event)
        self._state = state
        self._event_pusher.push(PushItem(
            deployment_id=deployment_id, seq=seq, ts=now, kind="state",
            payload={"state": state, "detail": detail}, droppable=False,
        ))

    def _push_report(self, deployment_id: str, report: FailureReport) -> None:
        # A separate push, distinct from the state event above (doc 5:
        # kind "report" -> {report:{reason, exit_code, log_tail}}, sent
        # alongside failed/crashed) -- the GET /events catch-up API still
        # only needs the state event's own `detail` (unchanged, P03a/b).
        self._event_pusher.push(PushItem(
            deployment_id=deployment_id, seq=self._next_seq(deployment_id), ts=time.time(), kind="report",
            payload={"report": report.to_dict()}, droppable=False,
        ))

    async def _run_log_batch(self, deployment_id: str) -> None:
        """Batches the child's new output lines about once a second while
        a deploy is in flight (doc 5) -- started right after spawn,
        cancelled once the deploy concludes (ready or failed)."""
        try:
            while True:
                await asyncio.sleep(LOG_BATCH_INTERVAL_SECONDS)
                lines = self._process_manager.drain_pending_log_lines()
                if lines:
                    self._event_pusher.push(PushItem(
                        deployment_id=deployment_id, seq=self._next_seq(deployment_id), ts=time.time(), kind="log",
                        payload={"lines": lines}, droppable=True,
                    ))
        except asyncio.CancelledError:
            pass

    async def _run_deploy(
        self,
        argv: list[str],
        deployment_id: str,
        env: Optional[dict[str, str]],
        graceful_timeout: float,
        health_timeout: float,
    ) -> bool:
        log_batch_task: Optional[asyncio.Task] = None
        vram_still_in_use = False
        try:
            if self._process_manager.is_running():
                before_gpu = await asyncio.to_thread(self._gpu_backend.query)
                self._record(deployment_id, "stopping")
                await asyncio.to_thread(self._process_manager.stop, graceful_timeout)
                self._record(deployment_id, "releasing_gpu")
                vram_still_in_use = await asyncio.to_thread(self._wait_for_gpu_release, before_gpu)

            self._record(deployment_id, "starting")
            await asyncio.to_thread(self._process_manager.spawn, argv, env)
            log_batch_task = asyncio.create_task(self._run_log_batch(deployment_id))
            self._record(deployment_id, "loading")
            self._record(deployment_id, "waiting_health")

            served_model_name = _extract_value(argv, "--served-model-name")
            port = _extract_port(argv)
            healthy, timed_out_while_alive = await asyncio.to_thread(
                _wait_for_health, port, served_model_name, health_timeout, self._process_manager.is_running,
            )

            if not healthy:
                report = build_report(
                    exit_code=self._process_manager.exit_code(),
                    log_tail=self._process_manager.log_tail(),
                    timed_out_while_alive=timed_out_while_alive,
                    vram_still_in_use=vram_still_in_use,
                )
                if timed_out_while_alive:
                    # health_timeout: the child never exited on its own --
                    # a failed deploy must not leave it running regardless
                    # (DoD: "--fake-slow-start beyond ... health_timeout ->
                    # failed ... child killed").
                    await asyncio.to_thread(self._process_manager.stop)
                self._last_failure_report = report
                self._record(deployment_id, "failed", report.to_dict())
                self._push_report(deployment_id, report)
                state = self._state_store.load() or SupervisorState()
                state.last_argv = argv
                state.last_run_outcome = "failed"
                state.last_failure_report = report.to_dict()
                self._state_store.save(state)
                return False

            self._record(deployment_id, "ready")
            self._last_failure_report = None
            state = self._state_store.load() or SupervisorState()
            state.last_argv = argv
            state.last_good_argv = argv
            state.last_run_outcome = "healthy"
            state.consecutive_failed_replays = 0
            state.last_failure_report = None
            self._state_store.save(state)

            self._post_ready_watcher = asyncio.create_task(self._watch_for_crash(deployment_id))
            return True
        finally:
            if log_batch_task is not None:
                log_batch_task.cancel()  # only runs "while a deployment is in flight" (doc 5)
            self._current_deployment_id = None

    def _wait_for_gpu_release(self, before: GpuInfo) -> bool:
        """Replaces P03a's blind 2s wait (doc 5): wait ~2s, then read VRAM;
        if still elevated after ~10s total, proceed anyway and return True
        so the caller can flag it in the failure report. No backend, or no
        usable baseline -> wait the initial 2s and assume released."""
        time.sleep(RELEASING_GPU_INITIAL_WAIT_SECONDS)
        if before.used_mb is None:
            return False

        deadline = time.monotonic() + (RELEASING_GPU_MAX_WAIT_SECONDS - RELEASING_GPU_INITIAL_WAIT_SECONDS)
        while True:
            current = self._gpu_backend.query()
            if current.used_mb is None or _vram_released(before.used_mb, current.used_mb):
                return False
            if time.monotonic() >= deadline:
                return True  # still elevated -- proceed anyway, flagged
            time.sleep(RELEASING_GPU_POLL_INTERVAL_SECONDS)

    async def _watch_for_crash(self, deployment_id: str) -> None:
        """Runs only after a deploy reaches ready. If the child dies later
        (without an intervening deploy, which would have cancelled this
        task first), that's "crashed" -- doc 5's "it was serving and died",
        distinct from "failed" (never got there)."""
        try:
            while True:
                await asyncio.sleep(CRASH_WATCH_POLL_INTERVAL_SECONDS)
                if not self._process_manager.is_running():
                    report = build_report(
                        exit_code=self._process_manager.exit_code(),
                        log_tail=self._process_manager.log_tail(),
                        timed_out_while_alive=False,
                    )
                    self._last_failure_report = report
                    self._record(deployment_id, "crashed", report.to_dict())
                    self._push_report(deployment_id, report)
                    state = self._state_store.load() or SupervisorState()
                    state.last_run_outcome = "crashed"
                    state.last_failure_report = report.to_dict()
                    self._state_store.save(state)
                    return
        except asyncio.CancelledError:
            pass  # superseded by a new deploy -- not actually a crash


def _vram_released(before_mb: int, current_mb: int) -> bool:
    return current_mb <= max(before_mb * RELEASED_FRACTION_THRESHOLD, RELEASED_ABSOLUTE_FLOOR_MB)


def _extract_value(argv: list[str], flag: str) -> Optional[str]:
    if flag in argv:
        idx = argv.index(flag)
        if idx + 1 < len(argv):
            return argv[idx + 1]
    return None


def _extract_port(argv: list[str], default: int = DEFAULT_CHILD_PORT) -> int:
    value = _extract_value(argv, "--port")
    if value is not None:
        try:
            return int(value)
        except ValueError:
            pass
    return default


def _wait_for_health(
    port: int, served_model_name: Optional[str], timeout: float, is_child_alive,
) -> tuple[bool, bool]:
    """Child alive (GET /health 200) and, when the argv names a served
    model, that name actually shows up in GET /v1/models -- not just that
    SOME model is listed (doc 5: "poll ... until the served model name ...
    appears"). Returns (healthy, timed_out_while_alive) -- the second value
    is what tells the caller "health_timeout" apart from "the child already
    exited" (fail-fast: this returns the moment `is_child_alive()` goes
    false, instead of sleeping out the rest of `timeout`)."""
    deadline = time.monotonic() + timeout
    health_url = f"http://127.0.0.1:{port}/health"
    models_url = f"http://127.0.0.1:{port}/v1/models"
    while time.monotonic() < deadline:
        if not is_child_alive():
            return False, False
        try:
            with urllib.request.urlopen(health_url, timeout=2) as resp:
                if resp.status != 200:
                    time.sleep(HEALTH_POLL_INTERVAL_SECONDS)
                    continue
            if served_model_name is None:
                return True, True
            with urllib.request.urlopen(models_url, timeout=2) as resp:
                data = json.loads(resp.read())
            names = [m.get("id") for m in data.get("data", [])]
            if served_model_name in names:
                return True, True
        except (urllib.error.URLError, ConnectionError, TimeoutError, OSError, ValueError):
            pass
        time.sleep(HEALTH_POLL_INTERVAL_SECONDS)
    return False, is_child_alive()
