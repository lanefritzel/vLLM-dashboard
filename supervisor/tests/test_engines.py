"""P2: the supervisor knows which engine it is (ENGINE env var, default vllm) and
everything engine-specific -- startup tracker, served-name flag, status block --
is looked up through supervisor/engines.py rather than hardcoded to vLLM."""
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

import http.server
import json
import threading

from supervisor.engines import engine_from_env, engine_info, extract_value, fetch_version, served_model_name, tracker_for
from supervisor.startup_phases import StartupTracker
from supervisor.startup_phases_llamacpp import LlamaCppStartupTracker


def test_engine_defaults_to_vllm_when_unset():
    assert engine_from_env({}) == "vllm"


def test_engine_reads_the_env_var_case_insensitively():
    assert engine_from_env({"ENGINE": "LlamaCpp"}) == "llamacpp"


def test_unknown_engine_is_rejected_loudly():
    with pytest.raises(ValueError, match="ENGINE"):
        engine_from_env({"ENGINE": "ollama"})


def test_tracker_for_picks_the_engines_own_tracker():
    assert isinstance(tracker_for("vllm"), StartupTracker)
    assert isinstance(tracker_for("llamacpp"), LlamaCppStartupTracker)


def test_each_call_gets_a_fresh_tracker():
    assert tracker_for("vllm") is not tracker_for("vllm")


def test_served_model_name_uses_each_engines_own_flag():
    assert served_model_name(["vllm", "serve", "/m", "--served-model-name", "a"], "vllm") == "a"
    assert served_model_name(["llama-server", "--model", "/m", "--alias", "b"], "llamacpp") == "b"


def test_served_model_name_ignores_the_other_engines_flag():
    assert served_model_name(["llama-server", "--served-model-name", "x"], "llamacpp") is None
    assert served_model_name(["vllm", "serve", "--alias", "x"], "vllm") is None


def test_engine_info_reports_name_and_a_nullable_version():
    assert engine_info("llamacpp") == {"name": "llamacpp", "version": None}
    assert engine_info("vllm")["name"] == "vllm"


def _serve(payload, status=200):
    class H(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            body = json.dumps(payload).encode()
            self.send_response(status)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *a):
            pass

    srv = http.server.HTTPServer(("127.0.0.1", 0), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv


def test_fetch_version_reads_llamacpps_build_info_from_props():
    srv = _serve({"build_info": "b12242-3b4d6ab3f"})
    try:
        assert fetch_version("llamacpp", srv.server_address[1]) == "b12242-3b4d6ab3f"
    finally:
        srv.shutdown()


def test_fetch_version_is_none_when_props_is_missing_or_unreachable():
    srv = _serve({"error": "nope"}, status=404)
    try:
        assert fetch_version("llamacpp", srv.server_address[1]) is None
    finally:
        srv.shutdown()
    assert fetch_version("llamacpp", 1) is None  # nothing listening


def test_fetch_version_is_none_for_vllm_until_its_source_is_confirmed():
    assert fetch_version("vllm", 1) is None


@pytest.mark.parametrize("payload", [[], None, "b123", 42, [{"build_info": "x"}]])
def test_fetch_version_is_none_when_props_is_not_a_json_object(payload):
    srv = _serve(payload)
    try:
        assert fetch_version("llamacpp", srv.server_address[1]) is None
    finally:
        srv.shutdown()


def test_extract_value_is_the_one_shared_argv_lookup():
    assert extract_value(["a", "--port", "1"], "--port") == "1"
    assert extract_value(["a", "--port"], "--port") is None
    assert extract_value(["a"], "--port") is None
