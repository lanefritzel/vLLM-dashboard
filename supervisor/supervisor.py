#!/usr/bin/env python3
"""Supervisor entrypoint (P02 + P03a + P03c). On start: decide whether to
replay the last healthy argv (or, on first boot, the compose command's
argv), spawn the child if so via the same deploy state machine
POST /admin/deploy uses, retrying in-process up to RETRY_CAP times before
giving up and persisting a non-healthy outcome. Then serve the admin API.
A dumb process manager -- never reads Postgres, never resolves recipes.

Everything (the boot sequence, the event-push background task, and the
admin API's own server) runs on ONE event loop for the whole process --
using asyncio.run() separately for the boot sequence and then the
(synchronous) uvicorn.run() for the server would tear the first loop down
the moment the boot coroutine returns, destroying anything still scheduled
on it (e.g. the event pusher's sender task, started during boot). Found by
hand while wiring P03c; same root cause as a test-harness issue hit twice
already while writing this phase's tests.
"""
from __future__ import annotations

import asyncio
import os
import sys

import uvicorn

from supervisor.admin_api import create_app
from supervisor.deploy_runner import DeployRunner
from supervisor.downloads import DownloadManager, HfClient
from supervisor.event_push import EventPusher
from supervisor.gpu_backend import detect_backend
from supervisor.process_manager import ProcessManager
from supervisor.state import RETRY_CAP, StateStore, decide_boot_action

# Was 30s by default -- fine for the fake-vllm test double, far too short
# for a real model (confirmed live, P16a: boot-replay of an already-known-
# good 27B model timed out and was killed every time, despite loading fine
# moments earlier via a user-initiated swap, which already got bumped to
# 15 min for the same reason). Same stopgap value as SwapService's
# DefaultHealthTimeout; a real fix is a configurable per-recipe timeout
# (tracked separately, not done here).
BOOT_HEALTH_TIMEOUT_SECONDS = float(os.environ.get("BOOT_HEALTH_TIMEOUT_SECONDS", "900"))
RETRY_DELAY_SECONDS = 2.0
ADMIN_PORT = int(os.environ.get("ADMIN_PORT", "8000"))  # TBD per doc 5 -- any free port works; firmed up by P15b
# Loopback by default -- real deployments use network_mode: host, where this
# IS the host's own 127.0.0.1, making the admin API unreachable from the LAN
# with zero config. Only ever overridden by the test suite's own
# SupervisorImageFixture, which launches this in an ordinary bridge-networked
# container: a process bound to 127.0.0.1 there is unreachable even through
# Docker's own published port (confirmed directly: a bridge-mapped port to a
# loopback-bound process resets the connection, it doesn't just time out) --
# that fixture passes ADMIN_BIND_HOST=0.0.0.0 to stay reachable from the host
# test process, which is fine there since it's an ephemeral per-test network,
# not a real deployment exposed to anything.
ADMIN_BIND_HOST = os.environ.get("ADMIN_BIND_HOST", "127.0.0.1")
DASHBOARD_EVENT_URL = os.environ.get("DASHBOARD_EVENT_URL")  # unset -- event push disabled cleanly (P03c)
HF_TOKEN = os.environ.get("HF_TOKEN")  # P08: env var only, never the DB or an API body
MODELS_ROOT = os.environ.get("MODELS_ROOT", "/models")  # compose mounts the host models root here


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


async def main_async() -> None:
    state_store = StateStore()
    process_manager = ProcessManager()
    event_pusher = EventPusher(url=DASHBOARD_EVENT_URL)
    gpu_backend = detect_backend(process_manager)  # once, at start (doc 5) -- nvidia-smi, then Intel sysfs, else generic
    print(f"supervisor: GPU backend detected -- {gpu_backend.vendor}", flush=True)
    deploy_runner = DeployRunner(process_manager, state_store, event_pusher, gpu_backend)
    download_manager = DownloadManager(HfClient(token=HF_TOKEN), models_root=MODELS_ROOT)

    await run_boot_sequence(deploy_runner, process_manager, state_store)

    app = create_app(process_manager, deploy_runner, state_store, download_manager)
    # ADMIN_BIND_HOST defaults to loopback: the dashboard always runs on this
    # same host (network_mode: host) and reaches this over 127.0.0.1, so the
    # admin API -- which can deploy/stop/redeploy models -- is never
    # reachable from the LAN by default. That's what actually protects it;
    # there used to be a bearer-token check on top, but once this can't be
    # reached from anywhere but this machine, a password here was protecting
    # against nothing that local shell/Docker access doesn't already grant.
    config = uvicorn.Config(app, host=ADMIN_BIND_HOST, port=ADMIN_PORT, log_level="info")
    await uvicorn.Server(config).serve()


def main() -> None:
    asyncio.run(main_async())


if __name__ == "__main__":
    main()
