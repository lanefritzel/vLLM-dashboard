"""The admin API (P02 + P03a). No token/auth of any kind -- the server this
binds to (supervisor.py) only ever listens on 127.0.0.1, so this is never
reachable from anywhere but the same host the dashboard itself runs on.
Never served on the vLLM port.
"""
from __future__ import annotations

import time
from typing import Optional

from fastapi import APIRouter, FastAPI, HTTPException
from pydantic import BaseModel

from .deploy_runner import DeployInProgressError, DeployRunner, NoDeployInProgressError, NotServingError
from .downloads import DownloadInProgressError, DownloadManager, DownloadNotFoundError, HfClient, HfRepoError
from .process_manager import ProcessManager
from .state import Settings, StateStore, SupervisorState


class DeployRequest(BaseModel):
    argv: list[str]
    env: Optional[dict[str, str]] = None
    deployment_id: Optional[str] = None
    graceful_timeout: float = 5.0
    health_timeout: float = 600.0
    smoke: Optional[dict] = None  # P06: omitted/null runs with defaults; {"skip": true} turns it off


class SettingsRequest(BaseModel):
    AUTO_START: str


class RestartRequest(BaseModel):
    # P10a: lets the caller correlate this restart with its own id (e.g. a
    # dashboard deployments row), exactly like /admin/deploy's own optional
    # id. Omitted (or no body at all) -- one is generated the usual way.
    deployment_id: Optional[str] = None


class InspectRequest(BaseModel):
    repo_id: str


class StartDownloadRequest(BaseModel):
    repo_id: str
    slug: str
    filename: Optional[str] = None


def create_app(
    process_manager: ProcessManager,
    deploy_runner: DeployRunner,
    state_store: StateStore,
    download_manager: Optional[DownloadManager] = None,
) -> FastAPI:
    # Optional so every existing caller/test that doesn't care about
    # downloads doesn't have to pass one -- falls back to a real client
    # with no token (fine: nothing in the existing test suite calls the
    # download routes at all; supervisor.py always passes its own
    # properly-configured instance explicitly).
    download_manager = download_manager or DownloadManager(HfClient())
    app = FastAPI()
    admin = APIRouter()

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

        # Read from the persisted state, not deploy_runner's in-memory copy:
        # after a restart into idle (P02's "last run wasn't healthy" rule),
        # this is a fresh DeployRunner with no history at all -- the report
        # from before the restart only survives in state.json (P03b DoD:
        # "/admin/status still shows the last report").
        state = state_store.load()
        failure_report = state.last_failure_report if state else None

        return {
            "state": deploy_runner.state,
            "child": child,
            "vllm_healthy": vllm_healthy,
            "exit_code": exit_code,
            "current_deployment_id": deploy_runner.current_deployment_id,
            "failure_report": failure_report,
            "smoke_ok": deploy_runner.last_smoke_ok,
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
                smoke=body.smoke,
            )
        except DeployInProgressError as exc:
            raise HTTPException(status_code=409, detail={"deployment_id": exc.deployment_id}) from exc
        return {"deployment_id": deployment_id}

    @admin.post("/admin/smoke-test")
    async def smoke_test() -> dict:
        # On-demand (P06): only valid while a model is actually serving --
        # run_smoke_test_now() raises NotServingError otherwise, which we
        # translate to 409 + the current state for the caller to display.
        try:
            result = await deploy_runner.run_smoke_test_now()
        except NotServingError as exc:
            raise HTTPException(status_code=409, detail={"state": exc.state}) from exc
        return result.to_dict()

    @admin.post("/admin/deployments/{deployment_id}/cancel")
    async def cancel(deployment_id: str) -> dict:
        # Hard stop, never a restore (P07) -- cancel_deploy() itself
        # decides phase 1 (old model untouched) vs phase 2 (idle) from
        # live process state; a mismatched/stale id or nothing in flight
        # both raise NoDeployInProgressError -> 409 (unlike Stop, Cancel
        # names a specific id, so "nothing to act on" is a caller error).
        try:
            return await deploy_runner.cancel_deploy(deployment_id)
        except NoDeployInProgressError as exc:
            raise HTTPException(status_code=409, detail=str(exc)) from exc

    @admin.post("/admin/stop")
    async def stop() -> dict:
        # Idempotent (P07): stopping an already-idle instance just
        # succeeds. Only conflicts with an in-flight deploy (409 -- cancel
        # that first).
        try:
            return await deploy_runner.stop()
        except DeployInProgressError as exc:
            raise HTTPException(status_code=409, detail={"deployment_id": exc.deployment_id}) from exc

    @admin.post("/admin/restart")
    async def restart(body: RestartRequest = RestartRequest()) -> dict:
        # Stop + start the current argv, no config change (P07) -- only
        # valid while actually serving, same rule as /admin/smoke-test.
        try:
            deployment_id = deploy_runner.restart(deployment_id=body.deployment_id)
        except NotServingError as exc:
            raise HTTPException(status_code=409, detail={"state": exc.state}) from exc
        except DeployInProgressError as exc:
            raise HTTPException(status_code=409, detail={"deployment_id": exc.deployment_id}) from exc
        return {"deployment_id": deployment_id}

    @admin.post("/admin/downloads/inspect")
    def downloads_inspect(body: InspectRequest) -> dict:
        try:
            return download_manager.inspect(body.repo_id)
        except HfRepoError as exc:
            raise HTTPException(status_code=exc.status or 502, detail=str(exc)) from exc

    @admin.post("/admin/downloads")
    def start_download(body: StartDownloadRequest) -> dict:
        try:
            download_id = download_manager.start_download(body.repo_id, body.slug, body.filename)
        except DownloadInProgressError as exc:
            raise HTTPException(status_code=409, detail={"download_id": exc.download_id}) from exc
        return {"download_id": download_id}

    @admin.get("/admin/downloads/{download_id}")
    def get_download(download_id: str) -> dict:
        try:
            return download_manager.get(download_id)
        except DownloadNotFoundError as exc:
            raise HTTPException(status_code=404, detail=str(exc)) from exc

    @admin.delete("/admin/downloads/{download_id}")
    def delete_download(download_id: str) -> dict:
        try:
            download_manager.cancel(download_id)
        except DownloadNotFoundError as exc:
            raise HTTPException(status_code=404, detail=str(exc)) from exc
        return {"status": "cancelled"}

    @admin.get("/admin/deployments/{deployment_id}/events")
    def events(deployment_id: str, after: int = 0) -> list[dict]:
        return [e.to_dict() for e in deploy_runner.events_for(deployment_id, after)]

    @admin.get("/admin/logs")
    def logs(lines: int = 200) -> dict:
        return {"lines": process_manager.logs(lines)}

    @admin.get("/admin/deployments/{deployment_id}/logs")
    def deployment_logs(deployment_id: str, lines: int = 200) -> dict:
        return {"lines": deploy_runner.archive.read_log_tail(deployment_id, lines)}

    @admin.get("/admin/gpu")
    def gpu() -> dict:
        return deploy_runner.gpu_backend.query().to_dict()

    @admin.post("/admin/gpu/recheck")
    def gpu_recheck() -> dict:
        return deploy_runner.redetect_gpu_backend().to_dict()

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
