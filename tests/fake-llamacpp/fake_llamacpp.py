#!/usr/bin/env python3
"""Fake llama-server test double (P2), same shape as tests/fake-vllm/fake_vllm.py.

Serves just enough of llama-server's HTTP surface (``/health``, ``/props``, ``/v1/models``,
``/v1/chat/completions``, ``/metrics``) for the supervisor's integration tests, with no
model and no Docker. Metric names are the ones confirmed against a real llama-server.
Stdlib only. Flags mirror what LlamaCppArgBuilder emits (--model, --alias, --ctx-size,
--port, --host, --metrics).
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

METRICS = """# TYPE llamacpp:prompt_tokens_total counter
llamacpp:prompt_tokens_total 0
# TYPE llamacpp:tokens_predicted_total counter
llamacpp:tokens_predicted_total 0
# TYPE llamacpp:requests_processing gauge
llamacpp:requests_processing 0
# TYPE llamacpp:requests_deferred gauge
llamacpp:requests_deferred 0
"""


def make_handler(alias: str, require_model: str | None) -> type[BaseHTTPRequestHandler]:
    class Handler(BaseHTTPRequestHandler):
        server_version = "FakeLlamaCpp/1.0"

        def log_message(self, fmt: str, *args: object) -> None:
            print(f"fake-llamacpp: {self.command} {self.path} -> {fmt % args}", flush=True)

        def _json(self, status: int, payload: dict) -> None:
            body = json.dumps(payload).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self) -> None:  # noqa: N802
            if self.path == "/health":
                self._json(200, {"status": "ok"})
            elif self.path == "/v1/models":
                self._json(200, {"object": "list", "data": [
                    {"id": alias, "object": "model", "created": int(time.time()), "owned_by": "fake-llamacpp"}]})
            elif self.path == "/props":
                # Real llama-server reports its build here (confirmed live: "b12242-3b4d6ab3f").
                self._json(200, {"model_alias": alias, "build_info": "bFAKE-0000"})
            elif self.path == "/metrics":
                body = METRICS.encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "text/plain; version=0.0.4")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            else:
                self._json(404, {"error": "not found"})

        def do_POST(self) -> None:  # noqa: N802
            if self.path != "/v1/chat/completions":
                self._json(404, {"error": "not found"})
                return
            length = int(self.headers.get("Content-Length") or 0)
            try:
                req = json.loads(self.rfile.read(length) or b"{}")
            except json.JSONDecodeError:
                req = {}
            if require_model is not None and req.get("model") != require_model:
                self._json(400, {"error": f"model {req.get('model')!r} != {require_model!r}"})
                return
            self._json(200, {
                "id": "fake-cmpl-0", "object": "chat.completion", "created": int(time.time()), "model": alias,
                "choices": [{"index": 0, "finish_reason": "stop", "message": {"role": "assistant", "content": "OK"}}],
            })

    return Handler


def replay_startup_log(path: str) -> None:
    """Prints a captured llama-server startup log before binding the port; '#sleep N' pauses."""
    with open(path, encoding="utf-8") as f:
        for raw in f:
            line = raw.rstrip("\n")
            if line.startswith("#sleep "):
                time.sleep(float(line.split()[1]))
            else:
                print(line, flush=True)


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=8080)
    p.add_argument("--alias", default="fake-model")
    p.add_argument("--model")
    p.add_argument("--ctx-size")
    p.add_argument("--metrics", action="store_true")
    p.add_argument("--fake-startup-log", default=None, metavar="FILE")
    p.add_argument("--fake-require-model", default=None, metavar="NAME",
                   help="400 any chat request whose model != NAME (proves the caller sent the alias)")
    args = p.parse_args()

    if args.fake_startup_log:
        replay_startup_log(args.fake_startup_log)

    server = ThreadingHTTPServer((args.host, args.port), make_handler(args.alias, args.fake_require_model))
    print(f"fake-llamacpp: listening on {args.host}:{args.port}, alias={args.alias}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
