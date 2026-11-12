# vLLM Dashboard

[![Docker Hub](https://img.shields.io/docker/v/lanedfritz/vllm-dashboard?sort=semver)](https://hub.docker.com/r/lanedfritz/vllm-dashboard)

A self-hosted dashboard for monitoring and controlling one or more
[vLLM](https://github.com/vllm-project/vllm) or
[llama.cpp](https://github.com/ggml-org/llama.cpp) inference servers on your
own network — live metrics, historical usage, a curated model/recipe
catalog, one-click model swapping with a live progress view, and a
transparent OpenAI-compatible proxy in front of it all.

Think of it as **llama-swap, but with a UI**: a stable `/v1` endpoint that your
tools (OpenWebUI, LibreChat, opencode, any OpenAI SDK client) talk to
directly, while the dashboard handles swapping the actual model process to
whatever was requested.

> **GPU support:** live-tested end-to-end against an **Intel GPU** host
> (`intel/llm-scaler-vllm`, raw `/dev/dri` passthrough). VRAM reporting also
> has an NVIDIA path (`nvidia-smi`), but it hasn't been exercised against real
> NVIDIA hardware yet — treat it as untested, not verified. Everything else
> (process lifecycle, health checks, the proxy) is vendor-agnostic.

## Architecture

This image is **one piece of a small stack**, not a single-container app:

```
 ┌──────────────┐        ┌──────────────┐        ┌─────────────────────┐
 │  PostgreSQL  │◄──────►│  Dashboard   │◄──────►│  Supervisor (per     │
 │ (this repo's │        │ (this image) │  HTTP  │  GPU host)           │
 │  own data)   │        │              │        │  manages the vLLM    │
 └──────────────┘        └──────────────┘        │  process directly    │
                                 ▲                └─────────────────────┘
                                 │ OpenAI-compatible clients
                          /proxy/<slug>/v1
```

- **Dashboard** (this image) — the web UI, Postgres-backed state (servers,
  models, recipes, metrics history), and the proxy. This is the only piece
  published to Docker Hub.
- **Supervisor** — a small Python sidecar that runs *alongside* the inference
  engine on each GPU machine and actually starts/stops/health-checks its
  process on the dashboard's behalf. Each instance picks its engine (vLLM or
  llama.cpp) per recipe. It's built, not pulled — the Compose stack below
  builds it straight from this repo's `supervisor/` directory on your own
  machine, layered onto whatever base image matches your hardware and engine
  (e.g. `intel/llm-scaler-vllm` or `vllm/vllm-openai` for vLLM via
  `supervisor/Dockerfile`; the official `ghcr.io/ggml-org/llama.cpp` server
  image for llama.cpp via `supervisor/Dockerfile.llamacpp`). `docker compose
  up -d --build` handles the clone and build for you; no separate image, no
  manual git clone. Its admin API binds to loopback only, so the dashboard
  reaches it over `127.0.0.1` on a shared network namespace (`network_mode:
  host` is the simplest way to get there) rather than any token or
  network-exposed auth.
- **Visualizer plugin** (inside the vLLM supervisor image only) — a vLLM
  plugin that vLLM loads by itself. It serves a small activity summary on
  loopback port `9998`; the dashboard connects to it only while someone has
  the Neuron map open. Nothing to deploy separately. **vLLM only** — llama.cpp
  has no equivalent runtime plugin mechanism (see
  [Engine differences](#engine-differences) below), so instances on that
  engine don't show a Neuron map.
- **Postgres** — metrics history, server/model/recipe config, accounts.

If you just want live metrics/history for a vLLM, llama.cpp, or Ollama server
that's already running somewhere, you don't need a supervisor at all — add it
as a monitoring-only source and skip straight to the proxy. Model swapping,
idle-unload, and restore-on-boot all require that source's supervisor.

### Engine differences

Both engines share the same recipe catalog, swap ceremony, proxy, and
history, but a few things only work on one of them:

| Capability | vLLM | llama.cpp |
|---|---|---|
| Live throughput / KV cache % | Prometheus `/metrics` scrape | read from each request's own streamed `timings` object (no scrape race, see below) |
| Neuron map (live per-layer activation view) | yes | no — llama.cpp has no runtime plugin hook equivalent to vLLM's; it would require a patched/forked `llama-server` build (researched, not pursued — see the project's internal notes) |
| GPU offload control | `--gpu-memory-utilization` and friends | `--gpu-layers` (**All** / **CPU only** / a specific layer count via Extra args) |

llama.cpp's live metrics deliberately do **not** come from its `/metrics`
endpoint's token counters: `llama-server` is effectively single-threaded
while streaming a completion, so a scrape made mid-generation can queue
behind it and read back an inflated tok/s on the next successful scrape.
Instead, the proxy asks for `timings_per_token` on every llama.cpp request and
reads throughput/KV% directly out of each streamed chunk's own numbers — the
engine's own live truth, not a sampled approximation.

## Features

- **Live monitoring** — requests running/waiting, KV cache usage, throughput,
  latency (TTFT/ITL/E2E); scraped from vLLM's Prometheus `/metrics`, or read
  live from llama.cpp's own per-request streaming timings (see
  [Engine differences](#engine-differences)) — with 30-day history and hourly
  rollups either way.
- **Model-driven swap** — a curated, typed catalog of engine launch options
  per recipe (vLLM: context length, GPU memory utilization, dtype, and the
  rest of vLLM's flags; llama.cpp: context size, GPU layer offload, and the
  rest of `llama-server`'s flags) instead of free-text CLI strings, with a
  live step-by-step progress view and log tail while a swap is in flight.
- **Transparent OpenAI-compatible proxy** — point any client at
  `/proxy/<slug>/v1`; `/v1/models` answers with the full registered catalog
  for that source, not just whatever's currently loaded.
- **Restore last known good model on boot** — a per-instance toggle; if the
  supervisor's container restarts, it replays the last model that reached a
  healthy state (never a run that crashed or failed).
- **Live traffic view** — watch requests, reasoning, and streamed responses
  go through the proxy in real time.
- **Neuron map** — a section on Overview that shows, live, which parts of the
  model respond most to each word, fed by a small vLLM plugin that ships inside
  the supervisor image. It is hidden by default and the plugin is dormant until
  you open it. You can switch the plugin on or off without a restart (both
  directions wait for a request boundary, so an answer is never cut mid-stream),
  pick from 16 color palettes, and choose a gap between cells. On an Intel Arc
  GPU it made no measurable difference to generation speed — see
  [`supervisor/vllm-visualizer`](supervisor/vllm-visualizer/README.md).
- **Live container logs** — stream a backend container's stdout/stderr in the
  UI.
- **Multi-instance** — any number of servers and vLLM instances, each with
  its own metrics, model registry, and recipes.

## Quick start

```bash
docker run -d \
  --name vllm-dashboard \
  -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="Host=<your-postgres-host>;Port=5432;Database=dashboard;Username=dashboard;Password=dashboard" \
  -e DATAPROTECTION_KEYS_DIR=/data/dataprotection \
  -v vllm-dashboard-data:/data \
  lanedfritz/vllm-dashboard:latest
```

You'll need a PostgreSQL database reachable from the container — see the
Compose example below for a complete self-contained setup, including
Postgres. `ASPNETCORE_ENVIRONMENT=Production` is already baked into the
image's default env, so you don't need to set it yourself.

Open `http://<host>:8080`, register the first account (it becomes the admin
automatically), then from **Servers**, register the machine running vLLM and
its supervisor (see below) — or just add a monitoring-only source if you
don't need swapping.

## Docker Compose

This is the complete stack — Postgres, the dashboard (pulled from Docker
Hub), and a supervisor (built on your own machine from this repo, layered
onto whatever vLLM image matches your GPU). `docker compose up -d --build`
is all it takes; Compose clones `supervisor/` from GitHub and builds it for
you, no manual steps:

```yaml
services:
  postgres:
    image: postgres:16
    restart: unless-stopped
    environment:
      POSTGRES_DB: dashboard
      POSTGRES_USER: dashboard
      POSTGRES_PASSWORD: dashboard
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U dashboard -d dashboard"]
      interval: 5s
      timeout: 5s
      retries: 10
    # The dashboard runs with network_mode: host (below), so it reaches Postgres
    # through the host, not the Compose network -- `expose` alone is not enough.
    # Bound to loopback so Postgres isn't opened to your LAN.
    ports:
      - "127.0.0.1:5432:5432"

  dashboard:
    image: lanedfritz/vllm-dashboard:latest
    # Re-pull on every `up` so a stale cached `latest` can't give you an old UI.
    pull_policy: always
    restart: unless-stopped
    depends_on:
      postgres:
        condition: service_healthy
    # network_mode: host -- the supervisor's admin API binds to loopback
    # only (see below), so the dashboard needs to share its network
    # namespace to reach it. Drop this (and use `ports:` below instead) if
    # you're only running monitoring-only sources with no supervisor.
    network_mode: host
    environment:
      ConnectionStrings__DefaultConnection: "Host=127.0.0.1;Port=5432;Database=dashboard;Username=dashboard;Password=dashboard"
      Kestrel__Endpoints__Http__Url: "http://+:8080"
      DATAPROTECTION_KEYS_DIR: /data/dataprotection
      # Must match the supervisor's own MODELS_ROOT mount (same host path on
      # both sides) for the Servers/Add-model folder picker to browse real
      # paths. Omit this var and the matching volume line below to fall back
      # to a plain text field.
      MODELS_BROWSE_ROOT: /models
      # Neuron map: only needed if the plugin is set up with a token or is
      # reached over the LAN rather than loopback (see "Neuron map plugin" below).
      # Visualizer__Token: "<same value as the supervisor's VIS_TOKEN>"
    # ports:
    #   - "8080:8080"   # only needed if NOT using network_mode: host above
    volumes:
      - dataprotection:/data
      - /path/to/your/hf-models:/models:ro

  supervisor:
    build:
      # Builds straight from this public repo's supervisor/ subdirectory --
      # no local clone needed. Swap #main for a tagged release if you want a
      # pinned version instead of tracking the branch.
      context: https://github.com/lanefritzel/vLLM-dashboard.git#main:supervisor
      # dockerfile: Dockerfile.llamacpp   # llama.cpp instead of vLLM -- see "llama.cpp hosts" below
      args:
        # Must match your actual GPU host's vLLM image/vendor.
        BASE_IMAGE: intel/llm-scaler-vllm:0.26.0-b2
    restart: unless-stopped
    network_mode: host
    devices:
      - /dev/dri          # Intel/AMD GPU passthrough. NVIDIA host: remove this
                           # and use the GPU reservation shown below instead.
    volumes:
      # Read-write: the dashboard's "Download model" runs here and writes into it.
      # Same host path as the dashboard's (read-only) mount above.
      - /path/to/your/hf-models:/models
      - supervisor-state:/state
    ipc: host
    privileged: true
    environment:
      ADMIN_PORT: "18001"
      DASHBOARD_EVENT_URL: "http://127.0.0.1:8080/internal/supervisor-events"
      # Neuron map plugin: loaded by vLLM, on by default, idle until the Overview
      # section is open. No change needed for this host-network stack.
      # VIS_ENABLED: "0"              # uncomment to turn it off for this server
      # If the dashboard reaches this host over the LAN instead of loopback:
      # VIS_BIND: "0.0.0.0"
      # VIS_TOKEN: "<shared-secret>"  # and set Visualizer__Token on the dashboard

volumes:
  pgdata:
  dataprotection:
  supervisor-state:
```

### NVIDIA hosts

Three changes to the `supervisor` service (needs the NVIDIA Container Toolkit
on the host) — this is a diff against the full `supervisor` service in the
main example above, not a standalone block: keep everything else from that
block (`restart`, `network_mode`, `ipc`, `privileged`, `volumes`,
`environment`) and only change what's shown here:

```yaml
  supervisor:
    build:
      context: https://github.com/lanefritzel/vLLM-dashboard.git#main:supervisor
      args:
        BASE_IMAGE: vllm/vllm-openai:v0.33.0     # instead of intel/llm-scaler-vllm
    # remove the `devices: - /dev/dri` block and add:
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: all
              capabilities: [gpu]
```

Everything else (dashboard, Postgres, the rest of the supervisor service) is
the same. The supervisor detects the GPU vendor at start-up, and the neuron
map plugin works on both.

Change `POSTGRES_PASSWORD` (and the matching password in the connection
string) before running this anywhere other than a trusted home network.

Once it's up, register the supervisor on the dashboard's **Servers** page
with `http://127.0.0.1:18001` (or whatever `ADMIN_PORT` you set) as the
Supervisor URL, and use **Test connection** to confirm it's reachable. If
you're only adding monitoring-only sources, you can drop the `supervisor`
service entirely and the `network_mode: host` lines above — a plain
`ports: ["8080:8080"]` mapping on the dashboard is enough.

### llama.cpp hosts

A server can run llama.cpp instead of vLLM — the dashboard talks to both the
same way, through its own supervisor. **llama.cpp's supervisor needs less than
vLLM's**: no `ipc: host` or `privileged: true` (those are a vLLM/CUDA IPC
requirement, not something llama.cpp needs) — live-verified end to end
(swap, recipe, live metrics) on an NVIDIA RTX 3060. This is the complete
`supervisor` service for llama.cpp on NVIDIA — unlike the NVIDIA hosts diff
above, this one **replaces** the whole service, since so much changes between
the two engines:

```yaml
  supervisor:
    build:
      context: https://github.com/lanefritzel/vLLM-dashboard.git#main:supervisor
      dockerfile: Dockerfile.llamacpp
      args:
        # Pin by digest, not the floating `:server` tag -- bump deliberately.
        BASE_IMAGE: ghcr.io/ggml-org/llama.cpp@sha256:<current server digest>
    restart: unless-stopped
    network_mode: host
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: all
              capabilities: [gpu]
    volumes:
      - /path/to/your/hf-models:/models
      - supervisor-state:/state
    environment:
      ADMIN_PORT: "18001"
      DASHBOARD_EVENT_URL: "http://127.0.0.1:8080/internal/supervisor-events"
```

On Intel/AMD instead of NVIDIA, swap the `deploy.resources.reservations`
block for `devices: ["/dev/dri"]`, same as the main example's `supervisor`
block — everything else above is engine-specific, not GPU-vendor-specific.

Postgres and the `dashboard` service are unchanged from the main example.
When you create a recipe for an instance on this server, pick **llama.cpp**
as its engine — the recipe editor, Help page, and Playground all adapt to
show that engine's own fields (context size, GPU layer offload) instead of
vLLM's. There's no Neuron map for llama.cpp instances — see
[Engine differences](#engine-differences).

## Configuration

| Variable | Required | Description |
|---|---|---|
| `ConnectionStrings__DefaultConnection` (or `DB_CONNECTION`) | yes | PostgreSQL connection string. |
| `Kestrel__Endpoints__Http__Url` | no | Defaults to `http://+:8080` inside the image. |
| `DATAPROTECTION_KEYS_DIR` | recommended | Directory (on the `/data` volume) for ASP.NET Core Data Protection keys — without a persistent volume here, every container restart invalidates existing login sessions. |
| `MODELS_BROWSE_ROOT` | no | Host models directory mounted into the container (default `/models`), used to offer a navigable folder picker when adding a server or a local model. Falls back to a free-text field if nothing's mounted there. |
| `Visualizer__Port` | no | Port the neuron-map plugin listens on (default `9998`). |
| `Visualizer__Token` | no | Shared secret, if the plugin was started with `VIS_TOKEN`. Sent as a Bearer header. |
| `Visualizer__HostOverride` | no | Reach the plugin on this host instead of the instance's own host. Rarely needed. |

Everything else — servers, instances, models, recipes, metrics retention — is
configured at runtime from the UI, not via environment variables.

### Neuron map plugin

The supervisor image installs a vLLM plugin (`supervisor/vllm-visualizer`) and
sets `VIS_ENABLED=1`, so the Overview **Neuron map** works with no extra
configuration on the standard host-network stack. These variables go on the
**supervisor** service (vLLM inherits them):

| Variable | Default | Description |
|---|---|---|
| `VIS_ENABLED` | `1` (set by the image) | `0` turns the plugin off for that server. It is only read when vLLM starts. For a switch that needs no restart, use **Plugin on/off** in the Neuron map section. |
| `VIS_BIND` | `127.0.0.1` | Address the plugin listens on. Use `0.0.0.0` only if the dashboard reaches vLLM over the LAN, and set `VIS_TOKEN` too. |
| `VIS_PORT` | `9998` | Plugin port (match `Visualizer__Port` on the dashboard). |
| `VIS_TOKEN` | unset | Require `Authorization: Bearer <token>` (or `?token=`) on every request. |
| `VIS_EVERY` | `2` | Record every Nth model step. `1` is smoothest (about 1.4% slower measured); higher is lighter. |
| `VIS_IDLE_GAP` | `0.6` | Seconds with no model step that count as "no request in flight" (used by a graceful off). |
| `VIS_PREFILL_MIN` | `32` | A step with at least this many tokens counts as a new prompt arriving (used by a graceful on). |

**The neuron map needs "Enforce eager" on.** The plugin reads each layer through
Python forward hooks. With eager mode off, vLLM compiles the model and captures
CUDA graphs, and hooks do not run inside a captured graph, so the map stays
blank (the model still answers normally). For a recipe that runs with CUDA graphs,
add the environment variable `VIS_ENABLED=0` to the recipe so the plugin stays out
of the way, and keep a separate eager recipe for the same model for when you want
to watch the map. See [Eager mode vs CUDA graphs](#eager-mode-vs-cuda-graphs).

Less common tuning (`VIS_BUCKET`, `VIS_ARM_TTL`, `VIS_MAX_STREAMS`,
`VIS_MAX_ERRORS`) is documented in the plugin's own README. The plugin only
reads numbers vLLM already computes, never changes a response, and switches
itself off after repeated errors, so vLLM keeps serving either way.

## Ports and volumes

- **Port `8080`** — the web UI and the `/proxy/<slug>/v1/...` API endpoint.
  (Port `8445` is also exposed for optional HTTPS — see `Kestrel__Endpoints__Https__*`
  env vars if you want to terminate TLS here instead of in front of it.)
- **Port `9998`** — the neuron-map plugin, inside the supervisor/vLLM host. It
  listens on loopback only by default and is never published; the dashboard
  reads it over the shared host network.
- **Volume at `/data`** — Data Protection keys (`/data/dataprotection`). Mount
  this as a named volume or bind mount so login sessions survive container
  restarts.

## Using the proxy

Every registered server/instance gets a stable OpenAI-compatible proxy
endpoint at:

```
http://<host>:8080/proxy/<slug>/v1
```

Point any OpenAI-compatible client (LibreChat, OpenWebUI, opencode, a raw
SDK) at that URL instead of the backend directly. If the client requests a
different model than what's currently running, the dashboard swaps it via
that instance's supervisor first, then forwards the request — the client
never needs to know a swap happened. `/v1/models` on that same endpoint
returns every model registered for the instance, not just whatever's
currently loaded, so a client with model auto-discovery (e.g. LibreChat's
`fetch: true`) picks up the full catalog automatically.

## Getting started: the three steps

1. **Servers** — register the machine (label, GPU info, models directory) and
   an instance on it (proxy slug, engine, port, the supervisor's own URL). Use
   **Test connection** before saving.
2. **Models & Recipes** — add a model (a local path, or download one from
   Hugging Face through the supervisor) and create a recipe for it — fields
   depend on the instance's engine (see [Engine differences](#engine-differences)).
3. **Overview** — pick a recipe and swap it in. The swap view shows a live
   diff, step-by-step progress as the supervisor reports it, and the result.

A fresh install shows this same checklist on the Overview page until your
first successful swap.

### Example recipe

A concrete, real-world recipe — a 27B Qwen3 MoE model (INT4), quantized,
running on an Intel GPU host — to show what the less-obvious fields are for.
Your own values will differ; this is here for the escape-hatch fields
(speculative decoding, extra args) that are easy to miss:

| Field | Value |
|---|---|
| Served model name | `qwen3.8-vllm` |
| Max model length | `131072` |
| GPU memory utilization | `0.90` |
| KV cache dtype | `fp8_e4m3` |
| Data type (`--dtype`) | `float16` (left at `auto`, this model silently resolves to `bfloat16` on the Intel stack — set it explicitly) |
| Enforce eager | on |
| Block size | `64` |
| Max num batched tokens | `8192` |
| Enable auto tool choice | on |
| Tool call parser | `qwen3_coder` |
| Reasoning parser | `qwen3` |
| Speculative decoding | on, tokens = `4` |
| Extra args | `enable-mamba-cache-stochastic-rounding` (blank value — boolean flag), `mamba-ssm-cache-dtype` = `float16` |

The **Extra args** row is the general escape hatch for any vLLM flag the
typed recipe fields don't cover — like this model's Mamba-cache options —
without waiting on the dashboard to add a dedicated field for it.

### Example llama.cpp recipe

A llama.cpp recipe has its own, much smaller dial set — no GPU memory
utilization, dtype, or block size, since those are vLLM-specific:

| Field | Value |
|---|---|
| Served model name | `gemma-uncensored` |
| Max model length (`--ctx-size`) | `8192` |
| GPU layers | **All** (or **CPU only**, or a specific count via Extra args) |
| Extra args | any other `llama-server` flag, e.g. `flash-attn` (blank value — boolean flag) |

`--metrics` and a free port are added automatically; you don't set them.

### Known recipes

Settings that were actually loaded and served, as a starting point. They were
tested on one box (**NVIDIA RTX 3060 12 GB, Intel i7-4790, vLLM 0.33.0**), so
treat them as a starting point for your own hardware, not a guarantee. Fields not
listed are left at their defaults (KV cache dtype `auto`, dtype `auto`, speculative
decoding off). These are examples from one machine; the dashboard's **Help** page has a
hardware-neutral guide to working out your own settings (memory budget, KV cache size,
max model length, and how to read vLLM's start-up log).

| Model | Max len | GPU mem | Enforce eager | Batched tokens | Block | Extra |
|---|---|---|---|---|---|---|
| `Qwen/Qwen3-0.6B` | 40960 | 0.90 | on | 8192 | 64 | Small, loads in under a minute; a good first test. |
| `unsloth/gemma-3-4b-it` | 4096 | 0.90 | on | 2048 | 16 | Fits comfortably in 12 GB. |
| `nicklas373/Huihui-gemma-4-12B-it-abliterated-AWQ` (Gemma 4 12B, 4-bit AWQ, **fast**) | 1024 | 0.93 | **off** | 4096 | 16 | Extra arg `max-num-seqs` = `4`; env var `VIS_ENABLED` = `0`. |
| same model, **eager** (neuron map works) | 2048 | 0.95 | on | 4096 | 16 | About 12 tokens/s on that box, far slower than the row above. |

Things that bit during testing:

- **Gemma 4 is multimodal**, so vLLM refuses to start unless *Max batched tokens*
  is at least **2496** (room for one image). The default 2048 fails with
  `max_tokens_per_mm_item (2496) > max_num_batched_tokens`.
- **Gemma 4 GGUF files do not load** in vLLM 0.33 (multimodal GGUF is not
  supported). Use an AWQ or other safetensors build of the same model.
- **Qwen3-0.6B cannot use `mtp` speculative decoding**; leave it off.
- **On a 12 GB card with CUDA graphs on**, graphs and compile buffers come out of
  the GPU memory budget. At 0.85 a 12B 4-bit model leaves almost nothing for the
  KV cache; use 0.93 to 0.95 and cap `max-num-seqs`.

### Eager mode vs CUDA graphs

- **Enforce eager on** runs the model one operation at a time from Python. It
  starts quickly and is the safest setting, but on a slow CPU it can be the
  bottleneck: on the test box vLLM waited on one busy CPU core while the GPU was
  about a third busy.
- **Enforce eager off** lets vLLM compile the model and capture CUDA graphs, which
  replays the whole forward pass at once. It was much faster for generation on the
  same box, at the cost of a longer first start (the swap's *Warm up engine* step
  shows the compile and graph capture) and more GPU memory.
- **The neuron map only works with eager on** (see above).

### Why a model load can take minutes

vLLM reads the whole model from disk, so the first load of a model that is not in
the operating system's file cache is limited by your disk. From a busy spinning
disk (about 55 MB/s) a 7.7 GB model took 2.5 minutes to read; the same file read
from cache took 3 seconds. The swap dialog shows where the time goes: *Start new
process* (vLLM importing, about a minute on a slow CPU), *Load weights*, *Warm up
engine*, then *Health check*.
