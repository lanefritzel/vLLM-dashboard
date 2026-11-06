"""HTTP-layer behavior of the admin API (P03a) via FastAPI's TestClient --
fast, in-process, no real sockets for most of these (the /admin/deploy
happy-path -> real-child tests live in test_deploy_runner.py).

No auth of any kind as of the loopback-only change: supervisor.py binds this
API to 127.0.0.1 only, so it's never reachable from anywhere but the same
host the dashboard runs on -- a bearer token was protecting against LAN
exposure that no longer exists, not against anything these tests simulate.
"""
import sys
from pathlib import Path

from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).parent.parent))

from supervisor.admin_api import create_app
from supervisor.deploy_runner import DeployRunner
from supervisor.downloads import DownloadManager, FakeHfClient
from supervisor.log_store import DeploymentArchive
from supervisor.process_manager import ProcessManager
from supervisor.state import StateStore, SupervisorState


def new_client(tmp_path, download_manager=None) -> TestClient:
    pm = ProcessManager()
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), archive=DeploymentArchive(tmp_path))
    app = create_app(pm, runner, StateStore(tmp_path / "state.json"), download_manager)
    return TestClient(app)


def auth_headers() -> dict:
    # Kept as a call site rather than ripping it out of every request below --
    # there is no auth any more, so this is deliberately a no-op.
    return {}


def test_health_is_reachable(tmp_path):
    client = new_client(tmp_path)
    resp = client.get("/admin/health")
    assert resp.status_code == 200


def test_status_with_no_auth_header_is_200(tmp_path):
    client = new_client(tmp_path)
    resp = client.get("/admin/status")
    assert resp.status_code == 200
    body = resp.json()
    assert body["state"] == "idle"
    assert body["child"] is None
    assert body["smoke_ok"] is None


def test_admin_routes_never_401_for_auth_reasons(tmp_path):
    client = new_client(tmp_path)
    for method, path in [
        ("GET", "/admin/status"),
        ("POST", "/admin/deploy"),
        ("GET", "/admin/deployments/x/events"),
        ("GET", "/admin/settings"),
        ("PUT", "/admin/settings"),
        ("POST", "/admin/smoke-test"),
        ("POST", "/admin/deployments/x/cancel"),
        ("POST", "/admin/stop"),
        ("POST", "/admin/restart"),
        ("POST", "/admin/downloads/inspect"),
        ("POST", "/admin/downloads"),
        ("GET", "/admin/downloads/x"),
        ("DELETE", "/admin/downloads/x"),
    ]:
        resp = client.request(method, path, json={} if method in ("POST", "PUT") else None)
        assert resp.status_code != 401, f"{method} {path} should never 401 -- there is no auth any more"


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
    app = create_app(pm, runner, StateStore(tmp_path / "state.json"))
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


def test_smoke_test_endpoint_409s_while_idle(tmp_path):
    client = new_client(tmp_path)
    resp = client.post("/admin/smoke-test", headers=auth_headers())
    assert resp.status_code == 409
    assert resp.json()["detail"]["state"] == "idle"


def test_smoke_test_endpoint_runs_on_demand_while_serving(tmp_path):
    import asyncio

    fake_vllm = str(Path(__file__).parent.parent.parent / "tests" / "fake-vllm" / "fake_vllm.py")
    pm = ProcessManager()
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), archive=DeploymentArchive(tmp_path))
    app = create_app(pm, runner, StateStore(tmp_path / "state.json"))
    client = TestClient(app)

    import socket
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]

    try:
        argv = [sys.executable, fake_vllm, "--port", str(port), "--served-model-name", "model-a"]
        ok = asyncio.run(runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=10, smoke={"skip": True}))
        assert ok is True

        resp = client.post("/admin/smoke-test", headers=auth_headers())
        assert resp.status_code == 200
        assert resp.json()["ok"] is True

        status = client.get("/admin/status", headers=auth_headers()).json()
        assert status["smoke_ok"] is True
    finally:
        pm.stop()


def test_stop_endpoint_is_idempotent_while_idle(tmp_path):
    client = new_client(tmp_path)
    resp = client.post("/admin/stop", headers=auth_headers())
    assert resp.status_code == 200
    assert resp.json() == {"state": "idle", "vram": {"released": True, "used_mb": None, "total_mb": None}}


def test_restart_endpoint_409s_while_idle(tmp_path):
    client = new_client(tmp_path)
    resp = client.post("/admin/restart", headers=auth_headers())
    assert resp.status_code == 409
    assert resp.json()["detail"]["state"] == "idle"


def test_cancel_endpoint_409s_with_nothing_in_flight(tmp_path):
    client = new_client(tmp_path)
    resp = client.post("/admin/deployments/nope/cancel", headers=auth_headers())
    assert resp.status_code == 409


def test_restart_endpoint_translates_to_a_new_deployment_id(tmp_path):
    # Restart's actual run-to-ready behavior (new PID, same argv, full event
    # sequence) is covered against a real child under one continuous
    # asyncio.run() in test_cancel_stop_restart.py -- TestClient drives its
    # own event loop to completion per call, same reason /admin/deploy's
    # own happy-path test lives there instead of here (see that file's
    # module docstring). This just isolates the HTTP-layer translation:
    # a 200 with a NEW deployment id, distinct from the one already serving.
    import asyncio

    fake_vllm = str(Path(__file__).parent.parent.parent / "tests" / "fake-vllm" / "fake_vllm.py")
    pm = ProcessManager()
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), archive=DeploymentArchive(tmp_path))
    app = create_app(pm, runner, StateStore(tmp_path / "state.json"))
    client = TestClient(app)

    import socket
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]

    try:
        argv = [sys.executable, fake_vllm, "--port", str(port), "--served-model-name", "model-a"]
        ok = asyncio.run(runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=10))
        assert ok is True

        resp = client.post("/admin/restart", headers=auth_headers())
        assert resp.status_code == 200
        assert resp.json()["deployment_id"] != "d1"
    finally:
        pm.stop()


def test_restart_endpoint_honors_a_caller_supplied_deployment_id(tmp_path):
    import asyncio

    fake_vllm = str(Path(__file__).parent.parent.parent / "tests" / "fake-vllm" / "fake_vllm.py")
    pm = ProcessManager()
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), archive=DeploymentArchive(tmp_path))
    app = create_app(pm, runner, StateStore(tmp_path / "state.json"))
    client = TestClient(app)

    import socket
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]

    try:
        argv = [sys.executable, fake_vllm, "--port", str(port), "--served-model-name", "model-a"]
        ok = asyncio.run(runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=10))
        assert ok is True

        resp = client.post("/admin/restart", headers=auth_headers(), json={"deployment_id": "dashboard-row-7"})
        assert resp.status_code == 200
        assert resp.json()["deployment_id"] == "dashboard-row-7"
    finally:
        pm.stop()


def test_stop_endpoint_against_a_real_child_releases_it(tmp_path):
    import asyncio

    fake_vllm = str(Path(__file__).parent.parent.parent / "tests" / "fake-vllm" / "fake_vllm.py")
    pm = ProcessManager()
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), archive=DeploymentArchive(tmp_path))
    app = create_app(pm, runner, StateStore(tmp_path / "state.json"))
    client = TestClient(app)

    import socket
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]

    try:
        argv = [sys.executable, fake_vllm, "--port", str(port), "--served-model-name", "model-a"]
        ok = asyncio.run(runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=10))
        assert ok is True

        resp = client.post("/admin/stop", headers=auth_headers())
        assert resp.status_code == 200
        assert resp.json()["state"] == "idle"
        assert not pm.is_running()
    finally:
        pm.stop()


def test_downloads_endpoints_round_trip_through_the_admin_api(tmp_path):
    import json
    import time

    repo = tmp_path / "repo"
    repo.mkdir()
    (repo / "model.safetensors").write_bytes(b"x" * 1024)
    (repo / "config.json").write_text(json.dumps({"k": "v"}))

    # A small chunk_delay keeps the download reliably "downloading" for the
    # 409 check below under any test-suite timing load -- without it, a
    # ~1KB copy can finish before the second POST ever runs (found flaky
    # under the full suite, passing in isolation).
    manager = DownloadManager(FakeHfClient({"org/model-a": repo}, chunk_delay=0.05), models_root=tmp_path / "models")
    client = new_client(tmp_path, download_manager=manager)

    resp = client.post("/admin/downloads/inspect", headers=auth_headers(), json={"repo_id": "org/model-a"})
    assert resp.status_code == 200
    assert resp.json()["config"] == {"k": "v"}

    resp = client.post("/admin/downloads/inspect", headers=auth_headers(), json={"repo_id": "nonexistent/repo"})
    assert resp.status_code == 404

    resp = client.post("/admin/downloads", headers=auth_headers(), json={"repo_id": "org/model-a", "slug": "model-a"})
    assert resp.status_code == 200
    download_id = resp.json()["download_id"]

    resp = client.post("/admin/downloads", headers=auth_headers(), json={"repo_id": "org/model-a", "slug": "model-a-2"})
    assert resp.status_code == 409

    for _ in range(100):
        resp = client.get(f"/admin/downloads/{download_id}", headers=auth_headers())
        if resp.json()["status"] == "done":
            break
        time.sleep(0.05)
    else:
        raise AssertionError("download never reached done")
    assert resp.json()["load_format"] is None

    resp = client.get("/admin/downloads/nope", headers=auth_headers())
    assert resp.status_code == 404

    resp = client.delete(f"/admin/downloads/{download_id}", headers=auth_headers())
    assert resp.status_code == 200
    assert not (tmp_path / "models" / "model-a").exists()

    resp = client.delete("/admin/downloads/nope", headers=auth_headers())
    assert resp.status_code == 404


def test_admin_deployment_logs_returns_that_deployments_archived_output(tmp_path):
    import asyncio

    fake_vllm = str(Path(__file__).parent.parent.parent / "tests" / "fake-vllm" / "fake_vllm.py")
    pm = ProcessManager()
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), archive=DeploymentArchive(tmp_path))
    app = create_app(pm, runner, StateStore(tmp_path / "state.json"))
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


def test_gpu_endpoint_reflects_the_configured_backend(tmp_path):
    from supervisor.gpu_backend import FakeBackend

    fake_path = tmp_path / "fake_gpu.json"
    fake_path.write_text('{"vendor": "fake", "count": 1, "total_mb": 12288, "used_mb": 4096}')
    pm = ProcessManager()
    runner = DeployRunner(
        pm, StateStore(tmp_path / "state.json"), archive=DeploymentArchive(tmp_path), gpu_backend=FakeBackend(fake_path)
    )
    app = create_app(pm, runner, StateStore(tmp_path / "state.json"))
    client = TestClient(app)

    resp = client.get("/admin/gpu", headers=auth_headers())
    assert resp.json() == {"vendor": "fake", "count": 1, "total_mb": 12288, "used_mb": 4096}


def test_gpu_recheck_re_detects_instead_of_re_querying_the_frozen_backend(tmp_path, monkeypatch):
    """Regression test for the recheck endpoint only re-querying whatever
    backend startup picked (never re-running detect_backend()) -- a future
    misdetection used to need a full supervisor restart to pick up a fix."""
    pm = ProcessManager()
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), archive=DeploymentArchive(tmp_path))  # starts generic
    app = create_app(pm, runner, StateStore(tmp_path / "state.json"))
    client = TestClient(app)

    assert client.get("/admin/gpu", headers=auth_headers()).json()["vendor"] == "generic"

    fake_path = tmp_path / "fake_gpu.json"
    fake_path.write_text('{"vendor": "fake", "count": 1, "total_mb": 8192, "used_mb": 1024}')
    monkeypatch.setenv("GPU_BACKEND", "fake")
    monkeypatch.setenv("GPU_BACKEND_FAKE_PATH", str(fake_path))

    recheck_resp = client.post("/admin/gpu/recheck", headers=auth_headers())
    assert recheck_resp.json() == {"vendor": "fake", "count": 1, "total_mb": 8192, "used_mb": 1024}

    # The swap persists on the runner, not just the recheck response itself.
    assert client.get("/admin/gpu", headers=auth_headers()).json()["vendor"] == "fake"
