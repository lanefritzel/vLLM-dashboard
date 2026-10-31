#!/usr/bin/env python3
"""Fake vLLM test double.

Serves just enough of vLLM's HTTP surface (``/health``, ``/v1/models``,
``/v1/completions``, ``/v1/chat/completions``) for the dashboard and its
integration tests to run against, with no GPU and no real model. Stdlib
only -- nothing here is a production dependency.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class FakeVllmState:
    def __init__(self, served_model_name: str, slow_start: float, bad_output: bool) -> None:
        self.served_model_name = served_model_name
        self.bad_output = bad_output
        self.slow_start = slow_start
        self._start_time = time.monotonic()

    @property
    def ready(self) -> bool:
        return time.monotonic() - self._start_time >= self.slow_start


def make_handler(state: FakeVllmState) -> type[BaseHTTPRequestHandler]:
    class Handler(BaseHTTPRequestHandler):
        server_version = "FakeVLLM/1.0"

        def log_message(self, fmt: str, *args: object) -> None:
            print(f"fake-vllm: {self.command} {self.path} -> {fmt % args}", flush=True)

        def _write_json(self, status: int, payload: dict) -> None:
            body = json.dumps(payload).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def _completion_choice(self, is_chat: bool, text: str, streaming: bool) -> dict:
            if streaming and is_chat:
                choice = {"delta": {"content": text}}
            elif is_chat:
                choice = {"message": {"role": "assistant", "content": text}}
            else:
                choice = {"text": text}
            choice.update({"index": 0, "finish_reason": "stop"})
            return choice

        def _completion_body(self, is_chat: bool, text: str, streaming: bool) -> dict:
            kind = "chat.completion.chunk" if streaming else ("chat.completion" if is_chat else "text_completion")
            return {
                "id": "fake-cmpl-0",
                "object": kind,
                "created": int(time.time()),
                "model": state.served_model_name,
                "choices": [self._completion_choice(is_chat, text, streaming)],
            }

        def do_GET(self) -> None:  # noqa: N802 (BaseHTTPRequestHandler API)
            if self.path == "/health":
                if state.ready:
                    self._write_json(200, {"status": "ok"})
                else:
                    self._write_json(503, {"status": "starting"})
                return
            if self.path == "/v1/models":
                self._write_json(200, {
                    "object": "list",
                    "data": [{
                        "id": state.served_model_name,
                        "object": "model",
                        "created": int(time.time()),
                        "owned_by": "fake-vllm",
                    }],
                })
                return
            self._write_json(404, {"error": "not found"})

        def do_POST(self) -> None:  # noqa: N802
            if self.path not in ("/v1/completions", "/v1/chat/completions"):
                self._write_json(404, {"error": "not found"})
                return

            length = int(self.headers.get("Content-Length") or 0)
            raw = self.rfile.read(length) if length else b"{}"
            try:
                req = json.loads(raw or b"{}")
            except json.JSONDecodeError:
                req = {}

            stream = bool(req.get("stream"))
            is_chat = self.path == "/v1/chat/completions"
            text = "nope" if state.bad_output else "OK"

            if stream:
                chunk = self._completion_body(is_chat, text, streaming=True)
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.end_headers()
                self.wfile.write(f"data: {json.dumps(chunk)}\n\n".encode("utf-8"))
                self.wfile.write(b"data: [DONE]\n\n")
            else:
                self._write_json(200, self._completion_body(is_chat, text, streaming=False))

    return Handler


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1", help="bind address (compose needs 0.0.0.0 to be reachable from other containers)")
    parser.add_argument("--port", type=int, default=9999)
    parser.add_argument("--served-model-name", default="fake-model")
    parser.add_argument("--fake-slow-start", type=float, default=0.0, metavar="SECS")
    parser.add_argument("--fake-crash", action="store_true", help="exit non-zero immediately instead of serving")
    parser.add_argument("--fake-exit-after", type=float, default=None, metavar="SECS", help="die mid-run")
    parser.add_argument("--fake-bad-output", action="store_true", help="omit 'OK' from completions")
    args = parser.parse_args()

    if args.fake_crash:
        print("fake-vllm: --fake-crash set, exiting non-zero immediately.", flush=True)
        sys.exit(1)

    state = FakeVllmState(args.served_model_name, args.fake_slow_start, args.fake_bad_output)
    server = ThreadingHTTPServer((args.host, args.port), make_handler(state))

    if args.fake_exit_after is not None:
        def die_later() -> None:
            time.sleep(args.fake_exit_after)
            print(f"fake-vllm: --fake-exit-after {args.fake_exit_after}s elapsed, exiting.", flush=True)
            os._exit(1)  # a plain thread can't exit the process via sys.exit()

        threading.Thread(target=die_later, daemon=True).start()

    print(f"fake-vllm: listening on {args.host}:{args.port}, served-model-name={args.served_model_name}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
