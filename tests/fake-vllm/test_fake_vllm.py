"""Scripted-conversation tests for the fake vLLM test double.

These are the "fake vLLM" checks named in P00's Definition of done. Each
test launches tests/fake-vllm/fake_vllm.py as a real subprocess on a free
port and talks to it over plain HTTP (stdlib only, no requests/pytest
plugins needed beyond pytest itself).
"""
from __future__ import annotations

import json
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

import pytest

SCRIPT = Path(__file__).parent / "fake_vllm.py"


def _free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def _get(port: int, path: str) -> tuple[int, dict]:
    req = urllib.request.Request(f"http://127.0.0.1:{port}{path}")
    try:
        with urllib.request.urlopen(req, timeout=5) as resp:
            return resp.status, json.loads(resp.read())
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read())


def _post(port: int, path: str, body: dict) -> tuple[int, bytes]:
    data = json.dumps(body).encode("utf-8")
    req = urllib.request.Request(
        f"http://127.0.0.1:{port}{path}", data=data, method="POST",
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=5) as resp:
        return resp.status, resp.read()


def _wait_for_health(port: int, timeout: float = 10.0) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            status, _ = _get(port, "/health")
            if status == 200:
                return True
        except (urllib.error.URLError, ConnectionError):
            pass
        time.sleep(0.1)
    return False


@pytest.fixture
def fake_vllm():
    """Starts a plain fake-vllm instance and yields (process, port)."""
    port = _free_port()
    proc = subprocess.Popen(
        [sys.executable, str(SCRIPT), "--port", str(port), "--served-model-name", "fake-model"],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
    )
    try:
        assert _wait_for_health(port), "fake-vllm never became healthy"
        yield proc, port
    finally:
        proc.terminate()
        proc.wait(timeout=5)


def test_scripted_conversation(fake_vllm):
    _, port = fake_vllm

    status, body = _get(port, "/health")
    assert status == 200

    status, body = _get(port, "/v1/models")
    assert status == 200
    assert body["data"][0]["id"] == "fake-model"

    status, raw = _post(port, "/v1/completions", {"model": "fake-model", "prompt": "hi"})
    assert status == 200
    assert "OK" in json.loads(raw)["choices"][0]["text"]

    status, raw = _post(port, "/v1/chat/completions", {"model": "fake-model", "messages": []})
    assert status == 200
    assert "OK" in json.loads(raw)["choices"][0]["message"]["content"]


def test_streaming_completion_is_sse(fake_vllm):
    _, port = fake_vllm
    data = json.dumps({"model": "fake-model", "messages": [], "stream": True}).encode("utf-8")
    req = urllib.request.Request(
        f"http://127.0.0.1:{port}/v1/chat/completions", data=data, method="POST",
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=5) as resp:
        assert resp.headers.get("Content-Type") == "text/event-stream"
        body = resp.read().decode("utf-8")
    assert body.startswith("data: ")
    assert "data: [DONE]" in body
    assert "OK" in body


def test_fake_crash_exits_non_zero():
    proc = subprocess.run(
        [sys.executable, str(SCRIPT), "--fake-crash"],
        capture_output=True, text=True, timeout=5,
    )
    assert proc.returncode != 0


def test_fake_slow_start_delays_health():
    port = _free_port()
    proc = subprocess.Popen(
        [sys.executable, str(SCRIPT), "--port", str(port), "--fake-slow-start", "3"],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
    )
    try:
        start = time.monotonic()
        # The subprocess needs a moment to start listening at all -- retry past
        # plain connection-refused, but the first real response must be 503.
        deadline = time.monotonic() + 2.0
        status = None
        while time.monotonic() < deadline:
            try:
                status, _ = _get(port, "/health")
                break
            except (urllib.error.URLError, ConnectionError):
                time.sleep(0.05)
        assert status == 503, "expected /health to fail while still starting"

        assert _wait_for_health(port, timeout=10), "fake-vllm never became healthy after slow start"
        elapsed = time.monotonic() - start
        assert elapsed >= 2.5, f"health passed suspiciously fast ({elapsed:.1f}s) for --fake-slow-start 3"
    finally:
        proc.terminate()
        proc.wait(timeout=5)


def test_fake_exit_after_dies_mid_run():
    port = _free_port()
    proc = subprocess.Popen(
        [sys.executable, str(SCRIPT), "--port", str(port), "--fake-exit-after", "1"],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
    )
    try:
        assert _wait_for_health(port), "fake-vllm never became healthy before the exit-after window"
        status, _ = _get(port, "/health")
        assert status == 200, "expected it to still be up right after starting"

        returncode = proc.wait(timeout=5)
        assert returncode != 0, "expected a non-zero exit when --fake-exit-after elapses"

        with pytest.raises((urllib.error.URLError, ConnectionError)):
            _get(port, "/health")
    finally:
        if proc.poll() is None:
            proc.terminate()
            proc.wait(timeout=5)


def test_fake_bad_output_omits_ok():
    port = _free_port()
    proc = subprocess.Popen(
        [sys.executable, str(SCRIPT), "--port", str(port), "--fake-bad-output"],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
    )
    try:
        assert _wait_for_health(port)
        status, raw = _post(port, "/v1/chat/completions", {"model": "fake-model", "messages": []})
        assert status == 200
        assert "OK" not in json.loads(raw)["choices"][0]["message"]["content"]
    finally:
        proc.terminate()
        proc.wait(timeout=5)
