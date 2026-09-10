# vLLM Dashboard

A self-hosted dashboard for monitoring and controlling one or more [vLLM](https://github.com/vllm-project/vllm)
(or Ollama) inference servers on your own network — live metrics, historical usage,
model hot-swapping, and a transparent request proxy with automatic model swapping,
idle GPU unloading, and startup preloading.

Think of it as **llama-swap, but with a UI**: a stable OpenAI-compatible endpoint that
your tools (OpenWebUI, opencode, any OpenAI SDK client) talk to directly, which swaps
the backend container to the right model on demand, unloads it when idle to free the
GPU, and brings it back automatically when the dashboard restarts.

> **GPU support:** hot-swap has been built and live-tested against an **Intel GPU**
> host (raw `--device` passthrough, e.g. `/dev/dri`). An NVIDIA code path exists
> (device requests via the `nvidia-container-runtime`) but hasn't been exercised
> against real NVIDIA hardware yet — treat it as untested, not verified.

## Features

- **Live monitoring** — requests running/waiting, KV cache usage, throughput,
  latency (TTFT/ITL/E2E), scraped straight from vLLM's Prometheus `/metrics`, with
  30-day history and hourly rollups.
- **Model-driven hot-swap** — a curated, typed catalog of vLLM launch options
  (dropdowns and suggestions instead of free-text flags) so swapping models doesn't
  mean guessing CLI syntax.
- **Transparent proxy with auto-swap** — point any OpenAI-compatible client at
  `/proxy/<source>/v1`; the dashboard swaps the backend container to the requested
  model automatically before forwarding, and answers `/v1/models` with the full
  registered catalog, not just whatever happens to be loaded.
- **Idle unload + startup preload** — stop a model's container automatically after
  it sits idle past a configurable TTL to free the GPU, and bring it back up on its
  own when the dashboard restarts if it isn't already running.
- **Live traffic view** — watch the messages sent, the model's reasoning, and its
  response stream in through the proxy in real time — actually useful for debugging
  why a model answered the way it did, not just a request counter.
- **Live container logs** — stream a backend container's stdout/stderr in the UI
  instead of `docker logs -f`-ing into a terminal.
- **Multi-instance** — any number of sources (vLLM or Ollama), each with its own
  metrics, model registry, and launch settings.
- **Docker-native** — hot-swap talks straight to the Docker Engine API and keeps a
  swapped container's Compose stack labels intact, so it doesn't drop out of
  Portainer's stack view.

## Quick start

```bash
docker run -d \
  --name vllm-dashboard \
  -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="Host=<your-postgres-host>;Port=5432;Database=dashboard;Username=dashboard;Password=dashboard" \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e DATAPROTECTION_KEYS_DIR=/data/dataprotection \
  -v vllm-dashboard-data:/data \
  lanedfritz/vllm-dashboard:latest
```

You'll need a PostgreSQL database reachable from the container — see the Compose
example below for a complete self-contained setup, including Postgres.

Open `http://<host>:8080`, register the first account (it becomes the admin
automatically), and add your first source from the Settings page.

## Docker Compose

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
    expose:
      - "5432"

  dashboard:
    image: lanedfritz/vllm-dashboard:latest
    restart: unless-stopped
    depends_on:
      postgres:
        condition: service_healthy
    environment:
      ConnectionStrings__DefaultConnection: "Host=postgres;Port=5432;Database=dashboard;Username=dashboard;Password=dashboard"
      ASPNETCORE_ENVIRONMENT: Production
      ASPNETCORE_URLS: "http://+:8080"
      DATAPROTECTION_KEYS_DIR: /data/dataprotection
      # Only needed for a source you want to hot-swap models on, e.g. tcp://tes.home:2375
      DOCKER_HOST: ""
    ports:
      - "8080:8080"
    volumes:
      - dataprotection:/data

volumes:
  pgdata:
  dataprotection:
```

Change `POSTGRES_PASSWORD` (and the matching password in the connection string)
before running this anywhere other than a trusted home network.

## Configuration

| Variable | Required | Description |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | yes | PostgreSQL connection string. |
| `ASPNETCORE_URLS` | no | Defaults to `http://+:8080` inside the container. |
| `DATAPROTECTION_KEYS_DIR` | recommended | Directory (on the `/data` volume) for ASP.NET Core Data Protection keys — without a persistent volume here, every container restart invalidates existing login sessions. |
| `DOCKER_HOST` | no | Default Docker Engine API endpoint (e.g. `tcp://host:2375`) offered when adding a new source. Each source's Docker host is actually configured per-source in the Settings page; this is just a convenience default. |

Everything else — sources, models, launch specs, metrics retention — is configured
at runtime from the Settings page, not via environment variables.

## Ports and volumes

- **Port `8080`** — the web UI and the `/proxy/<source>/v1/...` API endpoint.
- **Volume at `/data`** — Data Protection keys (`/data/dataprotection`). Mount this
  as a named volume or bind mount so login sessions survive container restarts.

## Hot-swap requirements

To use model hot-swap, idle-unload, startup preload, or the auto-swap proxy on a
given source, that source's Docker host needs its Engine API reachable from the
dashboard container (e.g. `tcp://<host>:2375`, or `2376` with TLS). This is
configured per-source in the Settings page, not globally. Sources you only want to
monitor (no swapping) don't need this at all.
