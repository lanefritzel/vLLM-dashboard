"""Tap on each decoder layer's MLP activation (SiluAndMul output).

Design goals: never block the model, do almost nothing when nobody is watching,
and fail closed (turn itself off) if anything misbehaves.

  * A forward hook on every layer's `mlp.act_fn` is registered when the MLP is
    built. The hook returns immediately unless a viewer is "armed".
  * When armed, the hook reduces the LAST token's activation row to bucket
    maxima on the device and writes them into a small device buffer. After the
    last layer, one async device-to-host copy is queued into a free pinned slot
    and an event is recorded. The model thread never waits on that event.
  * A background thread waits on the event, normalises per layer on the CPU,
    returns the slot to the free list and publishes the latest snapshot.
  * Viewers arm the tap by calling /snapshot or holding /stream open; the tap
    disarms itself ARM_TTL seconds after the last viewer activity.
  * After MAX_ERRORS errors the tap disables itself for the life of the process.

Environment:
  VIS_ENABLED=1       master switch (plugin is a no-op otherwise)
  VIS_EVERY=2         capture every Nth model step
  VIS_BUCKET=128      neurons per bucket
  VIS_PORT=9998       HTTP port
  VIS_BIND=127.0.0.1  bind address (use 0.0.0.0 only on a trusted network)
  VIS_TOKEN=          if set, requests need ?token=... or Authorization: Bearer ...
  VIS_MAX_STREAMS=4   max simultaneous /stream connections
  VIS_MAX_ERRORS=20   errors before the tap disables itself
  VIS_IDLE_GAP=0.6    seconds without a model step that count as 'idle' (for a graceful /disable)
  VIS_PREFILL_MIN=32  a step with this many tokens is a new prompt arriving (graceful /enable boundary)
  VIS_MLP_CLASSES=    extra MLP classes to tap, "module:Class,..." (module relative to vllm.model_executor.models)

Live master switch (no restart): POST /enable, POST /disable and POST /disable?now=1. Allowed from
loopback, or with the token when VIS_TOKEN is set. While disabled every hook returns at once.
Both directions wait for a request boundary so the tap never changes state in the middle of an
answer. /disable DRAINS: it keeps working until the model has been idle for VIS_IDLE_GAP seconds
(no model step = no request in flight), then switches off. /enable, if an answer is streaming,
waits and switches on when the NEXT request starts: its prompt-processing step (a step with at least
VIS_PREFILL_MIN tokens) or the first step after an idle pause. Immediate if the model is idle.
Add ?now=1 to either to skip the wait.
"""
from __future__ import annotations

import base64
import hmac
import importlib.abc
import inspect
import json
import logging
import os
import re
import sys
import threading
import time
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

logger = logging.getLogger("vllm_visualizer")


def _int_env(name: str, default: int, lo: int = 1) -> int:
    try:
        return max(lo, int(os.environ.get(name, default)))
    except ValueError:
        return default


BUCKET = _int_env("VIS_BUCKET", 128)
EVERY = _int_env("VIS_EVERY", 2)
PORT = _int_env("VIS_PORT", 9998)
BIND = os.environ.get("VIS_BIND", "127.0.0.1")
TOKEN = os.environ.get("VIS_TOKEN", "")
MAX_STREAMS = _int_env("VIS_MAX_STREAMS", 4)
MAX_ERRORS = _int_env("VIS_MAX_ERRORS", 20)
try:
    ARM_TTL = float(os.environ.get("VIS_ARM_TTL", "5"))
except ValueError:
    ARM_TTL = 5.0
try:
    IDLE_GAP = max(0.05, float(os.environ.get("VIS_IDLE_GAP", "0.6")))
except ValueError:
    IDLE_GAP = 0.6
PREFILL_MIN = _int_env("VIS_PREFILL_MIN", 32)
SLOTS = 4

_LAYER_RE = re.compile(r"(?:^|\.)(?:layers|h|blocks)\.(\d+)\.(?:mlp|feed_forward)$")

# Dense MLP classes to tap ("module:Class", module relative to vllm.model_executor.models).
MLP_CLASSES = (
    "qwen2_moe:Qwen2MoeMLP",  # Qwen3.5 / Qwen3-Next (dense and the shared expert)
    "qwen2:Qwen2MLP",         # Qwen2, Qwen2.5, Qwen3
    "qwen3_moe:Qwen3MoeMLP",
    "llama:LlamaMLP",         # Llama and most Llama-style models
    "mistral:MistralMLP",
    "granite:GraniteMLP",
    "granitemoeshared:GraniteMoeSharedMLP",
    "gemma:GemmaMLP",
    "gemma2:Gemma2MLP",
    "gemma3:Gemma3MLP",
    "gemma3n:Gemma3nMLP",
    "gemma4:Gemma4MLP",
    "commandr:CohereMLP",
    "deepseek_v2:DeepseekV2MLP",
    "exaone:ExaoneGatedMLP",
    "exaone4:Exaone4GatedMLP",
    "falcon_h1:FalconH1MLP",
    "glm4_moe:Glm4MoeMLP",
    "internlm2:InternLM2MLP",
    "minicpm:MiniCPMMLP",
    "nemotron:NemotronMLP",
    "nemotron_h:NemotronHMLP",
    "orion:OrionMLP",
    "solar:SolarMLP",
    "stablelm:StablelmMLP",
    "apertus:ApertusMLP",
    "hyperclovax:HyperCLOVAXMLP",
    "lfm2:Lfm2MLP",
    "zamba2:Zamba2MLP",
    "olmo_hybrid:OlmoHybridMLP",
    "ernie45_moe:Ernie4_5_MoeMLP",
    "bailing_moe:BailingMLP",
    "openpangu:OpenPanguMLP",
    "step1:StepMLP",
    "step3_text:Step3TextMLP",
    "step3p5:Step3p5MLP",
    "mimo_v2:MiMoV2MLP",
    "rnj1:Rnj1MLP",
)


class _NoopEvent:
    def record(self) -> None:
        pass

    def synchronize(self) -> None:
        pass


class _State:
    def __init__(self) -> None:
        self.armed_until = 0.0
        self.disabled = False
        self.disabled_reason = ""   # "manual" (live switch) or "errors" (circuit breaker)
        self.pending_disable = False  # a graceful /disable is waiting for the model to go idle
        self.pending_enable = False   # a graceful /enable is waiting for the next request to start
        self.last_step = 0.0          # monotonic time of the latest model step (0 = none yet)
        self.step = 0
        self.capturing = False
        self.min_idx = 10**9
        self.max_idx = -1
        self.gpu_buf = None
        self.acts: dict = {}  # id(activation module) -> (owner mlp, hook handle, idx, first, module)
        self.free: deque = deque()  # free pinned slots
        self.slots: list = []
        self.pending: deque = deque()
        self.cond = threading.Condition()
        self.latest: dict | None = None
        self.latest_json: bytes | None = None
        self.use_aminmax = True
        self.seq = 0
        self.server_started = False
        self.streams = 0
        self.hook_calls = 0
        self.armed_hook_calls = 0
        self.hook_ns = 0
        self.snapshots = 0
        self.dropped = 0
        self.skipped = 0
        self.errors = 0

    def note_error(self, what: str) -> None:
        self.errors += 1
        if self.errors <= 3:
            logger.exception("vllm_visualizer: %s", what)
        if self.errors >= MAX_ERRORS and not self.disabled:
            self.disabled = True
            self.disabled_reason = "errors"
            logger.error("vllm_visualizer: %d errors, tap disabled for this process", self.errors)


S = _State()


def _idle_for() -> float:
    """Seconds since the model last ran a step (infinity if it never has)."""
    return (time.monotonic() - S.last_step) if S.last_step else float("inf")


def _disable_now(reason: str) -> None:
    S.disabled, S.disabled_reason, S.pending_disable, S.pending_enable = True, reason, False, False
    S.armed_until = 0.0


def _enable_now() -> None:
    S.disabled, S.disabled_reason, S.errors = False, "", 0
    S.pending_disable = S.pending_enable = False


def _watchdog_tick() -> None:
    if S.pending_disable and not S.disabled and _idle_for() >= IDLE_GAP:
        _disable_now("manual")


def _targets() -> list[tuple[str, str]]:
    extra = [t.strip() for t in os.environ.get("VIS_MLP_CLASSES", "").split(",") if t.strip()]
    out = []
    for spec in (*MLP_CLASSES, *extra):
        mod, _, name = spec.partition(":")
        if mod and name:
            out.append((mod if "." in mod else f"vllm.model_executor.models.{mod}", name))
    return out


def _patch_class(cls) -> None:
    if cls.__dict__.get("_vis_patched"):
        return
    orig_init = cls.__init__
    try:
        sig = inspect.signature(orig_init)
    except (TypeError, ValueError):
        sig = None

    def patched_init(self, *args, **kwargs):
        orig_init(self, *args, **kwargs)
        try:
            prefix, width = "", None
            if sig is not None:  # works whether vLLM passes these by keyword or by position
                bound = sig.bind_partial(self, *args, **kwargs).arguments
                prefix = bound.get("prefix", "") or ""
                width = bound.get("intermediate_size")
            _attach(self, prefix, width)
        except Exception:
            S.note_error("attach failed")

    cls.__init__ = patched_init
    cls._vis_patched = True


_PENDING: dict[str, list[str]] = {}   # module name -> class names still waiting for that module to load


def _patch_module(module) -> list[str]:
    done = []
    for name in _PENDING.pop(getattr(module, "__name__", ""), []):
        try:
            _patch_class(getattr(module, name))
            done.append(name)
        except Exception as e:  # class missing in this vLLM version: skip it quietly
            logger.debug("vllm_visualizer: skipping %s.%s (%s)", module.__name__, name, e)
    return done


class _PatchOnImport(importlib.abc.MetaPathFinder):
    """Patches a listed class the moment its module is imported, so the plugin never imports vLLM's
    model files itself (it only taps the architecture that is actually being loaded)."""

    def find_spec(self, name, path, target=None):
        if name not in _PENDING:
            return None
        for finder in sys.meta_path:
            if finder is self or not hasattr(finder, "find_spec"):
                continue
            try:
                spec = finder.find_spec(name, path, target)
            except Exception:
                continue
            if spec is None or spec.loader is None or not hasattr(spec.loader, "exec_module"):
                continue
            loader, orig_exec = spec.loader, spec.loader.exec_module

            def exec_module(module, _orig=orig_exec):
                _orig(module)
                try:
                    if _patch_module(module):
                        logger.warning("vllm_visualizer: tapping MLP classes in %s", module.__name__)
                except Exception:
                    S.note_error("patch on import failed")

            loader.exec_module = exec_module
            return spec
        return None


_FINDER = _PatchOnImport()


def install() -> None:
    """Tap every known dense-MLP class, patching each one as its module loads.

    Qwen2MLP is also what Qwen3 uses; LlamaMLP covers Llama and most Llama-style models; Gemma 4 has its
    own. Classes missing from this vLLM version are skipped; add more with
    VIS_MLP_CLASSES="module:Class,...". Mixture-of-experts layers (fused experts) are not tapped."""
    for module, name in _targets():
        _PENDING.setdefault(module, []).append(name)
    patched = []
    for module in list(_PENDING):
        loaded = sys.modules.get(module)
        if loaded is not None:  # already imported: patch now
            patched += _patch_module(loaded)
    if _PENDING and _FINDER not in sys.meta_path:
        sys.meta_path.insert(0, _FINDER)  # the rest are patched when (and if) they get imported
    if patched:
        logger.warning("vllm_visualizer: patched %s (VIS_ENABLED=1)", ", ".join(patched))
    else:
        logger.warning("vllm_visualizer: ready, tapping MLP classes as vLLM imports them (VIS_ENABLED=1)")


def _own_activation(mlp, shared):
    """Replace this layer's activation with a pass-through wrapper that only this layer owns."""
    from torch import nn

    class _OwnActivation(nn.Module):
        def __init__(self, inner):
            super().__init__()
            self.inner = inner

        def forward(self, *args, **kwargs):
            return self.inner(*args, **kwargs)

    wrapper = _OwnActivation(shared)
    mlp.act_fn = wrapper
    return wrapper


def _attach(mlp, prefix: str, width=None) -> None:
    m = _LAYER_RE.search(prefix or "")
    act = getattr(mlp, "act_fn", None)
    if not m or "mtp" in prefix or "visual" in prefix or act is None:
        return
    if getattr(mlp, "_vis_attached", False):  # a subclass and its parent can both be patched
        return
    mlp._vis_attached = True
    idx = int(m.group(1))
    first = idx < S.min_idx   # the first layer of a step also stamps the step time
    S.min_idx = min(S.min_idx, idx)
    S.max_idx = max(S.max_idx, idx)
    hook = _make_hook(idx, first)
    seen = S.acts.get(id(act))
    if seen is None or seen[4] is not act:
        # normal case: the layer owns its activation module, hook it directly (no extra call per layer)
        S.acts[id(act)] = (mlp, act.register_forward_hook(hook), idx, first, act)
    else:
        # vLLM can hand every layer the SAME activation module (Gemma's comes from a registry), and a hook
        # on a shared module cannot tell the layers apart. Give each layer its own thin wrapper instead,
        # including the layer that was hooked first.
        owner, handle, oidx, ofirst, _ = seen
        if handle is not None:
            handle.remove()
            _own_activation(owner, act).register_forward_hook(_make_hook(oidx, ofirst))
            S.acts[id(act)] = (owner, None, oidx, ofirst, act)
        _own_activation(mlp, act).register_forward_hook(hook)
    if not S.server_started:
        S.server_started = True
        _start_background()


def _make_hook(idx: int, first: bool = False):
    monotonic = time.monotonic
    perf = time.perf_counter_ns

    def hook(module, inputs, output):
        S.hook_calls += 1
        if S.disabled or monotonic() > S.armed_until:
            return
        t0 = perf()
        try:
            if idx == S.min_idx:
                S.step += 1
                S.capturing = (S.step % EVERY) == 0
            if S.capturing:
                _capture(idx, output)
        except Exception:
            S.note_error("hook failed")
        S.armed_hook_calls += 1
        S.hook_ns += perf() - t0

    if not first:
        return hook

    def hook_first(module, inputs, output):
        now = monotonic()
        if S.pending_enable:
            try:
                rows = int(output.shape[0])
            except Exception:
                rows = 0
            # a new request is starting: a big prompt-processing step, or the first step after a pause
            if rows >= PREFILL_MIN or not S.last_step or now - S.last_step >= IDLE_GAP:
                _enable_now()
        S.last_step = now   # one stamp per model step; lets a graceful /disable see when it is idle
        hook(module, inputs, output)

    return hook_first


def _make_event(dev):
    import torch

    mod = getattr(torch, dev.type, None)
    ev_cls = getattr(mod, "Event", None)
    if ev_cls is None:
        return _NoopEvent()
    try:
        return ev_cls()
    except Exception:
        return _NoopEvent()


def _capture(idx: int, output) -> None:
    import torch

    # Only handle the expected [tokens, width] shape; skip anything else
    # (for example tensor-parallel shards that do not divide into buckets).
    if output.dim() != 2 or output.shape[-1] % BUCKET:
        S.skipped += 1
        return
    nbuckets = output.shape[-1] // BUCKET
    nlayers = S.max_idx + 1
    shape = (nlayers, 2, nbuckets)  # [layer, (min, max), bucket]
    if S.gpu_buf is None or tuple(S.gpu_buf.shape) != shape:
        S.gpu_buf = torch.zeros(shape, dtype=torch.float16, device=output.device)
        S.slots = []
        for _ in range(SLOTS):
            try:
                S.slots.append(torch.empty(shape, dtype=torch.float16, pin_memory=True))
            except Exception:
                S.slots.append(torch.empty(shape, dtype=torch.float16))
        with S.cond:
            S.free = deque(range(SLOTS))
            S.pending.clear()
    row = output[-1].view(nbuckets, BUCKET)
    buf = S.gpu_buf[idx]
    if S.use_aminmax:
        try:
            if row.dtype == buf.dtype:
                # one kernel: per-bucket min and max, written straight into the buffer
                torch.aminmax(row, dim=-1, out=(buf[0], buf[1]))
            else:
                # bfloat16 models (Gemma, many Qwen builds): out= needs matching dtypes, so reduce first,
                # then copy the two tiny results across (still a few KB per layer)
                lo, hi = torch.aminmax(row, dim=-1)
                buf[0].copy_(lo)
                buf[1].copy_(hi)
        except Exception:
            S.use_aminmax = False
            logger.warning("vllm_visualizer: aminmax unavailable, using abs+amax fallback")
    if not S.use_aminmax:
        buf[0].zero_()
        buf[1].copy_(row.abs().amax(dim=-1))
    if idx == S.max_idx:
        with S.cond:
            slot_id = S.free.popleft() if S.free else None
        if slot_id is None:  # worker is behind: drop this snapshot, never wait
            S.dropped += 1
            return
        S.slots[slot_id].copy_(S.gpu_buf, non_blocking=True)
        ev = _make_event(output.device)
        ev.record()
        with S.cond:
            S.pending.append((time.time(), ev, slot_id))
            S.cond.notify()


def _start_background() -> None:
    threading.Thread(target=_worker, name="vis-worker", daemon=True).start()
    threading.Thread(target=_serve, name="vis-http", daemon=True).start()
    threading.Thread(target=_watchdog, name="vis-watchdog", daemon=True).start()


def _worker() -> None:
    # numpy on purpose: torch CPU ops would compete with the engine's own threads.
    import numpy as np

    st = S
    cond = st.cond
    while True:
        with cond:
            while not st.pending:
                cond.wait(timeout=1.0)
            ts, ev, slot_id = st.pending.popleft()
        try:
            ev.synchronize()
            raw = st.slots[slot_id].numpy().astype(np.float32)  # [layers, 2, buckets]
            absmax = np.maximum(raw[:, 1, :], -raw[:, 0, :])
            layer_max = absmax.max(axis=1)
            denom = np.maximum(layer_max, 1e-6)[:, None]
            q = np.clip(absmax / denom * 255.0, 0, 255).astype(np.uint8)
            # direction: 1 where the strongest response was negative (bit-packed, ~1.1 KB)
            neg = (-raw[:, 0, :]) > raw[:, 1, :]
            snap = {
                "seq": st.seq + 1,
                "ts": ts,
                "layers": int(q.shape[0]),
                "buckets": int(q.shape[1]),
                "layer_max": [round(float(x), 3) for x in layer_max],
                "max": round(float(layer_max.max()), 3),
                "data": base64.b64encode(q.tobytes()).decode("ascii"),
                "sign": base64.b64encode(np.packbits(neg.reshape(-1)).tobytes()).decode("ascii"),
            }
            encoded = json.dumps(snap).encode()  # encode once, every viewer reuses it
            st.seq += 1
            st.snapshots += 1
            with cond:
                st.latest = snap
                st.latest_json = encoded
                cond.notify_all()
        except Exception:
            st.note_error("worker failed")
        finally:
            with cond:
                st.free.append(slot_id)


def _watchdog() -> None:
    while True:
        time.sleep(0.1)
        try:
            _watchdog_tick()
        except Exception:
            S.note_error("watchdog failed")


def stats() -> dict:
    return {
        "armed": (not S.disabled) and time.monotonic() <= S.armed_until,
        "disabled": S.disabled,
        "disabled_reason": S.disabled_reason,
        "pending_disable": S.pending_disable,
        "pending_enable": S.pending_enable,
        "idle_seconds": None if not S.last_step else round(time.monotonic() - S.last_step, 2),
        "aminmax": S.use_aminmax,
        "layers": S.max_idx + 1,
        "step": S.step,
        "every": EVERY,
        "bucket": BUCKET,
        "streams": S.streams,
        "hook_calls": S.hook_calls,
        "armed_hook_calls": S.armed_hook_calls,
        "hook_ms_total": round(S.hook_ns / 1e6, 2),
        "snapshots": S.snapshots,
        "dropped": S.dropped,
        "skipped": S.skipped,
        "errors": S.errors,
    }


def _authorized(handler) -> bool:
    if not TOKEN:
        return True
    q = parse_qs(urlparse(handler.path).query).get("token", [""])[0]
    auth = handler.headers.get("Authorization", "")
    given = q or (auth[7:] if auth.startswith("Bearer ") else "")
    return hmac.compare_digest(given.encode(), TOKEN.encode())


class _Handler(BaseHTTPRequestHandler):
    timeout = 30

    def log_message(self, *args):  # silence
        pass

    def _json(self, obj, code=200):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if not _authorized(self):
            return self._json({"error": "unauthorized"}, 401)
        path = urlparse(self.path).path
        if path == "/health":
            return self._json(stats())
        if path == "/snapshot":
            S.armed_until = time.monotonic() + ARM_TTL
            return self._json(S.latest or {})
        if path == "/stream":
            return self._stream()
        self._json({"error": "not found"}, 404)

    def do_POST(self):
        path = urlparse(self.path).path
        if path not in ("/enable", "/disable"):
            return self._json({"error": "not found"}, 404)
        loopback = self.client_address[0] in ("127.0.0.1", "::1")
        if TOKEN:
            if not _authorized(self):
                return self._json({"error": "unauthorized"}, 401)
        elif not loopback:
            return self._json({"error": "set VIS_TOKEN to control this remotely"}, 403)
        now = parse_qs(urlparse(self.path).query).get("now", ["0"])[0] in ("1", "true")
        idle = _idle_for() >= IDLE_GAP
        if path == "/disable":
            if now or idle:
                _disable_now("manual")
            else:
                S.pending_disable, S.pending_enable = True, False   # watchdog switches off once idle
        else:
            if not S.disabled:
                S.pending_disable = S.pending_enable = False        # already on: just cancels a pending off
            elif now or idle:
                _enable_now()
            else:
                S.pending_enable, S.pending_disable = True, False   # next request's first step switches on
        return self._json(stats())

    def _stream(self):
        if S.streams >= MAX_STREAMS:
            return self._json({"error": "too many streams"}, 429)
        S.streams += 1
        try:
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Cache-Control", "no-cache")
            self.end_headers()
            last = 0
            while True:
                S.armed_until = time.monotonic() + ARM_TTL
                with S.cond:
                    S.cond.wait(timeout=1.0)
                    snap = S.latest
                    encoded = S.latest_json
                if snap and encoded and snap["seq"] != last:
                    last = snap["seq"]
                    self.wfile.write(b"data: " + encoded + b"\n\n")
                else:
                    self.wfile.write(b": keepalive\n\n")
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError, TimeoutError, OSError):
            return
        finally:
            S.streams -= 1


def _serve() -> None:
    try:
        ThreadingHTTPServer((BIND, PORT), _Handler).serve_forever()
    except Exception:
        logger.exception("vllm_visualizer: http server failed on %s:%s", BIND, PORT)
