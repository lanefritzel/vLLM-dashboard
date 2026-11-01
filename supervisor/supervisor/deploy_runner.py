"""The deploy state machine (P03a): idle -> stopping -> releasing_gpu ->
starting -> loading -> waiting_health -> ready. Happy path only -- failure
classification (reason codes, structured report) is P03b; this just records
a plain "failed" transition when the health check times out. One deploy in
flight at a time: a second attempt while one is running is rejected with
the in-flight deployment's id, not queued.

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

from .process_manager import ProcessManager
from .state import StateStore, SupervisorState

RELEASING_GPU_WAIT_SECONDS = 2.0
HEALTH_POLL_INTERVAL_SECONDS = 1.0
DEFAULT_CHILD_PORT = 9999


@dataclass
class DeploymentEvent:
    deployment_id: str
    seq: int
    ts: float
    state: str
    detail: Optional[str] = None

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
    def __init__(self, process_manager: ProcessManager, state_store: StateStore) -> None:
        self._process_manager = process_manager
        self._state_store = state_store
        self._state = "idle"
        self._current_deployment_id: Optional[str] = None
        self._events: dict[str, list[DeploymentEvent]] = {}
        self._seq_counters: dict[str, "itertools.count[int]"] = {}

    @property
    def state(self) -> str:
        return self._state

    @property
    def current_deployment_id(self) -> Optional[str]:
        return self._current_deployment_id

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
        deployment_id = deployment_id or str(uuid.uuid4())
        self._current_deployment_id = deployment_id
        self._events[deployment_id] = []
        self._seq_counters[deployment_id] = itertools.count(1)
        return deployment_id

    def _record(self, deployment_id: str, state: str, detail: Optional[str] = None) -> None:
        counter = self._seq_counters[deployment_id]
        event = DeploymentEvent(deployment_id=deployment_id, seq=next(counter), ts=time.time(), state=state, detail=detail)
        self._events[deployment_id].append(event)
        self._state = state

    async def _run_deploy(
        self,
        argv: list[str],
        deployment_id: str,
        env: Optional[dict[str, str]],
        graceful_timeout: float,
        health_timeout: float,
    ) -> bool:
        try:
            if self._process_manager.is_running():
                self._record(deployment_id, "stopping")
                await asyncio.to_thread(self._process_manager.stop, graceful_timeout)
                self._record(deployment_id, "releasing_gpu")
                await asyncio.sleep(RELEASING_GPU_WAIT_SECONDS)

            self._record(deployment_id, "starting")
            await asyncio.to_thread(self._process_manager.spawn, argv, env)
            self._record(deployment_id, "loading")
            self._record(deployment_id, "waiting_health")

            served_model_name = _extract_value(argv, "--served-model-name")
            port = _extract_port(argv)
            healthy = await asyncio.to_thread(_wait_for_health, port, served_model_name, health_timeout)

            if not healthy:
                self._record(deployment_id, "failed", "did not become healthy within health_timeout")
                return False

            self._record(deployment_id, "ready")
            state = self._state_store.load() or SupervisorState()
            state.last_argv = argv
            state.last_good_argv = argv
            state.last_run_outcome = "healthy"
            state.consecutive_failed_replays = 0
            self._state_store.save(state)
            return True
        finally:
            self._current_deployment_id = None


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


def _wait_for_health(port: int, served_model_name: Optional[str], timeout: float) -> bool:
    """Child alive (GET /health 200) and, when the argv names a served
    model, that name actually shows up in GET /v1/models -- not just that
    SOME model is listed (doc 5: "poll ... until the served model name ...
    appears")."""
    deadline = time.monotonic() + timeout
    health_url = f"http://127.0.0.1:{port}/health"
    models_url = f"http://127.0.0.1:{port}/v1/models"
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(health_url, timeout=2) as resp:
                if resp.status != 200:
                    time.sleep(HEALTH_POLL_INTERVAL_SECONDS)
                    continue
            if served_model_name is None:
                return True
            with urllib.request.urlopen(models_url, timeout=2) as resp:
                data = json.loads(resp.read())
            names = [m.get("id") for m in data.get("data", [])]
            if served_model_name in names:
                return True
        except (urllib.error.URLError, ConnectionError, TimeoutError, OSError, ValueError):
            pass
        time.sleep(HEALTH_POLL_INTERVAL_SECONDS)
    return False
