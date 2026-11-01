"""Spawns and tracks the one child process this supervisor owns (P02/P03b).
Process lifecycle, plus (P03b) a bounded tail of its combined stdout/stderr
for the failure report -- still forwarded line-by-line to this container's
own stdout as it's captured, so "just forward to the container log" (P02)
still holds; no state machine, no GPU release (P03a/P04 onward).
"""
from __future__ import annotations

import os
import signal
import subprocess
import sys
import threading
import time
from collections import deque
from dataclasses import dataclass
from typing import Optional

LOG_TAIL_LINES = 50


@dataclass
class ChildInfo:
    pid: int
    argv: list[str]
    started_at: float


class ProcessManager:
    def __init__(self) -> None:
        self._process: Optional[subprocess.Popen] = None
        self._info: Optional[ChildInfo] = None
        self._log_tail: deque[str] = deque(maxlen=LOG_TAIL_LINES)

    @property
    def info(self) -> Optional[ChildInfo]:
        return self._info

    def is_running(self) -> bool:
        return self._process is not None and self._process.poll() is None

    def exit_code(self) -> Optional[int]:
        return self._process.poll() if self._process is not None else None

    def log_tail(self) -> str:
        """Last ~50 lines of the current/most recent child's combined
        stdout+stderr, for the failure report (P03b)."""
        return "\n".join(self._log_tail)

    def spawn(self, argv: list[str], env: Optional[dict[str, str]] = None) -> ChildInfo:
        if self.is_running():
            raise RuntimeError("a child is already running")
        self._log_tail = deque(maxlen=LOG_TAIL_LINES)
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
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            bufsize=1,
        )
        self._process = process
        self._info = ChildInfo(pid=process.pid, argv=list(argv), started_at=time.time())

        # One background thread does three things in sequence: pump the
        # child's combined output into the bounded tail (for the failure
        # report) while echoing each line to our own stdout (so it still
        # reaches the container log, same as before output was captured at
        # all); then, once the pipe hits EOF (the child closed its fds, i.e.
        # it's exited or about to), call wait() to actually reap it --
        # subprocess.Popen does NOT reap a child just by existing, only a
        # wait()/poll() call does (via os.waitpid); skipping this leaves a
        # zombie under the supervisor's own PID (found by hand verifying
        # P02's "no zombie" DoD bullet -- that one is about grandchild-to-
        # PID-1 reaping, which tini/docker-init already handles; this is a
        # separate gap the same investigation surfaced). This is also the
        # ONLY caller of Popen.wait(), so stop() below never races it.
        threading.Thread(target=self._pump_and_reap, args=(process,), daemon=True).start()
        return self._info

    def _pump_and_reap(self, process: subprocess.Popen) -> None:
        try:
            if process.stdout is not None:
                for line in process.stdout:
                    self._log_tail.append(line.rstrip("\n"))
                    sys.stdout.write(line)
                    sys.stdout.flush()
        finally:
            process.wait()

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
