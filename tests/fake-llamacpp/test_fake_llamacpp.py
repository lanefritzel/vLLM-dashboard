"""The llama.cpp test double serves the llama-server surface the supervisor touches."""
import json
import socket
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

HERE = Path(__file__).parent
FAKE = str(HERE / "fake_llamacpp.py")
LOG = str(HERE.parent.parent / "supervisor" / "tests" / "fixtures" / "llamacpp_qwen2.5-0.5b_startup.log")


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def start(*extra):
    port = free_port()
    proc = subprocess.Popen([sys.executable, FAKE, "--port", str(port), "--alias", "main", *extra],
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    for _ in range(50):
        try:
            urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=1)
            return proc, port
        except OSError:
            time.sleep(0.1)
    proc.kill()
    raise AssertionError("fake never came up")


def get(port, path):
    with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=2) as r:
        return r.read().decode()


def test_health_models_and_metrics():
    proc, port = start()
    try:
        assert json.loads(get(port, "/health")) == {"status": "ok"}
        assert json.loads(get(port, "/v1/models"))["data"][0]["id"] == "main"
        assert "llamacpp:requests_processing" in get(port, "/metrics")
    finally:
        proc.kill()


def test_chat_completion_answers_ok():
    proc, port = start()
    try:
        req = urllib.request.Request(f"http://127.0.0.1:{port}/v1/chat/completions", method="POST",
                                     data=b'{"model":"main","messages":[]}', headers={"Content-Type": "application/json"})
        body = json.loads(urllib.request.urlopen(req, timeout=2).read())
        assert "OK" in body["choices"][0]["message"]["content"]
    finally:
        proc.kill()


def test_startup_log_is_replayed_before_listening():
    proc, port = start("--fake-startup-log", LOG)
    try:
        proc.kill()
        out = proc.stdout.read()
        assert "load_model: loading model" in out
    finally:
        proc.kill()
