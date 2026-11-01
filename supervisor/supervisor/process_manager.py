"""Spawns and tracks the one child process this supervisor owns (P02). Just
process lifecycle -- no state machine, no GPU release, no deploy API (P03a
onward). stdout/stderr are inherited, not captured: they already land on
this container's own stdout/stderr (the container log), which is all "just
forward to the container log for now" needs.
"""
from __future__ import annotations

import os
import signal
import subprocess
import threading
import time
from dataclasses import dataclass
from typing import Optional


@dataclass
class ChildInfo:
    pid: int
    argv: list[str]
    started_at: float


class ProcessManager:
    def __init__(self) -> None:
        self._process: Optional[subprocess.Popen] = None
        self._info: Optional[ChildInfo] = None

    @property
    def info(self) -> Optional[ChildInfo]:
        return self._info

    def is_running(self) -> bool:
        return self._process is not None and self._process.poll() is None

    def exit_code(self) -> Optional[int]:
        return self._process.poll() if self._process is not None else None

    def spawn(self, argv: list[str], env: Optional[dict[str, str]] = None) -> ChildInfo:
        if self.is_running():
            raise RuntimeError("a child is already running")
        process = subprocess.Popen(
            argv,
            # Its own session/process group: a later stop (P03a) signals the
            # whole group -- the child plus anything IT spawns -- without
            # ever hitting the supervisor itself, which stays in its own.
            start_new_session=True,
            # A deploy's `env` (the recipe's extra_env) is merged OVER this
            # container's own env, never replacing it (doc 5) -- None here
            # means "inherit unchanged", same as before env support existed.
            env={**os.environ, **env} if env else None,
        )
        self._process = process
        self._info = ChildInfo(pid=process.pid, argv=list(argv), started_at=time.time())

        # subprocess.Popen does NOT reap its child just by existing -- only a
        # wait()/poll() call does, via os.waitpid. Without this, a child that
        # exits on its own (crashes, or just finishes) between boot and the
        # next time something happens to call stop()/poll() sits as a zombie
        # under the supervisor's own PID the whole time (found by hand while
        # verifying this phase's "no zombie" DoD bullet -- that bullet is
        # about grandchild-to-PID-1 reaping, which tini/docker-init already
        # handles; this is the supervisor's OWN reaping of its direct child,
        # a separate gap the same investigation surfaced). One background
        # thread per spawn, doing the one blocking wait(), is enough -- it's
        # also then the only caller of Popen.wait(), so stop() below never
        # races it by waiting on the same Popen concurrently.
        threading.Thread(target=process.wait, daemon=True).start()
        return self._info

    def stop(self, graceful_timeout: float = 5.0) -> None:
        """SIGTERM the child's process group, wait, SIGKILL if it's still
        alive. Matches doc 5's stop sequence (full VRAM-release check is
        P03a/P04 -- out of scope here). Polls rather than calling wait()
        itself -- the reaper thread from spawn() already owns that call."""
        if self._process is None or not self.is_running():
            self._process = None
            return
        pgid = os.getpgid(self._process.pid)
        try:
            os.killpg(pgid, signal.SIGTERM)
            if not self._poll_until_exited(graceful_timeout):
                os.killpg(pgid, signal.SIGKILL)
                self._poll_until_exited(timeout=5.0)
        except ProcessLookupError:
            pass  # already gone
        finally:
            self._process = None

    def _poll_until_exited(self, timeout: float) -> bool:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self._process is None or self._process.poll() is not None:
                return True
            time.sleep(0.1)
        return self._process is None or self._process.poll() is not None
