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
import threading
import time
import urllib.error
import urllib.request
import uuid
from dataclasses import dataclass
from typing import Optional

from .event_push import EventPusher, PushItem
from .failure_report import FailureReport, build_report
from .gpu_backend import GpuBackend, GpuInfo, detect_backend
from .log_store import DeploymentArchive
from .process_manager import ProcessManager
from .smoke_test import SmokeResult, resolve_smoke_config, run_smoke_test
from .engines import VLLM, engine_info, extract_value, fetch_version, served_model_name as extract_served_model_name, tracker_for
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
HEALTH_HEARTBEAT_INTERVAL_SECONDS = 10.0
CRASH_WATCH_POLL_INTERVAL_SECONDS = 1.0
LOG_BATCH_INTERVAL_SECONDS = 1.0
DEFAULT_CHILD_PORT = 9999
STARTUP_WATCH_INTERVAL_SECONDS = 1.0
# GET /admin/status is polled often; an unreadable child /props backs off 2s, 4s, ... up to 30s.
VERSION_RETRY_INITIAL_SECONDS = 2.0
VERSION_RETRY_MAX_SECONDS = 30.0


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


class NotServingError(Exception):
    """POST /admin/smoke-test or /admin/restart with no model actually
    serving (P06/P07) -- carries the current state so the caller can
    report it (409 + state)."""

    def __init__(self, state: str) -> None:
        super().__init__(f"no model is currently serving (state={state!r})")
        self.state = state


class NoDeployInProgressError(Exception):
    """POST /admin/deployments/{id}/cancel with nothing in flight (P07) --
    unlike Stop (idempotent: stopping an already-idle instance just
    succeeds), Cancel names a specific deployment id to act on, so with
    none in flight it's a genuine caller error -> 409."""


class DeployRunner:
    def __init__(
        self,
        process_manager: ProcessManager,
        state_store: StateStore,
        event_pusher: Optional[EventPusher] = None,
        gpu_backend: Optional[GpuBackend] = None,
        archive: Optional[DeploymentArchive] = None,
        engine: str = VLLM,
    ) -> None:
        self._engine = engine
        self._version_pid: Optional[int] = None
        self._version: Optional[str] = None
        self._version_next_try = 0.0
        self._version_delay = VERSION_RETRY_INITIAL_SECONDS
        self._process_manager = process_manager
        self._state_store = state_store
        self._event_pusher = event_pusher or EventPusher(url=None)
        self._gpu_backend = gpu_backend or GpuBackend()  # the base class IS the "generic" no-op backend
        self._archive = archive or DeploymentArchive()
        self._state = "idle"
        self._current_deployment_id: Optional[str] = None
        self._events: dict[str, list[DeploymentEvent]] = {}
        self._seq_counters: dict[str, "itertools.count[int]"] = {}
        # True only for a deploy the dashboard itself asked for (a real
        # `deployments.id`, passed in explicitly) -- a boot-replay has no such
        # id and self-generates a UUID (see _begin) purely for internal
        # bookkeeping. Pushing events for a UUID the dashboard never issued
        # is meaningless to it (no row to correlate with) and, worse, its
        # receiver only accepts integer ids and 400s on a UUID -- a 400 on a
        # non-droppable "state" push gets retried forever by EventPusher's
        # strictly-sequential queue, permanently blocking every real event
        # behind it (P16a: confirmed live, root cause of "logs never arrive"
        # across an entire supervisor process's lifetime after any boot
        # replay). Gates every push call site below.
        self._dashboard_initiated: dict[str, bool] = {}
        self._last_failure_report: Optional[FailureReport] = None
        # None until the first deploy's smoke test runs (or forever, if
        # every deploy so far was skipped) -- P06, in-memory only.
        self._last_smoke_ok: Optional[bool] = None
        # Watches the child AFTER a successful deploy, to tell "crashed"
        # (reached ready, died later) from "failed" (never got there) --
        # cancelled at the start of the next deploy, since an intentional
        # stop for a redeploy is not a crash.
        self._post_ready_watcher: Optional[asyncio.Task] = None
        # The in-flight deploy's own asyncio Task (P07) -- Cancel needs a
        # handle to actually interrupt it; cleared alongside
        # _current_deployment_id once the deploy concludes (normally OR via
        # cancellation).
        self._current_deploy_task: Optional[asyncio.Task] = None
        # Lets cancel_deploy() wake a blocked health-poll thread quickly
        # (P07) -- health_timeout defaults to 600s, and an asyncio
        # CancelledError can only be DELIVERED once the to_thread() call
        # it's suspended on actually returns (a blocking call can't be
        # interrupted directly); without this, Cancel during
        # "waiting_health" could silently wait out the whole timeout
        # instead of responding in about a second.
        self._cancel_event: Optional[threading.Event] = None
        # The argv of the in-flight deploy (P07) -- cancel_deploy() needs
        # this as a fallback for _handle_cancel() when the task never got
        # to run its own body at all (see cancel_deploy's own comment).
        self._current_deploy_argv: Optional[list[str]] = None
        # The env of the most recent deploy attempt (P07's Restart: "same
        # argv, no config change" needs the env too, not just argv --
        # ProcessManager's ChildInfo doesn't carry it). Only meaningful
        # while actually serving; Restart's own NotServingError check
        # guards against reusing a stale value from a failed attempt.
        self._current_env: Optional[dict[str, str]] = None

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

    def redetect_gpu_backend(self) -> GpuInfo:
        """POST /admin/gpu/recheck -- re-runs full vendor detection (not
        just a re-query of whatever backend startup picked), so fixing a
        misdetection doesn't need a supervisor restart. Replaces
        `_gpu_backend` in place so every other caller (deploy/stop's
        VRAM-release wait) picks up the new backend immediately too."""
        self._gpu_backend = detect_backend(self._process_manager)
        return self._gpu_backend.query()

    @property
    def archive(self) -> DeploymentArchive:
        return self._archive

    @property
    def engine(self) -> str:
        return self._engine

    @property
    def engine_info(self) -> dict:
        """{name, version} for GET /admin/status. The version is read from the running child
        (cached once read, per pid; a failed read backs off before the next probe); None while
        nothing is serving or the version can't be read."""
        info = self._process_manager.info
        if info is not None and self._process_manager.is_running():
            if self._version_pid != info.pid:  # a new child: forget the old one's result and backoff
                self._version_pid, self._version = info.pid, None
                self._version_next_try, self._version_delay = 0.0, VERSION_RETRY_INITIAL_SECONDS
            if self._version is None and _now() >= self._version_next_try:
                self._version = fetch_version(self._engine, _extract_port(info.argv))
                if self._version is None:
                    self._version_next_try = _now() + self._version_delay
                    self._version_delay = min(self._version_delay * 2, VERSION_RETRY_MAX_SECONDS)
            return engine_info(self._engine, self._version)
        return engine_info(self._engine)

    @property
    def last_smoke_ok(self) -> Optional[bool]:
        return self._last_smoke_ok

    def events_for(self, deployment_id: str, after: int = 0) -> list[DeploymentEvent]:
        if deployment_id in self._events:
            events = self._events[deployment_id]
        else:
            # Not in this process's memory -- e.g. a past deployment from
            # before a supervisor restart. Load from the persisted archive
            # (P05 DoD: "events?after=<seq> for a past deployment still
            # returns the right events" after a restart).
            events = [DeploymentEvent(**d) for d in self._archive.load_events(deployment_id)]
        return [e for e in events if e.seq > after]

    def start_deploy(
        self,
        argv: list[str],
        env: Optional[dict[str, str]] = None,
        deployment_id: Optional[str] = None,
        graceful_timeout: float = 5.0,
        health_timeout: float = 600.0,
        smoke: Optional[dict] = None,
    ) -> str:
        """Admits the deploy synchronously (so a caller gets an immediate
        409-equivalent if one's already running) and schedules the actual
        work as a background task. Returns the deployment id right away."""
        deployment_id = self._begin(deployment_id)
        self._current_deploy_argv = argv
        self._current_deploy_task = asyncio.create_task(
            self._run_deploy(argv, deployment_id, env, graceful_timeout, health_timeout, smoke)
        )
        return deployment_id

    async def deploy_and_wait(
        self,
        argv: list[str],
        env: Optional[dict[str, str]] = None,
        deployment_id: Optional[str] = None,
        graceful_timeout: float = 5.0,
        health_timeout: float = 600.0,
        smoke: Optional[dict] = None,
    ) -> bool:
        """Same admission + state machine, but awaited inline -- for the
        boot sequence, which needs to know the outcome before deciding
        whether to retry (P02's retry cap)."""
        deployment_id = self._begin(deployment_id)
        self._current_deploy_argv = argv
        task = asyncio.create_task(
            self._run_deploy(argv, deployment_id, env, graceful_timeout, health_timeout, smoke)
        )
        self._current_deploy_task = task
        try:
            return await task
        except asyncio.CancelledError:
            return False

    def _begin(self, deployment_id: Optional[str]) -> str:
        if self._current_deployment_id is not None:
            raise DeployInProgressError(self._current_deployment_id)
        if self._post_ready_watcher is not None:
            self._post_ready_watcher.cancel()
            self._post_ready_watcher = None
        self._event_pusher.start()  # no-op if disabled or already started
        was_dashboard_initiated = deployment_id is not None
        deployment_id = deployment_id or str(uuid.uuid4())
        self._dashboard_initiated[deployment_id] = was_dashboard_initiated
        self._current_deployment_id = deployment_id
        self._events[deployment_id] = []
        self._seq_counters[deployment_id] = itertools.count(1)
        self._archive.register_deployment(deployment_id)  # rotates off the oldest past the 50-deployment cap (P05)
        return deployment_id

    def _next_seq(self, deployment_id: str) -> int:
        # Shared across state/report/log pushes for a deployment (doc 5:
        # "seq increases monotonically per deployment across all kinds").
        return next(self._seq_counters[deployment_id])

    def _should_push(self, deployment_id: str) -> bool:
        """False for a boot-replay's self-generated UUID id (see _begin) --
        the dashboard never issued it, has no row to correlate it with, and
        (confirmed live, P16a) its receiver 400s on a non-integer id, which
        for a non-droppable push gets retried forever and deadlocks every
        real event queued behind it for the rest of this process's life."""
        return self._dashboard_initiated.get(deployment_id, True)

    def _record(self, deployment_id: str, state: str, detail: Optional[dict] = None) -> None:
        now = time.time()
        seq = self._next_seq(deployment_id)
        event = DeploymentEvent(deployment_id=deployment_id, seq=seq, ts=now, state=state, detail=detail)
        self._events[deployment_id].append(event)
        self._archive.append_event(deployment_id, event.to_dict())  # P05: survives a restart
        self._state = state
        if self._should_push(deployment_id):
            self._event_pusher.push(PushItem(
                deployment_id=deployment_id, seq=seq, ts=now, kind="state",
                payload={"state": state, "detail": detail}, droppable=False,
            ))

    def _push_report(self, deployment_id: str, report: FailureReport) -> None:
        # A separate push, distinct from the state event above (doc 5:
        # kind "report" -> {report:{reason, exit_code, log_tail}}, sent
        # alongside failed/crashed) -- the GET /events catch-up API still
        # only needs the state event's own `detail` (unchanged, P03a/b).
        if not self._should_push(deployment_id):
            return
        self._event_pusher.push(PushItem(
            deployment_id=deployment_id, seq=self._next_seq(deployment_id), ts=time.time(), kind="report",
            payload={"report": report.to_dict()}, droppable=False,
        ))

    async def _run_log_batch(self, deployment_id: str) -> None:
        """Batches the child's new output lines about once a second while
        a deploy is in flight (doc 5) -- started right after spawn,
        cancelled once the deploy concludes (ready or failed). Reads from
        ProcessManager's own drain buffer (fed by its one pipe-reading
        thread) rather than the pipe itself -- never a second reader of
        the same pipe. Also writes the same lines to this deployment's
        capped archive log file (P05) -- one drain, two sinks, so the
        pipe is still only ever read once.
        """
        try:
            while True:
                await asyncio.sleep(LOG_BATCH_INTERVAL_SECONDS)
                lines = self._process_manager.drain_pending_log_lines()
                if lines:
                    for line in lines:
                        self._archive.append_log_line(deployment_id, line)
                    if self._should_push(deployment_id):
                        self._event_pusher.push(PushItem(
                            deployment_id=deployment_id, seq=self._next_seq(deployment_id), ts=time.time(), kind="log",
                            payload={"lines": lines}, droppable=True,
                        ))
        except asyncio.CancelledError:
            pass

    async def _watch_startup(
        self, deployment_id: str, port: int, max_wait: float, cancel_event: Optional[threading.Event] = None,
    ) -> float:
        """Reports vLLM's REAL startup progress while it comes up (see startup_phases.py for the
        phases and the real log they were taken from): "starting" until it prints that it is
        loading the model, "loading" with the shard-bar percentage while weights are read,
        "warming_up" (profiling, KV cache, CUDA graphs) after "Model loading took", and it
        returns once vLLM says its server is starting.

        Reads the child's output through ProcessManager.logs() -- the same non-destructive
        ring-buffer read GET /admin/logs uses, never a second reader of the pipe and never
        interfering with _run_log_batch's own drain. A stage is only reported when its marker
        actually appears in the output; nothing is faked, so a server that prints none of the
        markers (the fake-vllm test double, or a vLLM version with different wording) simply
        stays in "starting" until /health answers.

        Also probes /health each pass and returns the moment it succeeds, and returns if the
        process exits, the deploy is cancelled, or `max_wait` elapses. Returns the seconds spent
        here so the caller can give the final health wait only what is left of the budget.
        """
        tracker = tracker_for(self._engine)
        last_key = None
        start = time.monotonic()
        health_url = f"http://127.0.0.1:{port}/health"
        while time.monotonic() - start < max_wait:
            if not self._process_manager.is_running():
                break
            if cancel_event is not None and cancel_event.is_set():
                break
            snap = tracker.update(self._process_manager.logs())
            if snap.stage == "server_up":
                break
            key = (snap.stage, snap.weights_pct, snap.graphs_pct, snap.note)
            if key != last_key:
                first = last_key is None
                last_key = key
                # "starting" with nothing to add was already recorded when the process spawned.
                if not (first and snap.stage == "starting" and snap.note is None):
                    self._record(deployment_id, snap.stage, snap.detail() or None)
            if await asyncio.to_thread(_quick_health_check, health_url):
                break
            await asyncio.sleep(STARTUP_WATCH_INTERVAL_SECONDS)
        return time.monotonic() - start

    async def _run_deploy(
        self,
        argv: list[str],
        deployment_id: str,
        env: Optional[dict[str, str]],
        graceful_timeout: float,
        health_timeout: float,
        smoke: Optional[dict] = None,
    ) -> bool:
        log_batch_task: Optional[asyncio.Task] = None
        vram_still_in_use = False
        self._current_env = env
        cancel_event = threading.Event()
        self._cancel_event = cancel_event
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
            served_model_name = extract_served_model_name(argv, self._engine)
            port = _extract_port(argv)
            startup_elapsed = await self._watch_startup(deployment_id, port, health_timeout, cancel_event)
            self._record(deployment_id, "waiting_health")

            # A large model's health wait can run for minutes (confirmed via
            # the real P16a hardware rehearsal -- a 27B model took ~2m38s)
            # with vLLM emitting no event of its own in between. A single
            # blocking asyncio.to_thread(_wait_for_health, ..., health_timeout)
            # call left the UI with nothing to show for that whole span but a
            # static "waiting" dot. _wait_for_health can't call self._record
            # directly -- it runs in a worker thread, and _record (via the
            # event pusher's asyncio.Event) isn't thread-safe to call from
            # there -- so this instead calls it repeatedly with a short
            # sub-timeout, emitting a "waiting_health" heartbeat (with
            # elapsed_seconds) back on the event loop between sub-calls.
            # _watch_startup already spent part of health_timeout watching vLLM
            # start up (and probes /health itself the whole time), so this loop only
            # gets what is left -- one shared budget, with a floor so a deploy that
            # used it all still gets a final health check and a proper failure report.
            health_budget = max(1.0, health_timeout - startup_elapsed)
            healthy = False
            timed_out_while_alive = False
            elapsed = 0.0
            sub_timeout = min(HEALTH_HEARTBEAT_INTERVAL_SECONDS, health_budget)
            while elapsed < health_budget:
                this_timeout = min(sub_timeout, health_budget - elapsed)
                healthy, timed_out_while_alive = await asyncio.to_thread(
                    _wait_for_health, port, served_model_name, this_timeout, self._process_manager.is_running,
                    cancel_event,
                )
                elapsed += this_timeout
                if healthy or not timed_out_while_alive or (cancel_event is not None and cancel_event.is_set()):
                    break
                # Only a heartbeat, not a real transition -- skip it when this
                # was the last sub-wait anyway (elapsed has now reached
                # health_timeout), or a short health_timeout (as in the
                # "fails exactly at the timeout" test) spuriously grows an
                # extra "waiting_health" event between the real one and the
                # terminal "failed" that follows.
                if elapsed < health_budget:
                    self._record(deployment_id, "waiting_health", {"elapsed_seconds": round(elapsed)})

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

            # Automatic smoke test (P06), the final step -- runs by default;
            # only an explicit smoke: {skip: true} turns it off (owner-
            # confirmed 2026-11-01). A FAILED smoke test does not tear down
            # a model that's actually up (warn-and-ready, also owner-
            # confirmed): the deploy still ends in ready, with smoke_ok=
            # false and a "smoke ... fail" event for the UI to act on
            # (e.g. offer Rollback) -- never silently hidden, never used
            # to kill a running server over a bad canned-prompt answer.
            smoke_config = resolve_smoke_config(smoke)
            if smoke_config is not None:
                prompt, expect_substring = smoke_config
                result = await asyncio.to_thread(run_smoke_test, port, served_model_name or "", prompt, expect_substring)
                self._last_smoke_ok = result.ok
                detail = {"outcome": "pass"} if result.ok else {"outcome": "fail", "excerpt": result.excerpt}
                # _record() also sets self._state to whatever "state" string
                # it's given -- fine for a real state-machine transition, but
                # "smoke" is just an event, not a state (warn-and-ready: the
                # deploy's actual state must stay "ready" either way), so
                # restore it right after.
                self._record(deployment_id, "smoke", detail)
                self._state = "ready"
            else:
                self._last_smoke_ok = None

            self._post_ready_watcher = asyncio.create_task(self._watch_for_crash(deployment_id))
            return True
        except asyncio.CancelledError:
            # Cancel (P07): the phase is read from live process state, not
            # a flag set earlier in this function -- asyncio can only
            # deliver the cancellation once the currently-awaited
            # to_thread() call actually returns (a blocking subprocess
            # call can't be interrupted mid-flight), so by the time we get
            # here `self._process_manager.stop(...)` above may have
            # already fully completed even though the CancelledError
            # pre-empted the line right after it -- a flag set only after
            # that await would have been wrong in exactly that window.
            await self._handle_cancel(deployment_id, argv)
            raise
        finally:
            if log_batch_task is not None:
                log_batch_task.cancel()  # only runs "while a deployment is in flight" (doc 5)
                try:
                    await log_batch_task
                except asyncio.CancelledError:
                    pass
                # A fast deploy (ready inside ~1s) can conclude before the
                # batch task's own sleep(1) ever elapses, losing whatever
                # output arrived in that window from the per-deployment
                # archive entirely (found by hand against a real
                # container -- GET .../logs showed only 1 of 3 lines the
                # ring buffer had). One final synchronous drain, after the
                # task is fully stopped (so it can't race this one), makes
                # sure the archive always ends up with everything.
                for line in self._process_manager.drain_pending_log_lines():
                    self._archive.append_log_line(deployment_id, line)
            self._current_deployment_id = None
            self._current_deploy_task = None
            self._cancel_event = None
            self._current_deploy_argv = None

    async def run_smoke_test_now(self) -> SmokeResult:
        """POST /admin/smoke-test (P06): runs against the CURRENT active
        model. Only valid while actually serving -- raises NotServingError
        (-> 409 + state at the API layer) otherwise, same "works only when
        a model is serving" rule the page states."""
        info = self._process_manager.info
        if self._state != "ready" or info is None or not self._process_manager.is_running():
            raise NotServingError(self._state)
        served_model_name = extract_served_model_name(info.argv, self._engine) or ""
        port = _extract_port(info.argv)
        result = await asyncio.to_thread(run_smoke_test, port, served_model_name)
        self._last_smoke_ok = result.ok
        return result

    async def _handle_cancel(self, deployment_id: str, argv: list[str]) -> None:
        """Cancel (P07), phase-detected from live process state rather
        than a flag: "phase 1" (old model untouched, still serving) is
        exactly "a child is running and it's NOT the new argv we were
        deploying" -- if that's true we leave it alone and revert to
        ready. Anything else ("phase 2") -- nothing running at all, or the
        NEW child is the one that's running/loading -- kills whatever's
        there (if anything) and goes idle; no auto-restart, no
        auto-rollback (doc 5/7: fix the recipe and deploy is one model
        load, not two)."""
        info = self._process_manager.info
        old_still_serving = self._process_manager.is_running() and (info is None or info.argv != argv)
        if old_still_serving:
            self._record(deployment_id, "cancelled", {"phase": "pre_stop"})
            self._state = "ready"
            return
        if self._process_manager.is_running():
            await asyncio.to_thread(self._process_manager.stop)
        self._record(deployment_id, "cancelled", {"phase": "post_stop"})
        self._state = "idle"

    async def cancel_deploy(self, deployment_id: str) -> dict:
        if self._current_deployment_id is None or self._current_deploy_task is None:
            raise NoDeployInProgressError("no deploy is currently in flight")
        if deployment_id != self._current_deployment_id:
            raise NoDeployInProgressError(f"deployment {deployment_id!r} is not the in-flight deploy")
        if self._cancel_event is not None:
            self._cancel_event.set()  # wakes a blocked health-poll thread quickly, see __init__'s comment
        task = self._current_deploy_task
        argv = self._current_deploy_argv or []
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass
        # _run_deploy's own except/finally handles cleanup when the task
        # actually got to run -- but cancelling a task before it has EVER
        # run so much as its first line never executes the coroutine's
        # body at all (found by hand: the "cancel immediately after
        # start_deploy, same tick" test left the lock held and no
        # "cancelled" event recorded). This is the fallback for exactly
        # that case: if the lock is still held for OUR deployment_id after
        # awaiting the task, _run_deploy's body never ran, so do the same
        # phase-appropriate cleanup here instead.
        if self._current_deployment_id == deployment_id:
            await self._handle_cancel(deployment_id, argv)
            self._current_deployment_id = None
            self._current_deploy_task = None
            self._cancel_event = None
            self._current_deploy_argv = None
        return {"deployment_id": deployment_id, "state": self._state}

    async def stop(self, graceful_timeout: float = 5.0) -> dict:
        """POST /admin/stop (P07): SIGTERM -> wait -> SIGKILL (exactly
        ProcessManager.stop's own sequence), then verify VRAM release the
        same way a swap does. Idempotent when nothing is running --
        unlike Cancel, Stop names no deployment id, so "already idle" is
        just success, not an error. Conflicts with an active deploy
        though (ask the caller to Cancel that first), same lock as
        start_deploy. Sets last_run_outcome="stopped" (not "healthy"),
        so a reboot's boot-replay (P02) leaves this idle on purpose
        (owner-confirmed 2026-11-01) -- last_good_argv is untouched, so
        "redeploy last working" still works."""
        if self._current_deployment_id is not None:
            raise DeployInProgressError(self._current_deployment_id)
        if self._post_ready_watcher is not None:
            self._post_ready_watcher.cancel()
            self._post_ready_watcher = None

        before = await asyncio.to_thread(self._gpu_backend.query)
        was_running = self._process_manager.is_running()
        if was_running:
            await asyncio.to_thread(self._process_manager.stop, graceful_timeout)
            current = await asyncio.to_thread(self._poll_gpu_release, before)
        else:
            current = before

        released = before.used_mb is None or current.used_mb is None or _vram_released(before.used_mb, current.used_mb)
        self._state = "idle"
        state = self._state_store.load() or SupervisorState()
        state.last_run_outcome = "stopped"
        self._state_store.save(state)
        return {"state": "idle", "vram": {"released": released, "used_mb": current.used_mb, "total_mb": current.total_mb}}

    def restart(self, deployment_id: Optional[str] = None) -> str:
        """POST /admin/restart (P07): stop + start the CURRENT argv, no
        config change -- goes through the normal deploy pipeline (same
        events, same smoke test) via start_deploy, which re-admits through
        _begin() and so already 409s if a deploy is somehow in flight.
        Only valid while actually serving, same rule as run_smoke_test_now.
        `deployment_id` (P10a) lets a caller correlate this restart with
        its own id (e.g. a dashboard's own deployments row), exactly like
        /admin/deploy's own optional id -- omitted, one is generated the
        same way start_deploy always does."""
        info = self._process_manager.info
        if self._state != "ready" or info is None or not self._process_manager.is_running():
            raise NotServingError(self._state)
        return self.start_deploy(list(info.argv), env=self._current_env, deployment_id=deployment_id)

    def _poll_gpu_release(self, before: GpuInfo) -> GpuInfo:
        """The polling loop behind both the deploy swap's GPU-release wait
        and Stop's own vram report (P07): wait ~2s, then poll up to ~10s
        total for VRAM to drop toward `before`; returns whatever reading
        the loop stopped on (released or not) for the caller to interpret
        -- no backend/no baseline short-circuits to `before` itself
        (used_mb None), same "assume released" contract as always."""
        time.sleep(RELEASING_GPU_INITIAL_WAIT_SECONDS)
        if before.used_mb is None:
            return before
        deadline = time.monotonic() + (RELEASING_GPU_MAX_WAIT_SECONDS - RELEASING_GPU_INITIAL_WAIT_SECONDS)
        while True:
            current = self._gpu_backend.query()
            if current.used_mb is None or _vram_released(before.used_mb, current.used_mb):
                return current
            if time.monotonic() >= deadline:
                return current  # still elevated -- proceed anyway, flagged
            time.sleep(RELEASING_GPU_POLL_INTERVAL_SECONDS)

    def _wait_for_gpu_release(self, before: GpuInfo) -> bool:
        """Replaces P03a's blind 2s wait (doc 5): wait ~2s, then read VRAM;
        if still elevated after ~10s total, proceed anyway and return True
        so the caller can flag it in the failure report. No backend, or no
        usable baseline -> wait the initial 2s and assume released."""
        current = self._poll_gpu_release(before)
        if before.used_mb is None or current.used_mb is None:
            return False
        return not _vram_released(before.used_mb, current.used_mb)

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


def _now() -> float:
    return time.monotonic()


def _vram_released(before_mb: int, current_mb: int) -> bool:
    return current_mb <= max(before_mb * RELEASED_FRACTION_THRESHOLD, RELEASED_ABSOLUTE_FLOOR_MB)


def _extract_port(argv: list[str], default: int = DEFAULT_CHILD_PORT) -> int:
    value = extract_value(argv, "--port")
    if value is not None:
        try:
            return int(value)
        except ValueError:
            pass
    return default


def _quick_health_check(health_url: str) -> bool:
    """A single, fast GET /health probe -- used only by _watch_startup
    to bail out early once the server is already responding, so that watcher
    never outlives the real health-wait loop that follows it. Deliberately
    not the served-model-name check _wait_for_health does -- that loop is
    still the real judge of success; this is only "is it worth continuing
    to watch logs for a weight-loading marker, or has the server clearly
    already moved past that point."""
    try:
        with urllib.request.urlopen(health_url, timeout=2) as resp:
            return resp.status == 200
    except (urllib.error.URLError, ConnectionError, TimeoutError, OSError, ValueError):
        return False


def _wait_for_health(
    port: int, served_model_name: Optional[str], timeout: float, is_child_alive,
    cancel_event: Optional[threading.Event] = None,
) -> tuple[bool, bool]:
    """Child alive (GET /health 200) and, when the argv names a served
    model, that name actually shows up in GET /v1/models -- not just that
    SOME model is listed (doc 5: "poll ... until the served model name ...
    appears"). Returns (healthy, timed_out_while_alive) -- the second value
    is what tells the caller "health_timeout" apart from "the child already
    exited" (fail-fast: this returns the moment `is_child_alive()` goes
    false, instead of sleeping out the rest of `timeout`). `cancel_event`
    (P07) lets a pending Cancel wake this up within about a second instead
    of waiting out the rest of `timeout` -- the return value in that case
    is discarded anyway (asyncio delivers the CancelledError it's really
    about once this call returns), so its exact shape doesn't matter."""
    deadline = time.monotonic() + timeout
    health_url = f"http://127.0.0.1:{port}/health"
    models_url = f"http://127.0.0.1:{port}/v1/models"
    while time.monotonic() < deadline:
        if not is_child_alive():
            return False, False
        if cancel_event is not None and cancel_event.is_set():
            return False, True
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
