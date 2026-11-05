"""P03c's Definition of done, against a real recording stub (no mocks for
the HTTP layer) and real deploys (the fake vLLM test double from P00).
"""
import asyncio
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

from recording_stub import RecordingStub

from supervisor.deploy_runner import DeployRunner
from supervisor.event_push import EventPusher, PushItem
from supervisor.log_store import DeploymentArchive
from supervisor.process_manager import ProcessManager
from supervisor.state import StateStore

FAKE_VLLM = str(Path(__file__).parent.parent.parent / "tests" / "fake-vllm" / "fake_vllm.py")


def run(coro):
    return asyncio.run(coro)


def new_runner(tmp_path, url: str) -> tuple[DeployRunner, ProcessManager, EventPusher]:
    pm = ProcessManager()
    pusher = EventPusher(url=url)
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), pusher, archive=DeploymentArchive(tmp_path))
    return runner, pm, pusher


def free_port() -> int:
    import socket

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def test_deploy_pushes_state_events_in_order_with_a_final_ready_and_a_log_batch(tmp_path):
    stub = RecordingStub()
    try:
        runner, pm, pusher = new_runner(tmp_path, stub.url)
        port = free_port()

        async def scenario():
            argv = [sys.executable, FAKE_VLLM, "--port", str(port), "--served-model-name", "model-a", "--fake-slow-start", "2"]
            ok = await runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=10)
            assert ok is True
            # Let the sender task flush the queue.
            for _ in range(50):
                if not pusher._queue:
                    break
                await asyncio.sleep(0.1)

        try:
            run(scenario())
        finally:
            pm.stop()

        state_pushes = [r["body"] for r in stub.requests if r["body"]["kind"] == "state"]
        log_pushes = [r["body"] for r in stub.requests if r["body"]["kind"] == "log"]

        states = [p["state"] for p in state_pushes]
        assert states == ["starting", "loading", "waiting_health", "ready", "smoke"]
        seqs = [r["body"]["seq"] for r in stub.requests]
        assert seqs == sorted(seqs)
        assert len(set(seqs)) == len(seqs)
        assert len(log_pushes) >= 1, "expected at least one log batch during the ~2s slow start"
    finally:
        stub.stop()


def test_failing_deploy_pushes_failed_state_and_a_separate_report(tmp_path):
    stub = RecordingStub()
    try:
        runner, pm, pusher = new_runner(tmp_path, stub.url)

        async def scenario():
            argv = [sys.executable, FAKE_VLLM, "--fake-crash"]
            ok = await runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=10)
            assert ok is False
            for _ in range(50):
                if not pusher._queue:
                    break
                await asyncio.sleep(0.1)

        try:
            run(scenario())
        finally:
            pm.stop()

        kinds = [r["body"]["kind"] for r in stub.requests]
        assert "state" in kinds and "report" in kinds
        failed_state = next(r["body"] for r in stub.requests if r["body"]["kind"] == "state" and r["body"]["state"] == "failed")
        report_push = next(r["body"] for r in stub.requests if r["body"]["kind"] == "report")
        assert report_push["report"]["reason"] == "process_exited"
        assert report_push["report"]["exit_code"] == 1
        assert report_push["report"]["log_tail"]
        assert report_push["seq"] > failed_state["seq"]
    finally:
        stub.stop()


def test_every_push_has_no_auth_header(tmp_path):
    # Loopback-only change: the dashboard's event receiver is reached over
    # 127.0.0.1 same as the admin API, so pushes no longer carry a bearer
    # token at all -- this just confirms nothing still tries to.
    stub = RecordingStub()
    try:
        runner, pm, pusher = new_runner(tmp_path, stub.url)
        port = free_port()

        async def scenario():
            argv = [sys.executable, FAKE_VLLM, "--port", str(port), "--served-model-name", "model-a"]
            await runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=10)
            for _ in range(50):
                if not pusher._queue:
                    break
                await asyncio.sleep(0.1)

        try:
            run(scenario())
        finally:
            pm.stop()

        assert len(stub.requests) > 0
        for r in stub.requests:
            assert "Authorization" not in r["headers"]
    finally:
        stub.stop()


def test_deploy_completes_while_stub_is_down_and_delivers_once_it_restarts(tmp_path):
    stub = RecordingStub()
    stub_port = stub.port
    stub.stop()  # down for the WHOLE deploy -- never up while it runs

    runner, pm, pusher = new_runner(tmp_path, f"http://127.0.0.1:{stub_port}/events")
    port = free_port()

    async def scenario():
        # One continuous event loop for the whole scenario -- the sender
        # task started by the deploy below must still be the one draining
        # the queue once the stub comes back; a second, separate
        # asyncio.run() call here would tear down the loop it's scheduled
        # on and leave nothing to redeliver anything (hit this same class
        # of bug repeatedly already in this phase and P03a -- see
        # supervisor.py's own module docstring).
        argv = [sys.executable, FAKE_VLLM, "--port", str(port), "--served-model-name", "model-a"]
        start = time.monotonic()
        ok = await runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=10)
        elapsed = time.monotonic() - start
        assert ok is True, "the deploy must succeed even though the event stub is unreachable"
        assert elapsed < 10, "push failures must never block the state machine"

        # Restart a stub bound to the SAME port -- the sender task's own
        # backoff loop should pick it back up without anything else.
        restarted = RecordingStub(port=stub_port)
        try:
            for _ in range(100):
                if any(r["body"]["kind"] == "state" and r["body"]["state"] == "ready" for r in restarted.requests):
                    break
                await asyncio.sleep(0.3)
            else:
                raise AssertionError("queued events were never delivered after the stub came back")

            # The dashboard's catch-up path: it missed everything up to
            # whatever it last saw -- say seq 0 (nothing) -- and GET
            # .../events?after=0 must return exactly what the stub missed.
            missed = runner.events_for("d1", after=0)
            assert [e.state for e in missed] == ["starting", "loading", "waiting_health", "ready", "smoke"]
        finally:
            restarted.stop()

    try:
        run(scenario())
    finally:
        pm.stop()


def test_disabled_when_url_unset_makes_no_network_calls(tmp_path):
    pm = ProcessManager()
    pusher = EventPusher(url=None)
    runner = DeployRunner(pm, StateStore(tmp_path / "state.json"), pusher, archive=DeploymentArchive(tmp_path))
    port = free_port()

    async def scenario():
        argv = [sys.executable, FAKE_VLLM, "--port", str(port), "--served-model-name", "model-a"]
        ok = await runner.deploy_and_wait(argv, deployment_id="d1", health_timeout=10)
        assert ok is True

    try:
        run(scenario())  # must not raise, must not hang
    finally:
        pm.stop()

    assert pusher.enabled is False
    assert pusher._queue == []
    assert pusher._task is None


def test_queue_full_drops_oldest_log_items_but_never_state_or_report():
    # port 1: nothing listens there -- every send attempt just fails/retries,
    # so pushed items sit in the queue for these assertions to inspect.
    pusher = EventPusher(url="http://127.0.0.1:1", max_queue_size=5)

    for i in range(3):
        pusher.push(PushItem("d1", i, time.time(), "log", {"lines": [f"line{i}"]}, droppable=True))
    pusher.push(PushItem("d1", 10, time.time(), "state", {"state": "starting"}, droppable=False))
    pusher.push(PushItem("d1", 11, time.time(), "state", {"state": "loading"}, droppable=False))
    assert [(i.kind, i.seq) for i in pusher._queue] == [
        ("log", 0), ("log", 1), ("log", 2), ("state", 10), ("state", 11),
    ]

    # At capacity (5): evicts the OLDEST droppable item anywhere in the
    # queue (log seq 0), not a state item, then appends the new one.
    pusher.push(PushItem("d1", 20, time.time(), "log", {"lines": ["line20"]}, droppable=True))
    assert [(i.kind, i.seq) for i in pusher._queue] == [
        ("log", 1), ("log", 2), ("state", 10), ("state", 11), ("log", 20),
    ]

    # Same again: evicts the next-oldest log (seq 1).
    pusher.push(PushItem("d1", 21, time.time(), "log", {"lines": ["line21"]}, droppable=True))
    assert [(i.kind, i.seq) for i in pusher._queue] == [
        ("log", 2), ("state", 10), ("state", 11), ("log", 20), ("log", 21),
    ]

    # A report: still evicts the oldest log (seq 2) rather than a state --
    # state/report are never the ones dropped.
    pusher.push(PushItem("d1", 30, time.time(), "report", {"report": {}}, droppable=False))
    assert [(i.kind, i.seq) for i in pusher._queue] == [
        ("state", 10), ("state", 11), ("log", 20), ("log", 21), ("report", 30),
    ]

    pusher.push(PushItem("d1", 22, time.time(), "log", {"lines": ["line22"]}, droppable=True))  # evicts log20
    pusher.push(PushItem("d1", 23, time.time(), "log", {"lines": ["line23"]}, droppable=True))  # evicts log21
    assert [(i.kind, i.seq) for i in pusher._queue] == [
        ("state", 10), ("state", 11), ("report", 30), ("log", 22), ("log", 23),
    ]

    # Finally, drain the one remaining log with two more non-droppable
    # pushes: the first still finds log22/23 to evict; the second has
    # nothing droppable left at all, so the queue grows past the nominal
    # cap rather than drop a state/report item.
    pusher.push(PushItem("d1", 32, time.time(), "report", {"report": {}}, droppable=False))  # evicts log22
    assert len(pusher._queue) == 5
    pusher.push(PushItem("d1", 33, time.time(), "report", {"report": {}}, droppable=False))  # evicts log23 -- now zero logs left
    assert len(pusher._queue) == 5
    assert not any(i.droppable for i in pusher._queue)

    pusher.push(PushItem("d1", 34, time.time(), "report", {"report": {}}, droppable=False))  # nothing droppable to evict -- grows past cap
    assert len(pusher._queue) == 6
    assert all(not i.droppable for i in pusher._queue)
