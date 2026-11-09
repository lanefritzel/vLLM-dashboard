import base64
import json
import os
import sys
import threading
import time
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer

import torch
from torch import nn

from vllm_visualizer import tap

WIDTH = 17408


def sign_bit(snap, layer, bucket):
    """Reads one bit from the packed direction plane (1 = pushed down)."""
    i = layer * snap["buckets"] + bucket
    return (base64.b64decode(snap["sign"])[i >> 3] >> (7 - (i & 7))) & 1


class _Act(nn.Module):
    def __init__(self, value_fn):
        super().__init__()
        self.value_fn = value_fn

    def forward(self, x):
        return self.value_fn(x)


class _FakeMlp:
    def __init__(self, fn):
        self.act_fn = _Act(fn)


def _row_of(peak_bucket, peak):
    t = torch.full((3, WIDTH), 0.01, dtype=torch.float16)
    t[-1, peak_bucket * tap.BUCKET] = peak
    return t


class TapTests(unittest.TestCase):
    def setUp(self):
        self._saved = (tap._make_event, tap.EVERY, tap.MAX_ERRORS)
        tap.S = tap._State()
        tap.S.server_started = True  # never start the real HTTP server here
        tap.EVERY = 1
        tap.MAX_ERRORS = 20

    def tearDown(self):
        tap._make_event, tap.EVERY, tap.MAX_ERRORS = self._saved

    def _layers(self, n, peak_for=lambda i: 1.0):
        mlps = []
        for i in range(n):
            m = _FakeMlp(lambda x, i=i: _row_of(i, peak_for(i)))
            tap._attach(m, f"language_model.model.layers.{i}.mlp", WIDTH)
            mlps.append(m)
        return mlps

    def _start_worker(self):
        threading.Thread(target=tap._worker, daemon=True).start()

    def _wait_snapshot(self, timeout=3.0):
        end = time.time() + timeout
        while time.time() < end:
            if tap.S.latest:
                return tap.S.latest
            time.sleep(0.02)
        self.fail("no snapshot produced")

    def test_unarmed_does_nothing(self):
        mlps = self._layers(3)
        for m in mlps:
            m.act_fn(torch.zeros(1))
        self.assertEqual(tap.S.hook_calls, 3)
        self.assertEqual(tap.S.armed_hook_calls, 0)
        self.assertEqual(tap.S.snapshots, 0)

    def test_armed_capture_and_per_layer_scaling(self):
        mlps = self._layers(3, peak_for=lambda i: [1.0, 50.0, 3.0][i])
        self._start_worker()
        tap.S.armed_until = time.monotonic() + 30
        for m in mlps:
            m.act_fn(torch.zeros(1))
        snap = self._wait_snapshot()
        self.assertEqual(snap["layers"], 3)
        self.assertEqual(snap["buckets"], WIDTH // tap.BUCKET)
        raw = base64.b64decode(snap["data"])
        self.assertEqual(len(raw), 3 * snap["buckets"])
        for layer in range(3):
            row = raw[layer * snap["buckets"]:(layer + 1) * snap["buckets"]]
            self.assertEqual(max(row), 255)  # every layer scaled to its own max
            self.assertEqual(row.index(255), layer)  # peak sits in the expected bucket
        self.assertEqual([round(x) for x in snap["layer_max"]], [1, 50, 3])
        for layer in range(3):                       # all positive here
            self.assertEqual(sign_bit(snap, layer, layer), 0)
        self.assertEqual(len(base64.b64decode(snap["sign"])), (3 * snap["buckets"] + 7) // 8)

    def test_fallback_when_aminmax_unavailable(self):
        mlps = self._layers(3, peak_for=lambda i: [1.0, 50.0, 3.0][i])
        self._start_worker()
        tap.S.armed_until = time.monotonic() + 30
        real = torch.aminmax

        def boom(*a, **k):
            raise RuntimeError("not supported on this device")

        torch.aminmax = boom
        try:
            for m in mlps:
                m.act_fn(torch.zeros(1))
        finally:
            torch.aminmax = real
        snap = self._wait_snapshot()
        self.assertFalse(tap.S.use_aminmax)
        self.assertEqual(tap.S.errors, 0)
        raw = base64.b64decode(snap["data"])
        for layer in range(3):
            row = raw[layer * snap["buckets"]:(layer + 1) * snap["buckets"]]
            self.assertEqual(row.index(255), layer)
        self.assertEqual([round(x) for x in snap["layer_max"]], [1, 50, 3])

    def test_negative_activations_use_absolute_value(self):
        # SiLU(x)*up can be negative; the strongest response may be a large negative.
        m = _FakeMlp(lambda x: -_row_of(5, 9.0))
        tap._attach(m, "language_model.model.layers.0.mlp", WIDTH)
        self._start_worker()
        tap.S.armed_until = time.monotonic() + 30
        m.act_fn(torch.zeros(1))
        snap = self._wait_snapshot()
        raw = base64.b64decode(snap["data"])
        self.assertEqual(raw.index(255), 5)
        self.assertEqual(round(snap["layer_max"][0]), 9)
        self.assertEqual(sign_bit(snap, 0, 5), 1)      # strongest response there was negative
        self.assertEqual(sign_bit(snap, 0, 6), 1)      # the whole row is negative in this test

    def test_every_nth_step(self):
        tap.EVERY = 2
        mlps = self._layers(2)
        self._start_worker()
        tap.S.armed_until = time.monotonic() + 30
        for _ in range(6):
            for m in mlps:
                m.act_fn(torch.zeros(1))
            time.sleep(0.05)
        time.sleep(0.3)
        self.assertEqual(tap.S.step, 6)
        self.assertEqual(tap.S.snapshots, 3)

    def test_mtp_and_visual_layers_ignored(self):
        m = _FakeMlp(lambda x: x)
        tap._attach(m, "mtp.layers.0.mlp", WIDTH)
        tap._attach(m, "visual.blocks.0.layers.1.mlp", WIDTH)
        self.assertEqual(tap.S.max_idx, -1)
        self.assertEqual(len(m.act_fn._forward_hooks), 0)

    def test_slots_exhausted_drops_without_waiting(self):
        mlps = self._layers(2)  # no worker running: slots never come back
        tap.S.armed_until = time.monotonic() + 30
        t0 = time.time()
        for _ in range(8):
            for m in mlps:
                m.act_fn(torch.zeros(1))
        self.assertLess(time.time() - t0, 2.0)
        self.assertEqual(len(tap.S.pending), tap.SLOTS)
        self.assertEqual(tap.S.dropped, 8 - tap.SLOTS)
        self.assertEqual(tap.S.errors, 0)

    def test_slot_returns_to_free_list(self):
        mlps = self._layers(2)
        self._start_worker()
        tap.S.armed_until = time.monotonic() + 30
        for _ in range(20):
            for m in mlps:
                m.act_fn(torch.zeros(1))
            time.sleep(0.03)
        time.sleep(0.3)
        self.assertEqual(len(tap.S.free), tap.SLOTS)
        self.assertEqual(tap.S.errors, 0)

    def test_bad_shape_is_skipped_not_an_error(self):
        m = _FakeMlp(lambda x: torch.zeros(3, 100, dtype=torch.float16))
        tap._attach(m, "language_model.model.layers.0.mlp", WIDTH)
        tap.S.armed_until = time.monotonic() + 30
        m.act_fn(torch.zeros(1))
        self.assertEqual(tap.S.skipped, 1)
        self.assertEqual(tap.S.errors, 0)

    def test_circuit_breaker_disables_after_errors(self):
        tap.MAX_ERRORS = 5
        m = _FakeMlp(lambda x: "not a tensor")
        tap._attach(m, "language_model.model.layers.0.mlp", WIDTH)
        tap.S.armed_until = time.monotonic() + 30
        for _ in range(10):
            m.act_fn(torch.zeros(1))
        self.assertTrue(tap.S.disabled)
        self.assertEqual(tap.S.errors, 5)  # stops counting once disabled
        self.assertFalse(tap.stats()["armed"])

    def test_bfloat16_output_still_uses_aminmax_and_keeps_the_sign(self):
        m = _FakeMlp(lambda x: -_row_of(2, 5.0).to(torch.bfloat16))
        tap._attach(m, "language_model.model.layers.0.mlp", WIDTH)
        self._start_worker()
        tap.S.armed_until = time.monotonic() + 30
        m.act_fn(torch.zeros(1))
        snap = self._wait_snapshot()
        self.assertTrue(tap.S.use_aminmax)
        self.assertEqual(tap.S.errors, 0)
        self.assertEqual(sign_bit(snap, 0, 2), 1)  # the strongest response was negative

    def test_install_is_quiet_when_vllm_class_missing(self):
        saved = sys.modules.get("vllm.model_executor.models.qwen2_moe", "absent")
        sys.modules["vllm.model_executor.models.qwen2_moe"] = None  # import raises
        try:
            tap.install()  # must not raise
        finally:
            tap._PENDING.clear()
            while tap._FINDER in sys.meta_path:
                sys.meta_path.remove(tap._FINDER)
            if saved == "absent":
                sys.modules.pop("vllm.model_executor.models.qwen2_moe", None)
            else:
                sys.modules["vllm.model_executor.models.qwen2_moe"] = saved


class _FakeLlamaMlp(nn.Module):
    """Same call shape as vLLM's LlamaMLP / Qwen2MLP: prefix last, width positional or keyword."""

    def __init__(self, hidden_size, intermediate_size, hidden_act, quant_config=None, prefix=""):
        super().__init__()
        self.act_fn = _Act(lambda x: _row_of(0, 1.0))


class _FakeSubclassMlp(_FakeLlamaMlp):
    pass


class InstallTests(unittest.TestCase):
    """install() on stand-in vLLM modules: generic across MLP classes, signature styles and subclasses."""

    def setUp(self):
        tap.S = tap._State()
        tap.S.server_started = True
        self._mods = {}
        tap._PENDING.clear()

    def tearDown(self):
        for name in self._mods:
            sys.modules.pop(name, None)
        tap._PENDING.clear()
        while tap._FINDER in sys.meta_path:
            sys.meta_path.remove(tap._FINDER)

    def test_class_is_patched_when_its_module_is_imported_later(self):
        import importlib
        import tempfile

        with tempfile.TemporaryDirectory() as d:
            pkg = os.path.join(d, "vislazy")
            os.makedirs(pkg)
            open(os.path.join(pkg, "__init__.py"), "w").close()
            with open(os.path.join(pkg, "mod.py"), "w") as f:
                f.write(
                    "from torch import nn\n"
                    "class LazyMLP(nn.Module):\n"
                    "    def __init__(self, hidden_size, intermediate_size, prefix=''):\n"
                    "        super().__init__()\n"
                    "        self.act_fn = nn.Identity()\n"
                )
            sys.path.insert(0, d)
            old = os.environ.get("VIS_MLP_CLASSES")
            os.environ["VIS_MLP_CLASSES"] = "vislazy.mod:LazyMLP"
            try:
                tap.install()
                self.assertNotIn("vislazy.mod", sys.modules)  # install() did not import it
                mod = importlib.import_module("vislazy.mod")
                self.assertTrue(mod.LazyMLP.__dict__.get("_vis_patched"))
                m = mod.LazyMLP(8, WIDTH, prefix="model.layers.0.mlp")
                self.assertEqual(len(m.act_fn._forward_hooks), 1)
            finally:
                sys.path.remove(d)
                sys.modules.pop("vislazy", None)
                sys.modules.pop("vislazy.mod", None)
                if old is None:
                    os.environ.pop("VIS_MLP_CLASSES", None)
                else:
                    os.environ["VIS_MLP_CLASSES"] = old

    def test_gemma4_and_other_architectures_are_in_the_default_list(self):
        for spec in ("gemma4:Gemma4MLP", "gemma3:Gemma3MLP", "qwen2:Qwen2MLP", "llama:LlamaMLP", "qwen2_moe:Qwen2MoeMLP"):
            self.assertIn(spec, tap.MLP_CLASSES)

    def _fake_module(self, short, **classes):
        import types

        name = f"vllm.model_executor.models.{short}"
        mod = types.ModuleType(name)
        for k, v in classes.items():
            setattr(mod, k, v)
        sys.modules[name] = mod
        self._mods[name] = mod
        return mod

    def _block_defaults(self):
        for spec in tap.MLP_CLASSES:
            short = spec.split(":")[0]
            name = f"vllm.model_executor.models.{short}"
            if name not in sys.modules:
                sys.modules[name] = None  # import raises: class "missing in this vLLM"
                self._mods[name] = None

    def test_patches_every_known_class_that_exists_and_skips_the_rest(self):
        class Llama(_FakeLlamaMlp):
            pass

        class Qwen2(_FakeLlamaMlp):
            pass

        self._fake_module("llama", LlamaMLP=Llama)
        self._fake_module("qwen2", Qwen2MLP=Qwen2)
        self._block_defaults()
        tap.install()
        self.assertTrue(Llama.__dict__.get("_vis_patched"))
        self.assertTrue(Qwen2.__dict__.get("_vis_patched"))

    def test_keyword_and_positional_construction_both_attach(self):
        class Llama(_FakeLlamaMlp):
            pass

        self._fake_module("llama", LlamaMLP=Llama)
        self._block_defaults()
        tap.install()
        kw = Llama(hidden_size=8, intermediate_size=WIDTH, hidden_act="silu", prefix="model.layers.3.mlp")
        pos = Llama(8, WIDTH, "silu", None, "model.layers.4.mlp")
        self.assertEqual(len(kw.act_fn._forward_hooks), 1)
        self.assertEqual(len(pos.act_fn._forward_hooks), 1)
        self.assertEqual(tap.S.max_idx, 4)

    def test_subclass_and_parent_patched_hook_only_once(self):
        class Parent(_FakeLlamaMlp):
            pass

        class Child(Parent):
            pass

        self._fake_module("llama", LlamaMLP=Parent)
        self._fake_module("mistral", MistralMLP=Child)
        self._block_defaults()
        tap.install()
        m = Child(8, WIDTH, "silu", None, "model.layers.0.mlp")
        self.assertEqual(len(m.act_fn._forward_hooks), 1)

    def test_extra_classes_from_the_environment(self):
        class Custom(_FakeLlamaMlp):
            pass

        self._fake_module("mymodel", CustomMLP=Custom)
        self._block_defaults()
        old = os.environ.get("VIS_MLP_CLASSES")
        os.environ["VIS_MLP_CLASSES"] = "mymodel:CustomMLP"
        try:
            tap.install()
        finally:
            if old is None:
                os.environ.pop("VIS_MLP_CLASSES", None)
            else:
                os.environ["VIS_MLP_CLASSES"] = old
        self.assertTrue(Custom.__dict__.get("_vis_patched"))

    def test_other_layer_naming_schemes(self):
        for i, prefix in enumerate(["transformer.h.0.mlp", "model.blocks.1.feed_forward", "model.layers.2.mlp"]):
            m = _FakeMlp(lambda x: x)
            tap._attach(m, prefix, None)
            self.assertEqual(len(m.act_fn._forward_hooks), 1, prefix)

    def test_one_shared_activation_module_still_gives_every_layer_its_own_hook(self):
        """vLLM's Gemma gets one activation module from a registry and gives it to all layers."""
        shared = _Act(lambda x: _row_of(0, 1.0))

        class Mlp(nn.Module):
            def __init__(self):
                super().__init__()
                self.act_fn = shared

        mlps = [Mlp() for _ in range(4)]
        for i, m in enumerate(mlps):
            tap._attach(m, f"language_model.model.layers.{i}.mlp", WIDTH)
        self.assertEqual(tap.S.max_idx, 3)
        tap.S.armed_until = time.monotonic() + 30
        for m in mlps:
            m.act_fn(torch.zeros(1))
        self.assertEqual(tap.S.armed_hook_calls, 4)  # one hook per layer, none lost, none doubled

    def test_module_without_an_activation_is_ignored(self):
        class NoAct:
            pass

        tap._attach(NoAct(), "model.layers.0.mlp", WIDTH)
        self.assertEqual(tap.S.max_idx, -1)


class HttpTests(unittest.TestCase):
    def setUp(self):
        tap.S = tap._State()
        tap.S.latest = {"seq": 1, "ts": 0, "layers": 1, "buckets": 1, "layer_max": [1], "max": 1, "data": "AA=="}
        tap.S.latest_json = json.dumps(tap.S.latest).encode()
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), tap._Handler)
        self.port = self.server.server_address[1]
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def tearDown(self):
        self.server.shutdown()
        tap.TOKEN = ""
        tap.MAX_STREAMS = 4

    def _get(self, path, headers=None):
        req = urllib.request.Request(f"http://127.0.0.1:{self.port}{path}", headers=headers or {})
        return urllib.request.urlopen(req, timeout=5)

    def test_health_and_snapshot_arm_the_tap(self):
        self.assertFalse(json.load(self._get("/health"))["armed"])
        self.assertEqual(json.load(self._get("/snapshot"))["seq"], 1)
        self.assertTrue(json.load(self._get("/health"))["armed"])

    def test_token_required_when_configured(self):
        tap.TOKEN = "s3cret"
        with self.assertRaises(urllib.error.HTTPError) as e:
            self._get("/health")
        self.assertEqual(e.exception.code, 401)
        self.assertEqual(self._get("/health?token=s3cret").status, 200)
        self.assertEqual(self._get("/health", {"Authorization": "Bearer s3cret"}).status, 200)

    def _post(self, path, headers=None):
        req = urllib.request.Request(f"http://127.0.0.1:{self.port}{path}", method="POST", data=b"", headers=headers or {})
        return urllib.request.urlopen(req, timeout=5)

    def test_live_master_switch_disables_and_reenables(self):
        tap.S.armed_until = time.monotonic() + 30
        self.assertTrue(json.load(self._get("/health"))["armed"])

        off = json.load(self._post("/disable"))
        self.assertTrue(off["disabled"])
        self.assertEqual(off["disabled_reason"], "manual")
        self.assertFalse(off["armed"])

        on = json.load(self._post("/enable"))
        self.assertFalse(on["disabled"])
        self.assertEqual(on["disabled_reason"], "")

    def _active(self):
        tap.S.last_step = time.monotonic()   # the model just ran a step: a request is in flight

    def test_disable_waits_for_the_request_to_finish(self):
        self._active()
        r = json.load(self._post("/disable"))
        self.assertFalse(r["disabled"])
        self.assertTrue(r["pending_disable"])

        tap._watchdog_tick()                  # still busy: nothing happens
        self.assertFalse(tap.S.disabled)

        tap.S.last_step = time.monotonic() - 10   # model went quiet: the answer is finished
        tap._watchdog_tick()
        self.assertTrue(tap.S.disabled)
        self.assertEqual(tap.S.disabled_reason, "manual")
        self.assertFalse(tap.S.pending_disable)

    def test_disable_is_immediate_when_the_model_is_already_idle(self):
        tap.S.last_step = time.monotonic() - 10
        self.assertTrue(json.load(self._post("/disable"))["disabled"])

    def test_disable_now_skips_the_wait(self):
        self._active()
        r = json.load(self._post("/disable?now=1"))
        self.assertTrue(r["disabled"])
        self.assertFalse(r["pending_disable"])

    def test_enable_cancels_a_pending_disable(self):
        self._active()
        self._post("/disable")
        r = json.load(self._post("/enable"))
        self.assertFalse(r["pending_disable"])
        self.assertFalse(r["disabled"])
        tap.S.last_step = time.monotonic() - 10
        tap._watchdog_tick()
        self.assertFalse(tap.S.disabled)

    def test_enable_waits_for_the_next_request_when_an_answer_is_streaming(self):
        tap.S.disabled, tap.S.disabled_reason = True, "manual"
        self._active()
        r = json.load(self._post("/enable"))
        self.assertTrue(r["disabled"])         # still off: do not switch on mid-answer
        self.assertTrue(r["pending_enable"])

    def test_enable_is_immediate_when_the_model_is_idle(self):
        tap.S.disabled, tap.S.disabled_reason = True, "manual"
        tap.S.last_step = time.monotonic() - 10
        r = json.load(self._post("/enable"))
        self.assertFalse(r["disabled"])
        self.assertFalse(r["pending_enable"])

    def test_enable_now_skips_the_wait(self):
        tap.S.disabled, tap.S.disabled_reason = True, "manual"
        self._active()
        self.assertFalse(json.load(self._post("/enable?now=1"))["disabled"])

    def test_pending_enable_switches_on_at_the_first_step_of_the_next_request(self):
        mlp0 = type("M", (), {})(); mlp0.act_fn = nn.Identity()
        mlp1 = type("M", (), {})(); mlp1.act_fn = nn.Identity()
        tap._attach(mlp0, "language_model.model.layers.0.mlp", WIDTH)
        tap._attach(mlp1, "language_model.model.layers.1.mlp", WIDTH)
        tap.S.disabled, tap.S.disabled_reason, tap.S.pending_enable = True, "manual", True
        tap.S.last_step = time.monotonic()          # answer still streaming
        mlp0.act_fn(torch.zeros(1))                 # next step of the SAME answer
        self.assertTrue(tap.S.disabled)             # stays off

        tap.S.last_step = time.monotonic() - 10     # gap: that answer ended
        mlp0.act_fn(torch.zeros(1))                 # first step of a NEW request
        self.assertFalse(tap.S.disabled)
        self.assertFalse(tap.S.pending_enable)

    def test_pending_enable_also_switches_on_at_a_new_prompt_even_without_a_pause(self):
        # Back-to-back requests never leave an idle gap, but a new prompt shows up as a big step.
        mlp0 = type("M", (), {})(); mlp0.act_fn = nn.Identity()
        tap._attach(mlp0, "language_model.model.layers.0.mlp", WIDTH)
        tap.S.disabled, tap.S.disabled_reason, tap.S.pending_enable = True, "manual", True
        tap.S.last_step = time.monotonic()
        mlp0.act_fn(torch.zeros(5, 4))                    # a normal decode step (a few tokens)
        self.assertTrue(tap.S.disabled)
        tap.S.last_step = time.monotonic()
        mlp0.act_fn(torch.zeros(tap.PREFILL_MIN, 4))      # a prompt arrives
        self.assertFalse(tap.S.disabled)
        self.assertFalse(tap.S.pending_enable)

    def test_only_the_first_layer_stamps_the_step_time(self):
        mlp0 = type("M", (), {})(); mlp0.act_fn = nn.Identity()
        mlp1 = type("M", (), {})(); mlp1.act_fn = nn.Identity()
        tap._attach(mlp0, "language_model.model.layers.0.mlp", WIDTH)
        tap._attach(mlp1, "language_model.model.layers.1.mlp", WIDTH)
        mlp1.act_fn(torch.zeros(1))
        self.assertEqual(tap.S.last_step, 0.0)
        mlp0.act_fn(torch.zeros(1))
        self.assertGreater(tap.S.last_step, 0.0)

    def test_disabled_hooks_do_no_work(self):
        m = type("M", (), {})()
        m.act_fn = nn.Identity()
        tap._attach(m, "language_model.model.layers.0.mlp", WIDTH)
        tap.S.armed_until = time.monotonic() + 30
        self._post("/disable")
        m.act_fn(torch.zeros(1))
        self.assertEqual(tap.S.armed_hook_calls, 0)

    def test_enable_also_clears_an_error_shutdown(self):
        tap.S.disabled, tap.S.disabled_reason, tap.S.errors = True, "errors", 99
        self.assertFalse(json.load(self._post("/enable"))["disabled"])
        self.assertEqual(tap.S.errors, 0)

    def test_switch_needs_the_token_when_one_is_configured(self):
        tap.TOKEN = "s3cret"
        with self.assertRaises(urllib.error.HTTPError) as e:
            self._post("/disable")
        self.assertEqual(e.exception.code, 401)
        self.assertFalse(tap.S.disabled)
        self.assertTrue(json.load(self._post("/disable", {"Authorization": "Bearer s3cret"}))["disabled"])

    def test_unknown_post_path_404(self):
        with self.assertRaises(urllib.error.HTTPError) as e:
            self._post("/nope")
        self.assertEqual(e.exception.code, 404)

    def test_unknown_path_404(self):
        with self.assertRaises(urllib.error.HTTPError) as e:
            self._get("/nope")
        self.assertEqual(e.exception.code, 404)

    def test_stream_limit_and_event_format(self):
        tap.MAX_STREAMS = 1
        first = self._get("/stream")
        line = first.readline().decode()
        self.assertTrue(line.startswith("data: "))
        self.assertEqual(json.loads(line[6:])["seq"], 1)
        with self.assertRaises(urllib.error.HTTPError) as e:
            self._get("/stream")
        self.assertEqual(e.exception.code, 429)
        first.close()


if __name__ == "__main__":
    unittest.main()
