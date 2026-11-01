"""A tiny recording HTTP server standing in for the dashboard's events
receiver (P03c's DoD: "point DASHBOARD_EVENT_URL at a recording stub").
Records every POST's headers + JSON body; nothing fancier.
"""
from __future__ import annotations

import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class RecordingStub:
    def __init__(self, port: int = 0) -> None:
        self.requests: list[dict] = []
        self._lock = threading.Lock()
        handler = self._make_handler()
        self._server = ThreadingHTTPServer(("127.0.0.1", port), handler)
        self.port = self._server.server_address[1]
        self._thread = threading.Thread(target=self._server.serve_forever, daemon=True)
        self._thread.start()

    @property
    def url(self) -> str:
        return f"http://127.0.0.1:{self.port}/events"

    def _make_handler(self):
        stub = self

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self) -> None:  # noqa: N802
                length = int(self.headers.get("Content-Length") or 0)
                raw = self.rfile.read(length) if length else b"{}"
                try:
                    body = json.loads(raw)
                except json.JSONDecodeError:
                    body = None
                with stub._lock:
                    stub.requests.append({"headers": dict(self.headers), "body": body})
                self.send_response(200)
                self.end_headers()

            def log_message(self, *args: object) -> None:  # silence
                pass

        return Handler

    def stop(self) -> None:
        self._server.shutdown()
        self._server.server_close()
