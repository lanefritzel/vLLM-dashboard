"""P04's Definition of done, integration bullets: with the fake GPU
backend, used_mb dropping lets releasing_gpu proceed quickly; used_mb
staying high makes it wait out ~10s and set the flag. With no GPU tools at
all (the default/generic backend), a deploy still reaches ready and
/admin/gpu reports vendor "generic" -- a missing metric never fails a swap.
"""
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

from supervisor.deploy_runner import DeployRunner
from supervisor.gpu_backend import FakeBackend, GpuBackend, GpuInfo
from supervisor.process_manager import ProcessManager
from supervisor.state import StateStore


def new_runner(tmp_path, gpu_backend=None) -> tuple[DeployRunner, ProcessManager]:
    pm = ProcessManager()
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), gpu_backend=gpu_backend)
    return runner, pm


def test_vram_dropping_lets_releasing_gpu_proceed_quickly(tmp_path):
    fake_path = tmp_path / "fake_gpu.json"
    fake_path.write_text(json.dumps({"vendor": "fake", "count": 1, "total_mb": 12288, "used_mb": 50}))
    runner, pm = new_runner(tmp_path, gpu_backend=FakeBackend(fake_path))

    # before.used_mb will be 50 (already low) -- _vram_released(50, 50) is
    # True immediately (50 <= max(50*0.1, 100) == 100), so this never even
    # enters the polling loop.
    before = GpuInfo(vendor="fake", count=1, total_mb=12288, used_mb=50)
    start = time.monotonic()
    still_in_use = runner._wait_for_gpu_release(before)
    elapsed = time.monotonic() - start

    assert still_in_use is False
    assert elapsed < 4, f"should proceed right after the initial 2s wait, took {elapsed:.1f}s"
    pm.stop()


def test_vram_staying_high_waits_out_ten_seconds_and_sets_the_flag(tmp_path):
    fake_path = tmp_path / "fake_gpu.json"
    fake_path.write_text(json.dumps({"vendor": "fake", "count": 1, "total_mb": 12288, "used_mb": 8000}))
    runner, pm = new_runner(tmp_path, gpu_backend=FakeBackend(fake_path))

    before = GpuInfo(vendor="fake", count=1, total_mb=12288, used_mb=8000)  # never changes -- stays "high"
    start = time.monotonic()
    still_in_use = runner._wait_for_gpu_release(before)
    elapsed = time.monotonic() - start

    assert still_in_use is True
    assert 9 <= elapsed <= 15, f"should give up at ~10s total, took {elapsed:.1f}s"
    pm.stop()


def test_vram_dropping_partway_through_the_wait_is_detected(tmp_path):
    fake_path = tmp_path / "fake_gpu.json"
    fake_path.write_text(json.dumps({"vendor": "fake", "count": 1, "total_mb": 12288, "used_mb": 8000}))
    runner, pm = new_runner(tmp_path, gpu_backend=FakeBackend(fake_path))
    before = GpuInfo(vendor="fake", count=1, total_mb=12288, used_mb=8000)

    import threading

    def drop_vram_after_a_bit():
        time.sleep(3)
        fake_path.write_text(json.dumps({"vendor": "fake", "count": 1, "total_mb": 12288, "used_mb": 10}))

    threading.Thread(target=drop_vram_after_a_bit, daemon=True).start()
    start = time.monotonic()
    still_in_use = runner._wait_for_gpu_release(before)
    elapsed = time.monotonic() - start

    assert still_in_use is False
    assert elapsed < 9, f"should notice the drop well before the 10s cap, took {elapsed:.1f}s"
    pm.stop()


def test_no_backend_at_all_waits_only_the_initial_two_seconds(tmp_path):
    runner, pm = new_runner(tmp_path, gpu_backend=GpuBackend())  # the generic no-op backend
    before = runner.gpu_backend.query()
    assert before.used_mb is None

    start = time.monotonic()
    still_in_use = runner._wait_for_gpu_release(before)
    elapsed = time.monotonic() - start

    assert still_in_use is False
    assert elapsed < 4
    pm.stop()


def test_generic_backend_gpu_endpoint_and_a_deploy_still_reaches_ready(tmp_path):
    """The "no GPU tools on the host" integration bullet: /admin/gpu
    reports generic, and a deploy still completes (a missing metric never
    fails a swap)."""
    import asyncio

    from fastapi.testclient import TestClient

    from supervisor.admin_api import create_app

    pm = ProcessManager()
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), gpu_backend=GpuBackend())
    app = create_app(pm, runner, StateStore(tmp_path / "state.json"), "tok")
    client = TestClient(app)

    resp = client.get("/admin/gpu", headers={"Authorization": "Bearer tok"})
    assert resp.json() == {"vendor": "generic", "count": 0, "total_mb": None, "used_mb": None}

    fake_vllm = str(Path(__file__).parent.parent.parent / "tests" / "fake-vllm" / "fake_vllm.py")

    import socket
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]

    try:
        argv = [sys.executable, fake_vllm, "--port", str(port), "--served-model-name", "model-a"]
        ok = asyncio.run(runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=10))
        assert ok is True
        assert runner.state == "ready"
    finally:
        pm.stop()
