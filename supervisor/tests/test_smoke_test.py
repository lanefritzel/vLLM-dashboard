"""Unit tests for the smoke-test runner (P06) -- a real fake-vllm subprocess
for pass/fail/timeout behavior, pure tests for config resolution.
"""
import socket
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

from supervisor.smoke_test import resolve_smoke_config, run_smoke_test

FAKE_VLLM = str(Path(__file__).parent.parent.parent / "tests" / "fake-vllm" / "fake_vllm.py")


def free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def start_fake(port: int, *extra_args: str) -> subprocess.Popen:
    argv = [sys.executable, FAKE_VLLM, "--port", str(port), "--served-model-name", "model-a", *extra_args]
    proc = subprocess.Popen(argv)
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=0.5):
                return proc
        except OSError:
            time.sleep(0.1)
    proc.kill()
    raise RuntimeError("fake vllm never came up")


def test_run_smoke_test_passes_when_model_answers_ok():
    port = free_port()
    proc = start_fake(port)
    try:
        result = run_smoke_test(port, "model-a")
        assert result.ok is True
        assert "OK" in result.excerpt
    finally:
        proc.kill()
        proc.wait()


def test_run_smoke_test_fails_on_bad_output():
    port = free_port()
    proc = start_fake(port, "--fake-bad-output")
    try:
        result = run_smoke_test(port, "model-a")
        assert result.ok is False
    finally:
        proc.kill()
        proc.wait()


def test_run_smoke_test_fails_when_nothing_is_listening():
    port = free_port()
    result = run_smoke_test(port, "model-a", timeout=2)
    assert result.ok is False
    assert "request failed" in result.excerpt


def test_resolve_smoke_config_defaults_when_omitted():
    assert resolve_smoke_config(None) == ("Reply with OK.", "OK")


def test_resolve_smoke_config_defaults_when_explicitly_null_field():
    # omitted and explicit None are indistinguishable once past pydantic --
    # both must run with defaults (owner-confirmed: automatic by default).
    assert resolve_smoke_config(None) == ("Reply with OK.", "OK")


def test_resolve_smoke_config_skips_on_explicit_skip_true():
    assert resolve_smoke_config({"skip": True}) is None


def test_resolve_smoke_config_honors_custom_prompt_and_expect_substring():
    assert resolve_smoke_config({"prompt": "ping", "expect_substring": "pong"}) == ("ping", "pong")
