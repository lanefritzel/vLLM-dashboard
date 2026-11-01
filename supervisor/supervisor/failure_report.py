"""Failure classification + report (P03b). Best-effort, in this fixed
order: health_timeout (the supervisor gave up while the child was still
alive) -> out_of_memory -> bad_args -> process_exited (the fallback for
anything else, including a plain nonzero exit).
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Optional

OOM_MARKERS = ("outofmemory", "out of memory", "oom")
BAD_ARGS_MARKERS = ("unrecognized arguments", "invalid choice", "error: argument")
OOM_EXIT_CODE = 137  # 128 + SIGKILL(9) -- the OOM killer's signature signal death
BAD_ARGS_EXIT_CODE = 2  # argparse's own exit code for a parsing error


@dataclass
class FailureReport:
    reason: str  # "process_exited" | "health_timeout" | "bad_args" | "out_of_memory"
    exit_code: Optional[int]
    log_tail: str
    # P04: set when releasing_gpu gave up with VRAM still looking elevated
    # right before this deploy attempt -- lets the UI mention "VRAM was
    # still in use" specifically when the NEW process then failed to
    # allocate (doc 5), rather than guessing from the generic reason alone.
    vram_still_in_use: bool = False

    def to_dict(self) -> dict:
        return {
            "reason": self.reason,
            "exit_code": self.exit_code,
            "log_tail": self.log_tail,
            "vram_still_in_use": self.vram_still_in_use,
        }


def classify(exit_code: Optional[int], log_tail: str, timed_out_while_alive: bool) -> str:
    if timed_out_while_alive:
        return "health_timeout"

    tail_lower = log_tail.lower()
    if exit_code == OOM_EXIT_CODE or any(marker in tail_lower for marker in OOM_MARKERS):
        return "out_of_memory"
    if exit_code == BAD_ARGS_EXIT_CODE or any(marker in tail_lower for marker in BAD_ARGS_MARKERS):
        return "bad_args"
    return "process_exited"


def build_report(
    exit_code: Optional[int], log_tail: str, timed_out_while_alive: bool, vram_still_in_use: bool = False,
) -> FailureReport:
    return FailureReport(
        reason=classify(exit_code, log_tail, timed_out_while_alive),
        exit_code=exit_code,
        log_tail=log_tail,
        vram_still_in_use=vram_still_in_use,
    )
