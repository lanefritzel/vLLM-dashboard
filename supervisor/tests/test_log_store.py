"""P05's Definition of done: ring buffer stays bounded, per-deployment logs
cap out instead of growing unbounded, and the 50-deployment rotation keeps
exactly the most recent ones.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

from supervisor.log_store import DeploymentArchive, RingBuffer


def test_ring_buffer_stays_bounded_by_bytes():
    buf = RingBuffer(max_bytes=1000)
    line = "x" * 100  # 101 bytes incl. the joining newline this module accounts for
    for _ in range(50):  # 50 * 101 = 5050 bytes, well over the 1000 cap
        buf.append(line)

    tail = buf.tail()
    assert len(tail.encode()) <= 1000 + 101  # one line's worth of slack is fine
    assert tail.count(line) < 50  # older lines were evicted


def test_ring_buffer_tail_limits_by_line_count():
    buf = RingBuffer(max_bytes=10_000)
    for i in range(10):
        buf.append(f"line{i}")

    assert buf.tail(3) == "line7\nline8\nline9"
    assert buf.tail() == "\n".join(f"line{i}" for i in range(10))


def test_ring_buffer_clear_empties_it():
    buf = RingBuffer()
    buf.append("something")
    buf.clear()
    assert buf.tail() == ""


def test_deployment_log_stops_writing_once_at_its_cap(tmp_path):
    archive = DeploymentArchive(tmp_path)
    archive.register_deployment("d1")
    cap = 200
    import supervisor.log_store as log_store_module
    original_cap = log_store_module.DEPLOYMENT_LOG_MAX_BYTES
    log_store_module.DEPLOYMENT_LOG_MAX_BYTES = cap
    try:
        line = "x" * 50
        for _ in range(20):  # 20 * 51 bytes = 1020, well over a 200-byte cap
            archive.append_log_line("d1", line)
        size = (tmp_path / "logs" / "d1.log").stat().st_size
        assert size <= cap + 51  # stops at (or just past) the cap, not unbounded
    finally:
        log_store_module.DEPLOYMENT_LOG_MAX_BYTES = original_cap


def test_deployment_log_read_tail_limits_by_lines(tmp_path):
    archive = DeploymentArchive(tmp_path)
    archive.register_deployment("d1")
    for i in range(10):
        archive.append_log_line("d1", f"line{i}")

    assert archive.read_log_tail("d1", lines=3) == "line7\nline8\nline9"
    assert archive.read_log_tail("d1") == "\n".join(f"line{i}" for i in range(10))
    assert archive.read_log_tail("does-not-exist") == ""


def test_event_persistence_round_trips(tmp_path):
    archive = DeploymentArchive(tmp_path)
    archive.register_deployment("d1")
    archive.append_event("d1", {"deployment_id": "d1", "seq": 1, "ts": 1.0, "state": "starting", "detail": None})
    archive.append_event("d1", {"deployment_id": "d1", "seq": 2, "ts": 2.0, "state": "ready", "detail": None})

    events = archive.load_events("d1")
    assert [e["state"] for e in events] == ["starting", "ready"]
    assert archive.load_events("does-not-exist") == []


def test_rotation_keeps_only_the_last_fifty_deployments(tmp_path):
    archive = DeploymentArchive(tmp_path)
    for i in range(1, 52):  # 51 distinct deployments
        deployment_id = f"d{i}"
        archive.register_deployment(deployment_id)
        archive.append_log_line(deployment_id, f"hello from {deployment_id}")
        archive.append_event(deployment_id, {"deployment_id": deployment_id, "seq": 1, "ts": 1.0, "state": "ready", "detail": None})

    # d1's files are gone -- rotated off once d51 pushed the count past 50.
    assert archive.read_log_tail("d1") == ""
    assert archive.load_events("d1") == []

    # d2..d51 (50 deployments) are all still present and independently retrievable.
    for i in range(2, 52):
        deployment_id = f"d{i}"
        assert archive.read_log_tail(deployment_id) == f"hello from {deployment_id}"
        assert archive.load_events(deployment_id)[0]["state"] == "ready"
