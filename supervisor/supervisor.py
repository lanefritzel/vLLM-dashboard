#!/usr/bin/env python3
"""Supervisor entrypoint (P02 + P03a). On start: decide whether to replay
the last healthy argv (or, on first boot, the compose command's argv),
spawn the child if so via the same deploy state machine POST /admin/deploy
uses, retrying in-process up to RETRY_CAP times before giving up and
persisting a non-healthy outcome. Then serve the admin API. A dumb process
manager -- never reads Postgres, never resolves recipes.
"""
from __future__ import annotations

import asyncio
import os
import sys

import uvicorn

from supervisor.admin_api import create_app
from supervisor.deploy_runner import DeployRunner
from supervisor.process_manager import ProcessManager
from supervisor.state import RETRY_CAP, StateStore, decide_boot_action

BOOT_HEALTH_TIMEOUT_SECONDS = float(os.environ.get("BOOT_HEALTH_TIMEOUT_SECONDS", "30"))
RETRY_DELAY_SECONDS = 2.0
ADMIN_PORT = int(os.environ.get("ADMIN_PORT", "8000"))  # TBD per doc 5 -- any free port works; firmed up by P15b
SUPERVISOR_TOKEN = os.environ.get("SUPERVISOR_TOKEN")  # unset -- and every /admin/* route except /health 401s


async def run_boot_sequence(deploy_runner: DeployRunner, process_manager: ProcessManager, state_store: StateStore) -> None:
    fallback_argv = sys.argv[1:] or None  # entrypoint.sh passes the compose `command` through, used only on first boot
    loaded_state = state_store.load()
    decision, state = decide_boot_action(loaded_state, fallback_argv)
    state_store.save(state)  # first-boot case creates+persists state even before any deploy attempt

    print(f"supervisor: boot decision -- replay={decision.should_replay} ({decision.reason})", flush=True)
    if not (decision.should_replay and decision.argv):
        return

    argv = decision.argv
    while True:
        healthy = await deploy_runner.deploy_and_wait(argv, health_timeout=BOOT_HEALTH_TIMEOUT_SECONDS)

        if healthy:
            info = process_manager.info
            print(f"supervisor: boot replay healthy (pid {info.pid if info else '?'}).", flush=True)
            break

        # deploy_and_wait() already persisted last_argv/last_run_outcome=
        # "failed"/last_failure_report (P03b) -- "failed" always, never
        # "crashed", since by definition a boot replay that didn't return
        # True here never reached ready (crashed is reserved for a child
        # that died AFTER ready, tracked separately by DeployRunner's own
        # post-ready watcher). This just adds the retry-cap bookkeeping,
        # which is boot-sequence-specific, on top of that.
        state = state_store.load() or state
        state.consecutive_failed_replays += 1
        state_store.save(state)
        reason = (state.last_failure_report or {}).get("reason")
        print(
            f"supervisor: boot replay did not become healthy within {BOOT_HEALTH_TIMEOUT_SECONDS}s "
            f"(reason={reason!r}, consecutive_failed_replays={state.consecutive_failed_replays}).",
            flush=True,
        )
        if state.consecutive_failed_replays >= RETRY_CAP:
            print(f"supervisor: retry cap ({RETRY_CAP}) reached -- giving up, staying idle.", flush=True)
            break
        # No manual stop() here: the next deploy_and_wait() call's own state
        # machine already stops a still-running-but-unhealthy child (the
        # "failed" case) via its normal stopping/releasing_gpu steps before
        # starting a fresh one -- a "crashed" child is already gone.
        await asyncio.sleep(RETRY_DELAY_SECONDS)


def main() -> None:
    state_store = StateStore()
    process_manager = ProcessManager()
    deploy_runner = DeployRunner(process_manager, state_store)

    asyncio.run(run_boot_sequence(deploy_runner, process_manager, state_store))

    app = create_app(process_manager, deploy_runner, state_store, SUPERVISOR_TOKEN)
    uvicorn.run(app, host="0.0.0.0", port=ADMIN_PORT, log_level="info")


if __name__ == "__main__":
    main()
