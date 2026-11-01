#!/usr/bin/env python3
"""Supervisor entrypoint (P02). On start: decide whether to replay the last
healthy argv (or, on first boot, the compose command's argv), spawn the
child if so, record the outcome ("healthy" in this phase = child alive and
its /health returns 200 -- the full state machine arrives in P03a), then
serve the admin API. A dumb process manager -- never reads Postgres, never
resolves recipes.
"""
from __future__ import annotations

import os
import sys
import time
import urllib.error
import urllib.request

import uvicorn

from supervisor.admin_api import create_app
from supervisor.process_manager import ProcessManager
from supervisor.state import RETRY_CAP, StateStore, decide_boot_action

BOOT_HEALTH_TIMEOUT_SECONDS = float(os.environ.get("BOOT_HEALTH_TIMEOUT_SECONDS", "30"))
HEALTH_POLL_INTERVAL_SECONDS = 1.0
RETRY_DELAY_SECONDS = 2.0
DEFAULT_CHILD_PORT = 9999
ADMIN_PORT = int(os.environ.get("ADMIN_PORT", "8000"))  # TBD per doc 5 -- any free port works; firmed up by P15b


def extract_port(argv: list[str], default: int = DEFAULT_CHILD_PORT) -> int:
    if "--port" in argv:
        idx = argv.index("--port")
        if idx + 1 < len(argv):
            try:
                return int(argv[idx + 1])
            except ValueError:
                pass
    return default


def wait_for_health(port: int, timeout: float) -> bool:
    deadline = time.monotonic() + timeout
    url = f"http://127.0.0.1:{port}/health"
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=2) as resp:
                if resp.status == 200:
                    return True
        except (urllib.error.URLError, ConnectionError, TimeoutError, OSError):
            pass
        time.sleep(HEALTH_POLL_INTERVAL_SECONDS)
    return False


def main() -> None:
    state_store = StateStore()
    process_manager = ProcessManager()

    # entrypoint.sh passes the compose `command` straight through as this
    # script's own argv -- that's the fallback used only when state.json
    # doesn't exist yet (first boot).
    fallback_argv = sys.argv[1:] or None
    loaded_state = state_store.load()
    decision, state = decide_boot_action(loaded_state, fallback_argv)

    print(f"supervisor: boot decision -- replay={decision.should_replay} ({decision.reason})", flush=True)

    # "Consecutive boot replays" (doc 5) are retries WITHIN this one run, not
    # across separate container restarts: decide_boot_action's "only if
    # last_run_outcome == healthy" gate decides whether to attempt a replay
    # AT ALL on this boot; once attempting, a transient failure gets retried
    # (same argv) up to RETRY_CAP times before this run finally gives up and
    # persists a non-healthy outcome. If outcome != healthy stopped every
    # future boot outright after just one failure, consecutive_failed_replays
    # could never reach RETRY_CAP through normal operation -- the NEXT boot
    # would already refuse to replay at all.
    if decision.should_replay and decision.argv:
        argv = decision.argv
        state.last_argv = argv
        while True:
            process_manager.spawn(argv)
            port = extract_port(argv)
            healthy = wait_for_health(port, BOOT_HEALTH_TIMEOUT_SECONDS)

            if healthy:
                state.last_good_argv = argv
                state.last_run_outcome = "healthy"
                state.consecutive_failed_replays = 0
                print(f"supervisor: child healthy on port {port} (pid {process_manager.info.pid}).", flush=True)
                break

            state.last_run_outcome = "crashed" if not process_manager.is_running() else "failed"
            state.consecutive_failed_replays += 1
            print(
                f"supervisor: boot replay did not become healthy within {BOOT_HEALTH_TIMEOUT_SECONDS}s "
                f"(outcome={state.last_run_outcome!r}, consecutive_failed_replays={state.consecutive_failed_replays}).",
                flush=True,
            )
            # Either it already exited on its own, or health timed out while
            # it's still alive -- either way, make sure nothing's left
            # running before the next retry spawns a fresh one.
            process_manager.stop()

            if state.consecutive_failed_replays >= RETRY_CAP:
                print(f"supervisor: retry cap ({RETRY_CAP}) reached -- giving up, staying idle.", flush=True)
                break

            state_store.save(state)  # persist progress before each retry, not just at the end
            time.sleep(RETRY_DELAY_SECONDS)

    state_store.save(state)

    app = create_app(process_manager)
    uvicorn.run(app, host="0.0.0.0", port=ADMIN_PORT, log_level="info")


if __name__ == "__main__":
    main()
