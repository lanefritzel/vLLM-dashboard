"""The admin API (P02 + P03a). GET /admin/health needs no token -- every
other /admin/* route requires `Authorization: Bearer <SUPERVISOR_TOKEN>`
(doc 5); missing/wrong gets 401. Never served on the vLLM port.
"""
from __future__ import annotations

import time
from typing import Optional

from fastapi import APIRouter, Depends, FastAPI, HTTPException, Request
from pydantic import BaseModel

from .deploy_runner import DeployInProgressError, DeployRunner
from .process_manager import ProcessManager
from .state import Settings, StateStore, SupervisorState


class DeployRequest(BaseModel):
    argv: list[str]
    env: Optional[dict[str, str]] = None
    deployment_id: Optional[str] = None
    graceful_timeout: float = 5.0
    health_timeout: float = 600.0
    smoke: Optional[dict] = None  # accepted, ignored -- smoke test is P06


class SettingsRequest(BaseModel):
    AUTO_START: str


def _require_token(token: Optional[str]):
    async def dependency(request: Request) -> None:
        expected = f"Bearer {token}" if token else None
        actual = request.headers.get("Authorization")
        if not expected or actual != expected:
            raise HTTPException(status_code=401, detail="missing or invalid bearer token")

    return dependency


def create_app(
    process_manager: ProcessManager,
    deploy_runner: DeployRunner,
    state_store: StateStore,
    supervisor_token: Optional[str],
) -> FastAPI:
    app = FastAPI()
    admin = APIRouter(dependencies=[Depends(_require_token(supervisor_token))])

    @app.get("/admin/health")
    def health() -> dict:
        return {"status": "ok"}

    @admin.get("/admin/status")
    def status() -> dict:
        info = process_manager.info
        child = None
        vllm_healthy = False
        exit_code = None
        if info is not None:
            exit_code = process_manager.exit_code()
            child = {
                "argv": info.argv,
                "pid": info.pid,
                "started_at": info.started_at,
                "uptime": time.time() - info.started_at,
            }
            vllm_healthy = process_manager.is_running() and exit_code is None

        return {
            "state": deploy_runner.state,
            "child": child,
            "vllm_healthy": vllm_healthy,
            "exit_code": exit_code,
            "current_deployment_id": deploy_runner.current_deployment_id,
        }

    @admin.post("/admin/deploy")
    async def deploy(body: DeployRequest) -> dict:
        # Must be `async def`, not a plain `def`: FastAPI runs sync routes in
        # a worker thread, where start_deploy()'s internal
        # asyncio.create_task() call would have no running event loop to
        # attach to. As a coroutine, this runs directly on the event loop
        # that's already serving the request, which create_task() needs.
        try:
            deployment_id = deploy_runner.start_deploy(
                argv=body.argv,
                env=body.env,
                deployment_id=body.deployment_id,
                graceful_timeout=body.graceful_timeout,
                health_timeout=body.health_timeout,
            )
        except DeployInProgressError as exc:
            raise HTTPException(status_code=409, detail={"deployment_id": exc.deployment_id}) from exc
        return {"deployment_id": deployment_id}

    @admin.get("/admin/deployments/{deployment_id}/events")
    def events(deployment_id: str, after: int = 0) -> list[dict]:
        return [e.to_dict() for e in deploy_runner.events_for(deployment_id, after)]

    @admin.get("/admin/settings")
    def get_settings() -> dict:
        state = state_store.load() or SupervisorState()
        return {"AUTO_START": state.settings.AUTO_START}

    @admin.put("/admin/settings")
    def put_settings(body: SettingsRequest) -> dict:
        state = state_store.load() or SupervisorState()
        state.settings = Settings(AUTO_START=body.AUTO_START)
        state_store.save(state)
        return {"AUTO_START": state.settings.AUTO_START}

    app.include_router(admin)
    return app
