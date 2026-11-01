"""Auth + HTTP-layer behavior of the admin API (P03a) via FastAPI's
TestClient -- fast, in-process, no real sockets for most of these (the
/admin/deploy happy-path -> real-child tests live in test_deploy_runner.py).
"""
import sys
from pathlib import Path

from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).parent.parent))

from supervisor.admin_api import create_app
from supervisor.deploy_runner import DeployRunner
from supervisor.log_store import DeploymentArchive
from supervisor.process_manager import ProcessManager
from supervisor.state import StateStore, SupervisorState

TOKEN = "test-token"


def new_client(tmp_path) -> TestClient:
    pm = ProcessManager()
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), archive=DeploymentArchive(tmp_path))
    app = create_app(pm, runner, StateStore(tmp_path / "state.json"), TOKEN)
    return TestClient(app)


def auth_headers() -> dict:
    return {"Authorization": f"Bearer {TOKEN}"}


def test_health_needs_no_token(tmp_path):
    client = new_client(tmp_path)
    resp = client.get("/admin/health")
    assert resp.status_code == 200


def test_status_without_token_is_401(tmp_path):
    client = new_client(tmp_path)
    resp = client.get("/admin/status")
    assert resp.status_code == 401


def test_status_with_wrong_token_is_401(tmp_path):
    client = new_client(tmp_path)
    resp = client.get("/admin/status", headers={"Authorization": "Bearer nope"})
    assert resp.status_code == 401


def test_status_with_correct_token_is_200(tmp_path):
    client = new_client(tmp_path)
    resp = client.get("/admin/status", headers=auth_headers())
    assert resp.status_code == 200
    body = resp.json()
    assert body["state"] == "idle"
    assert body["child"] is None


def test_every_admin_route_except_health_requires_auth(tmp_path):
    client = new_client(tmp_path)
    for method, path in [
        ("GET", "/admin/status"),
        ("POST", "/admin/deploy"),
        ("GET", "/admin/deployments/x/events"),
        ("GET", "/admin/settings"),
        ("PUT", "/admin/settings"),
    ]:
        resp = client.request(method, path, json={} if method in ("POST", "PUT") else None)
        assert resp.status_code == 401, f"{method} {path} should 401 without a token"


def test_status_surfaces_a_persisted_failure_report_even_from_a_fresh_runner(tmp_path):
    # P03b DoD: "/admin/status still shows the last report" after a restart
    # -- simulated here by writing state.json directly (as a real failed
    # deploy would) and reading it back through a brand new DeployRunner
    # that never ran that deploy itself.
    store = StateStore(tmp_path / "state.json")
    state = SupervisorState(last_run_outcome="failed")
    state.last_failure_report = {"reason": "bad_args", "exit_code": 2, "log_tail": "error: unrecognized arguments"}
    store.save(state)

    client = new_client(tmp_path)
    resp = client.get("/admin/status", headers=auth_headers())
    assert resp.json()["failure_report"] == state.last_failure_report


def test_settings_round_trips_and_persists_across_a_fresh_store(tmp_path):
    client = new_client(tmp_path)

    resp = client.get("/admin/settings", headers=auth_headers())
    assert resp.json() == {"AUTO_START": "last_good"}

    resp = client.put("/admin/settings", headers=auth_headers(), json={"AUTO_START": "off"})
    assert resp.status_code == 200
    assert resp.json() == {"AUTO_START": "off"}

    # A brand new StateStore pointed at the same file -- simulates reading
    # it after a container restart, not just re-reading the same object.
    reloaded = StateStore(tmp_path / "state.json").load()
    assert reloaded.settings.AUTO_START == "off"


def test_deploy_without_a_body_argv_is_rejected(tmp_path):
    client = new_client(tmp_path)
    resp = client.post("/admin/deploy", headers=auth_headers(), json={})
    assert resp.status_code == 422  # pydantic validation, not a 500


def test_second_deploy_while_one_is_in_flight_gets_409_with_the_first_id(tmp_path, monkeypatch):
    # The real admission-control race (DeployRunner refusing a second
    # concurrent deploy) is exercised against a real, still-running
    # deployment in test_deploy_runner.py's
    # test_concurrent_deploy_raises_with_the_in_flight_id -- TestClient
    # drives its own event loop to completion per call, which made a
    # background deploy task started by the first request already finish
    # (clearing the in-flight id) before the second request even ran, so
    # that timing can't be observed reliably at the HTTP layer. This test
    # instead isolates what IS HTTP-layer-specific: that the admin API
    # correctly turns a DeployInProgressError into a 409 with the in-flight
    # id in the body.
    from supervisor.deploy_runner import DeployInProgressError

    client = new_client(tmp_path)

    def raise_in_progress(*args, **kwargs):
        raise DeployInProgressError("already-running-id")

    monkeypatch.setattr("supervisor.admin_api.DeployRunner.start_deploy", raise_in_progress)

    resp = client.post("/admin/deploy", headers=auth_headers(), json={"argv": ["sleep", "5"]})
    assert resp.status_code == 409
    assert resp.json()["detail"]["deployment_id"] == "already-running-id"


def test_admin_logs_returns_the_current_process_output_and_lines_limits_it(tmp_path):
    """P05: run a deploy -> GET /admin/logs returns the child's recent
    output; lines=N limits correctly."""
    import asyncio

    fake_vllm = str(Path(__file__).parent.parent.parent / "tests" / "fake-vllm" / "fake_vllm.py")
    pm = ProcessManager()
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), archive=DeploymentArchive(tmp_path))
    app = create_app(pm, runner, StateStore(tmp_path / "state.json"), TOKEN)
    client = TestClient(app)

    import socket
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]

    try:
        argv = [sys.executable, fake_vllm, "--port", str(port), "--served-model-name", "model-a"]
        ok = asyncio.run(runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=10))
        assert ok is True

        full = client.get("/admin/logs", headers=auth_headers()).json()["lines"]
        assert "fake-vllm: listening" in full
        assert "GET /health" in full

        limited = client.get("/admin/logs?lines=1", headers=auth_headers()).json()["lines"]
        assert len(limited.splitlines()) == 1
        assert limited == full.splitlines()[-1]
    finally:
        pm.stop()


def test_admin_deployment_logs_returns_that_deployments_archived_output(tmp_path):
    import asyncio

    fake_vllm = str(Path(__file__).parent.parent.parent / "tests" / "fake-vllm" / "fake_vllm.py")
    pm = ProcessManager()
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), archive=DeploymentArchive(tmp_path))
    app = create_app(pm, runner, StateStore(tmp_path / "state.json"), TOKEN)
    client = TestClient(app)

    import socket
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]

    try:
        argv = [sys.executable, fake_vllm, "--port", str(port), "--served-model-name", "model-a", "--fake-slow-start", "2"]
        ok = asyncio.run(runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=10))
        assert ok is True

        resp = client.get("/admin/deployments/d1/logs", headers=auth_headers())
        assert "fake-vllm: listening" in resp.json()["lines"]

        missing = client.get("/admin/deployments/does-not-exist/logs", headers=auth_headers())
        assert missing.json()["lines"] == ""
    finally:
        pm.stop()
