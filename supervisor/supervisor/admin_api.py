"""Minimal admin API (P02): GET /admin/health (no auth -- liveness checks
stay simple, per doc 5) and a stub GET /admin/status. Auth and the rest of
the admin surface (deploy/stop/restart/events/...) arrive in P03a.
"""
from __future__ import annotations

from fastapi import FastAPI

from .process_manager import ProcessManager


def create_app(process_manager: ProcessManager) -> FastAPI:
    app = FastAPI()

    @app.get("/admin/health")
    def health() -> dict:
        return {"status": "ok"}

    @app.get("/admin/status")
    def status() -> dict:
        info = process_manager.info
        if info is None:
            return {"pid": None, "argv": None, "started_at": None}
        return {"pid": info.pid, "argv": info.argv, "started_at": info.started_at}

    return app
