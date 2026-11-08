# vllm-visualizer

A vLLM plugin that feeds the dashboard's **Neuron map** (Overview). It taps each
decoder layer's MLP activation and serves a small per-step summary over HTTP.
It is installed into the supervisor image by `supervisor/Dockerfile` and found by
vLLM through the `vllm.general_plugins` entry point, so there is nothing to wire up.

## How it stays out of the way

- **Dormant until watched.** The hook on each layer returns immediately unless a viewer
  is connected (or recently was, `VIS_ARM_TTL` seconds).
- **One kernel per layer.** When recording, each layer does a single min/max reduction
  on the last token's activation, then one async copy per step into a pinned buffer.
  The model never waits: if the background thread falls behind, snapshots are dropped.
- **Never changes output.** It only reads. After `VIS_MAX_ERRORS` errors it switches
  itself off for the life of the process.
- **Measured** (Qwen3.8 27B INT4, Intel Arc, eager mode, ~40 tok/s): no throughput
  change idle, disabled, or watched at the default sampling; about 1.4% slower sampling
  every step.

Verified on the Qwen3.5 architecture (it hooks vLLM's shared `Qwen2MoeMLP` class,
named `layers.N.mlp`). Other models are untouched; the plugin quietly does nothing
for a model that never builds that class.

## Endpoints

| | |
|---|---|
| `GET /health` | State: `armed`, `disabled`, `pending_disable`, `pending_enable`, counters. |
| `GET /snapshot` | Latest snapshot (also keeps the tap awake). |
| `GET /stream` | Server-sent events, one snapshot per recorded step. |
| `POST /disable` | Switch off **after the current answer** (when the model has been idle `VIS_IDLE_GAP` s). `?now=1` switches off immediately. |
| `POST /enable` | Switch on at the **next request** (a prompt of `VIS_PREFILL_MIN`+ tokens, or the first step after an idle pause); immediate if idle. `?now=1` skips the wait. |

`POST` is allowed from loopback, or with the token when `VIS_TOKEN` is set. If a token
is set, every request needs it.

A snapshot is JSON: `layers`, `buckets` (neurons are grouped into buckets of
`VIS_BUCKET`), `layer_max[]` (raw per-layer maximum), `data` (base64, one byte per
cell, scaled per layer), and `sign` (bit-packed: 1 = the strongest response was negative).

## Settings

| Variable | Default | |
|---|---|---|
| `VIS_ENABLED` | `1` in the image | `1` loads the tap. Read once at vLLM start. |
| `VIS_BIND` / `VIS_PORT` | `127.0.0.1` / `9998` | Where it listens. |
| `VIS_TOKEN` | unset | Bearer token for every request. |
| `VIS_EVERY` | `2` | Record every Nth model step. |
| `VIS_BUCKET` | `128` | Neurons per bucket (must divide the MLP width). |
| `VIS_ARM_TTL` | `5` | Seconds the tap stays awake after the last viewer activity. |
| `VIS_MAX_STREAMS` | `4` | Simultaneous `/stream` connections. |
| `VIS_MAX_ERRORS` | `20` | Errors before it switches itself off. |
| `VIS_IDLE_GAP` | `0.6` | Seconds without a model step that mean "idle". |
| `VIS_PREFILL_MIN` | `32` | Step size that means "a new prompt arrived". |

## Tests and development

All GPU-free: fake layers, a fake engine loop, and 12 snapshots recorded from a real run.

```bash
IMAGE=<any local image with python, torch and vllm> ./run-tests.sh
```

`python -m vllm_visualizer.replay tests/fixtures/real_snapshots.jsonl --port 9998`
serves the recorded data with the same endpoints, so the dashboard page can be
developed with no GPU.
