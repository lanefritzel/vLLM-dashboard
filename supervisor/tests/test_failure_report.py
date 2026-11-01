"""The classifier table from P03b's Definition of done, verbatim."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

from supervisor.failure_report import build_report, classify


def test_exit_137_is_out_of_memory_regardless_of_tail():
    assert classify(exit_code=137, log_tail="anything at all", timed_out_while_alive=False) == "out_of_memory"


def test_torch_oom_error_in_tail_is_out_of_memory():
    assert classify(exit_code=1, log_tail="...\ntorch.OutOfMemoryError: CUDA out of memory\n", timed_out_while_alive=False) == "out_of_memory"


def test_oom_marker_case_insensitive():
    assert classify(exit_code=1, log_tail="Killed (OOM)", timed_out_while_alive=False) == "out_of_memory"


def test_unrecognized_arguments_is_bad_args():
    assert classify(exit_code=2, log_tail="error: unrecognized arguments: --foo", timed_out_while_alive=False) == "bad_args"


def test_bad_args_marker_without_matching_exit_code_still_classifies():
    assert classify(exit_code=1, log_tail="argparse.ArgumentError: invalid choice: 'x'", timed_out_while_alive=False) == "bad_args"


def test_exit_code_2_alone_is_bad_args():
    assert classify(exit_code=2, log_tail="", timed_out_while_alive=False) == "bad_args"


def test_plain_exit_1_is_process_exited():
    assert classify(exit_code=1, log_tail="Traceback (most recent call last): ...", timed_out_while_alive=False) == "process_exited"


def test_timeout_with_live_child_is_health_timeout():
    assert classify(exit_code=None, log_tail="still starting up...", timed_out_while_alive=True) == "health_timeout"


def test_health_timeout_takes_precedence_over_other_markers():
    # timed_out_while_alive=True means the child never exited at all --
    # exit_code/tail markers are irrelevant; it's whatever's still printing.
    assert classify(exit_code=137, log_tail="OOM", timed_out_while_alive=True) == "health_timeout"


def test_build_report_carries_exit_code_and_tail_through():
    report = build_report(exit_code=1, log_tail="line1\nline2", timed_out_while_alive=False)
    assert report.reason == "process_exited"
    assert report.exit_code == 1
    assert report.log_tail == "line1\nline2"
    assert report.to_dict() == {
        "reason": "process_exited", "exit_code": 1, "log_tail": "line1\nline2", "vram_still_in_use": False,
    }
