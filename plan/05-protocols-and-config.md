# 05 — Protocols, Metrics Mapping & Configuration

## A. vLLM source (primary) — verified live on `tes.home:9999`

Server: **intel/vllm** `0.21.1.dev0+gad7125a43.d20260810` (Intel fork of vLLM —
standard vLLM API + Prometheus surface). Model: `qwen3.8-vllm`. All metric series
carry labels `engine="0"` and `model_name="..."`.

### Endpoint contract
- `GET /metrics` → Prometheus text format (`# HELP`/`# TYPE` + series lines).
- `GET /v1/models` → OpenAI-style list `{"data":[{"id":"qwen3.8-vllm",...}]}`.
- `GET /health` → `200 OK`. `GET /version` → `{"version":"..."}`.
- `POST /v1/completions` → OpenAI completions (quick-test target).

### Metric → dashboard field mapping (primary first, legacy fallback)

| Dashboard field | Primary metric (verified present) | Legacy fallback (older vLLM) | Transform |
|---|---|---|---|
| running | `vllm:num_requests_running` | (same name historically) | sum over `engine` labels |
| waiting | `vllm:num_requests_waiting` | — | sum over `engine` |
| kv_pct | `vllm:kv_cache_usage_perc` | `vllm:gpu_cache_usage_avg` (0–1) | ×100 if <1 and metric is `*_avg`; `*_perc` already 0–100 |
| gen_tps | `vllm:generation_tokens_total` | `vllm:num_tokens_generated` | Δcounter/Δt (reset-safe) |
| prompt_tps | `vllm:prompt_tokens_total` | `vllm:num_prompt_tokens` | Δcounter/Δt |
| req_tps | `vllm:e2e_request_latency_seconds_count` | `vllm:request_success_total` | Δcounter/Δt |
| ttft_avg | `vllm:time_to_first_token_seconds` | — | histogram `_sum`/`_count` |
| itl_avg | `vllm:request_time_per_output_token_seconds` (verified) | `vllm:inter_token_latency_seconds` (verified) → `vllm:time_per_output_token_seconds` | histogram avg |
| e2e_avg | `vllm:e2e_request_latency_seconds` | `vllm:request_e2e_time_seconds` | histogram avg |
| queue_avg | `vllm:request_queue_time_seconds` | `vllm:request_queue_time_seconds` | histogram avg |
| prefix_hit (bonus) | `vllm:prefix_cache_hits_total` / `vllm:prefix_cache_queries_total` | — | ratio of rates (Δhits/Δqueries) |
| preemptions (bonus) | `vllm:num_preemptions_total` | — | Δcounter/Δt (or cumulative display) |

**Note on ITL:** the live server exposes BOTH `vllm:inter_token_latency_seconds` and
`vllm:request_time_per_output_token_seconds`. Use `request_time_per_output_token_seconds`
as primary (it is the per-request average ITL histogram), fallback to
`inter_token_latency_seconds`.

### Prometheus parser requirements
- Strip `#` comment lines; parse `name{labels} value`.
- Ignore `_created` gauge suffixes; for histograms collect `_sum`, `_count`,
  (ignore `_bucket` at MVP — avg is enough; keep buckets for future percentiles).
- **Label-aware**: group by `model_name`; sum `engine` dimension per model.
- Counter resets: if `value < prev` → treat as reset (rate = (value - 0)/dt or
  discard sample for that metric); store baseline on first sight.
- Malformed/unknown lines: skip, never throw.
- Percentiles (P50/P95/P99) for the History page: at MVP compute from raw
  distribution if buckets retained, else from stored avg; if true percentiles are
  required later, store histogram buckets — flagged in open issues.

### Rate/aggregation algorithm (aggregator)
```
For each counter metric with current value v at time t, previous value p at time t0:
  if p == null: baseline = v; rate = null (first sample)
  elif v < p:    reset detected → rate = null (or (v - 0)/(t-t0) if conservative)
  else:          rate = (v - p) / (t - t0)
For each histogram (avg): avg = sum_delta / count_delta (reset-safe like counters)
```
`MetricsSample` stores the rates/avgs; `Ts` = scrape time.

## B. Ollama source (scaffold) — verified live on `serenity.home:11434`

Endpoints (standard Ollama REST):
- `GET /api/version` → `{"version":"..."}` (status check).
- `GET /api/tags` → `{"models":[{"name":"gemma4:latest","model":"...","size":...,
  "details":{...}}]}` (catalog).
- `GET /api/ps` → `{"models":[{"name":"...","size_vram":..., ...}], "running":[...]}`
  (loaded models + VRAM).
- `POST /api/generate` → `{"response":"...", ...}` (probe; non-stream,
  `stream:false`).

Mapping: `MetricsSnapshot` for Ollama carries **availability**, `LoadedModelNames[]`,
`TotalVramBytes`, `CatalogCount` — NOT token rates (Ollama exposes none). UI shows
an "Ollama" variant of the KPI row (status, loaded models, VRAM) instead of the
vLLM charts. This is the scaffold; a full implementation later only adds fields.

## C. llama-swap (NOT a source — documented for context)

- `tes.home:8000` = llama-swap router. It exposes llama.cpp-style `/health` +
  `/version`, routes inference, but **no `/metrics` and no `/v1/models`**.
- The dashboard **does not** use it. Metrics + quick-test target the direct backend
  (`tes:9999`). llama-swap stays in the user's stack permanently for now; the
  dashboard bypasses it. If the user later wants the dashboard to *replace*
  llama-swap routing, that is a separate feature (out of MVP scope, see NG1).

## D. Docker Engine API (hot-swap) — `DOCKER_HOST=tcp://<host>:2375`

Transport: plain HTTP JSON (Engine API), via `HttpClient`. TLS (`tcp://:2376`)
supported by `HttpClientHandler` client-cert config if the user enables it (open
issue: certs provisioning).

Endpoints used:
- `GET  /v1.43/containers/{name}/json` — inspect (exists? image? state).
- `POST /v1.43/containers/{name}/stop` — stop (t=10).
- `DELETE /v1.43/containers/{name}?v=true&force=true` — remove.
- `POST /v1.43/images/create` — pull image if not present (`fromImage`, `repo`, `tag`).
- `POST /v1.43/containers/create` — create with config from `LaunchSpec`
  (Image, Cmd [model-arg substituted], HostConfig {NetworkMode, PortBindings,
  Binds, Env, RestartPolicy, DeviceRequests for GPU if needed}).
- `POST /v1.43/containers/{id}/start` — start.
- Health-wait: poll `GET {inference_url}/v1/models` every 2 s up to 180 s until
  200 + expected model id present.

Hot-swap flow (`DockerOrchestrator.SwapModelAsync(sourceId, model)`):
1. Load `LaunchSpec` for source; build `Cmd` substituting `{model}` (from
   `Models.SwapArgs` if present, else `Models.Name`).
2. Inspect current container; stop + remove (force).
3. Ensure image present (pull if missing).
4. Create + start container (same name from `LaunchSpec`/source config).
5. Health-wait on `/v1/models` (180 s timeout). On success: set `Model.IsActive=true`
   (deactivate others), broadcast `model-changed`. On timeout/failure: surface error,
   mark source state `swap-failed`, DO NOT flip active model.
6. llama-swap routing note: after swap, llama-swap (if pointed at the same
   container/port) continues to route — no change needed there, but verify the
   backend port in `LaunchSpec.Ports` matches what llama-swap expects (open issue).

## E. Configuration system

- **Startup config** (env / appsettings): `ConnectionStrings__Db`, `Urls`
  (`http://+:8080`), `DockerHost` default, retention default.
- **Runtime config** (Postgres `Config` table + `Sources`/`Models`/`LaunchSpecs`):
  everything user-editable. `IConfigStore` exposes typed getters and raises
  `Changed`. `MetricsPollingService` rebinds on change. Settings page writes via
  services (not raw DB) so events fire.
- **First-run seeding** (see `04-data-model.md` § Seeding).

## F. Environment variables (for `deploy/.env.example`)

```
COMPOSE_PROJECT_NAME=vllm-dashboard
DASHBOARd_PORT=8080
DB_HOST=postgres
DB_PORT=5432
DB_NAME=dashboard
DB_USER=dashboard
DB_PASSWORD=<change-me>
DOCKER_HOST=tcp://tes.home:2375          # default; per-source override in UI
VLLM_URL=http://tes.home:9999             # first-run seed default
VLLM_INFERENCE_URL=http://tes.home:9999   # first-run seed default
OLLAMA_URL=http://serenity.home:11434     # optional seed (inactive)
ASPNETCORE_ENVIRONMENT=Production
```
