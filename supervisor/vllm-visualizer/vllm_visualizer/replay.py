"""Replay server: serves recorded real snapshots with the same HTTP contract as
the live plugin (/health, /snapshot, /stream). Lets the dashboard page be built
and tested with no GPU and no vLLM.

    python -m vllm_visualizer.replay tests/fixtures/real_snapshots.jsonl --port 9998
"""
from __future__ import annotations

import argparse
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def load(path: str) -> list[dict]:
    with open(path) as f:
        return [json.loads(line) for line in f if line.strip()]


def make_server(path: str, host: str = "127.0.0.1", port: int = 0, interval: float = 0.12):
    snaps = load(path)
    state = {"i": 0, "armed_until": 0.0}

    def current() -> dict:
        s = dict(snaps[state["i"] % len(snaps)])
        s["seq"] = state["i"] + 1
        s["ts"] = time.time()
        return s

    def ticker() -> None:
        while True:
            time.sleep(interval)
            if time.monotonic() <= state["armed_until"]:
                state["i"] += 1

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *a):
            pass

        def _json(self, obj, code=200):
            body = json.dumps(obj).encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            path_ = self.path.split("?")[0]
            if path_ == "/health":
                return self._json({"armed": time.monotonic() <= state["armed_until"], "replay": True,
                                   "layers": snaps[0]["layers"], "snapshots": state["i"], "errors": 0})
            if path_ == "/snapshot":
                state["armed_until"] = time.monotonic() + 5
                return self._json(current())
            if path_ == "/stream":
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.send_header("Cache-Control", "no-cache")
                self.end_headers()
                last = -1
                try:
                    while True:
                        state["armed_until"] = time.monotonic() + 5
                        if state["i"] != last:
                            last = state["i"]
                            self.wfile.write(b"data: " + json.dumps(current()).encode() + b"\n\n")
                        else:
                            self.wfile.write(b": keepalive\n\n")
                        self.wfile.flush()
                        time.sleep(interval)
                except (BrokenPipeError, ConnectionResetError, OSError):
                    return
            self._json({"error": "not found"}, 404)

    server = ThreadingHTTPServer((host, port), Handler)
    threading.Thread(target=ticker, daemon=True).start()
    return server


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("fixture")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=9998)
    ap.add_argument("--interval", type=float, default=0.12)
    a = ap.parse_args()
    srv = make_server(a.fixture, a.host, a.port, a.interval)
    print(f"replaying {a.fixture} on http://{a.host}:{srv.server_address[1]}")
    srv.serve_forever()
