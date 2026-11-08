"""Tests that mimic real-world use without a GPU or vLLM.

1. FakeEngineTests: 64 fake layers driven like a real decode loop (single-token
   steps, MTP-style multi-token verification steps, one big prefill), with the
   real worker thread and the real HTTP server.
2. ContractTests: snapshots recorded from the real Intel GPU run must satisfy
   the data contract the dashboard page relies on, and the replay server must
   serve that same contract.
"""
import base64
import json
import os
import threading
import time
import unittest
import urllib.request
from http.server import ThreadingHTTPServer

import torch
from torch import nn

from vllm_visualizer import replay, tap

WIDTH = 17408
LAYERS = 64
FIXTURE = os.path.join(os.path.dirname(__file__), "fixtures", "real_snapshots.jsonl")


class _Act(nn.Module):
    def __init__(self, tokens_fn):
        super().__init__()
        self.tokens_fn = tokens_fn

    def forward(self, x):
        return torch.randn(self.tokens_fn(), WIDTH).half()


class _FakeMlp:
    def __init__(self, tokens_fn):
        self.act_fn = _Act(tokens_fn)


class FakeEngineTests(unittest.TestCase):
    def setUp(self):
        self._saved = (tap._make_event, tap.EVERY, tap.ARM_TTL, tap.IDLE_GAP)
        tap.S = tap._State()
        tap.S.server_started = True
        tap.EVERY = 2
        self.tokens = 1
        self.mlps = []
        for i in range(LAYERS):
            m = _FakeMlp(lambda: self.tokens)
            tap._attach(m, f"language_model.model.layers.{i}.mlp", WIDTH)
            self.mlps.append(m)
        # also the MTP drafter layer, which must be ignored
        mtp = _FakeMlp(lambda: self.tokens)
        tap._attach(mtp, "mtp.layers.0.mlp", WIDTH)
        self.assertEqual(len(mtp.act_fn._forward_hooks), 0)
        threading.Thread(target=tap._worker, daemon=True).start()
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), tap._Handler)
        self.port = self.server.server_address[1]
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        tap._make_event, tap.EVERY, tap.ARM_TTL, tap.IDLE_GAP = self._saved

    def _step(self, tokens):
        self.tokens = tokens
        for m in self.mlps:
            m.act_fn(torch.zeros(1))

    def test_full_flow_with_decode_mtp_and_prefill_steps(self):
        stream = urllib.request.urlopen(f"http://127.0.0.1:{self.port}/stream", timeout=10)
        time.sleep(0.3)  # viewer connected -> tap armed
        self._step(200)  # prefill of a long prompt
        for i in range(30):
            self._step(5 if i % 3 == 0 else 1)  # decode, with MTP-style 5-token steps
            time.sleep(0.01)
        seen = []
        deadline = time.time() + 10
        while len(seen) < 5 and time.time() < deadline:
            line = stream.readline().decode()
            if line.startswith("data: "):
                seen.append(json.loads(line[6:]))
        stream.close()
        self.assertGreaterEqual(len(seen), 5)
        seqs = [s["seq"] for s in seen]
        self.assertEqual(seqs, sorted(seqs))
        for s in seen:
            self.assertEqual(s["layers"], LAYERS)  # MTP layer not counted
            self.assertEqual(s["buckets"], WIDTH // tap.BUCKET)
            self.assertEqual(len(base64.b64decode(s["data"])), LAYERS * s["buckets"])
            self.assertEqual(len(base64.b64decode(s["sign"])), (LAYERS * s["buckets"] + 7) // 8)
        self.assertEqual(tap.S.errors, 0)

    def test_slow_consumer_never_blocks_the_model_loop(self):
        class SlowEvent:
            def record(self):
                pass

            def synchronize(self):
                time.sleep(0.5)  # a stalled device-to-host copy

        tap._make_event = lambda dev: SlowEvent()
        tap.S.armed_until = time.monotonic() + 60
        t0 = time.time()
        for _ in range(60):
            self._step(1)
        elapsed = time.time() - t0
        self.assertLess(elapsed, 10.0)  # the loop kept running regardless
        self.assertGreater(tap.S.dropped, 0)  # and the plugin dropped snapshots instead of waiting
        self.assertEqual(tap.S.errors, 0)

    def test_graceful_disable_never_cuts_an_answer_short_then_switches_off(self):
        saved_gap = tap.IDLE_GAP
        tap.IDLE_GAP = 0.25
        try:
            tap.S.armed_until = time.monotonic() + 60
            req = urllib.request.Request(f"http://127.0.0.1:{self.port}/disable", method="POST", data=b"")
            for i in range(30):                        # one long answer, a step every 20 ms
                self._step(1)
                if i == 5:
                    state = json.load(urllib.request.urlopen(req, timeout=5))
                    self.assertTrue(state["pending_disable"])
                    self.assertFalse(state["disabled"])
                tap._watchdog_tick()
                self.assertFalse(tap.S.disabled, f"switched off mid-answer at step {i}")
                time.sleep(0.02)
            time.sleep(0.4)                            # the answer is returned; the model goes quiet
            tap._watchdog_tick()
            self.assertTrue(tap.S.disabled)
            self.assertEqual(tap.S.disabled_reason, "manual")
        finally:
            tap.IDLE_GAP = saved_gap

    def test_graceful_enable_waits_for_the_next_prompt_in_a_back_to_back_stream(self):
        tap.S.disabled, tap.S.disabled_reason = True, "manual"
        req = urllib.request.Request(f"http://127.0.0.1:{self.port}/enable", method="POST", data=b"")
        for _ in range(10):                          # answer 1 streaming (decode steps)
            self._step(5)
        state = json.load(urllib.request.urlopen(req, timeout=5))
        self.assertTrue(state["pending_enable"])
        for _ in range(10):                          # rest of answer 1: still off
            self._step(5)
        self.assertTrue(tap.S.disabled)
        self._step(200)                              # request 2's prompt arrives immediately
        self.assertFalse(tap.S.disabled)

    def test_no_work_when_nobody_is_watching(self):
        for _ in range(20):
            self._step(1)
        self.assertEqual(tap.S.armed_hook_calls, 0)
        self.assertEqual(tap.S.snapshots, 0)
        self.assertEqual(tap.S.hook_calls, 20 * LAYERS)

    def test_tap_disarms_after_viewer_leaves(self):
        tap.S.armed_until = time.monotonic() + 0.2
        self._step(1)
        self.assertGreater(tap.S.armed_hook_calls, 0)
        time.sleep(0.4)
        before = tap.S.armed_hook_calls
        self._step(1)
        self.assertEqual(tap.S.armed_hook_calls, before)


class ContractTests(unittest.TestCase):
    def test_recorded_real_snapshots_satisfy_the_contract(self):
        snaps = replay.load(FIXTURE)
        self.assertGreaterEqual(len(snaps), 10)
        for s in snaps:
            for key in ("seq", "ts", "layers", "buckets", "layer_max", "max", "data"):
                self.assertIn(key, s)
            raw = base64.b64decode(s["data"])
            self.assertEqual(len(raw), s["layers"] * s["buckets"])
            self.assertEqual(len(s["layer_max"]), s["layers"])
            self.assertAlmostEqual(s["max"], max(s["layer_max"]), places=2)
            for layer in range(s["layers"]):
                row = raw[layer * s["buckets"]:(layer + 1) * s["buckets"]]
                self.assertEqual(max(row), 255)  # per-layer scaling

    def test_real_data_is_sparse(self):
        s = replay.load(FIXTURE)[0]
        raw = base64.b64decode(s["data"])
        hot = sum(1 for v in raw if v > 127)
        # per-layer scaling: measured ~5% of cells above half-bright on the real run
        self.assertLess(hot / len(raw), 0.10)  # the page has something to highlight

    def test_replay_server_serves_the_same_contract(self):
        server = replay.make_server(FIXTURE, interval=0.02)
        port = server.server_address[1]
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            snap = json.load(urllib.request.urlopen(f"http://127.0.0.1:{port}/snapshot", timeout=5))
            self.assertEqual(snap["layers"], 64)
            stream = urllib.request.urlopen(f"http://127.0.0.1:{port}/stream", timeout=5)
            got = []
            while len(got) < 3:
                line = stream.readline().decode()
                if line.startswith("data: "):
                    got.append(json.loads(line[6:]))
            stream.close()
            self.assertEqual(len(got), 3)
            self.assertTrue(json.load(urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=5))["replay"])
        finally:
            server.shutdown()


if __name__ == "__main__":
    unittest.main()
