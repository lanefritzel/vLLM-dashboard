# 01 — Requirements

## Context

The user runs model servers on a home LAN (192.168.0.x, mDNS `.home` names):

- **tes.home (192.168.0.170)** — current vLLM host. The live backend is **intel/vllm**
  (Intel fork; standard vLLM API + Prometheus metrics) on port **9999**, model
  `qwen3.8-vllm`. Traffic normally goes through **llama-swap** on port 8000, which is
  a routing proxy only (no metrics, no model swapping).
- **serenity.home (192.168.0.34)** — Ollama on port 11434 (model `gemma4:latest`).
  To be **migrated to vLLM** soon; kept as an option host.
- **lf.home (192.168.0.117)** — small machine; may get vLLM later. Keep as an option.

The user wants a dashboard to monitor these servers in real time, keep historical
usage (to review when running projects), hot-swap models, and eventually replace
llama-swap's routing role. This is an MVP; llama-swap stays for now.

## Functional requirements

### F1. Real-time monitoring (vLLM — primary)
- Scrape vLLM `/metrics` (Prometheus text) per source at a configurable interval.
- Live KPI cards + 4 live charts (see `06-ui-specification.md`).
- Metrics: requests running, waiting, KV cache usage, generated/prompt tokens/sec,
  requests/sec, TTFT, ITL, E2E latency, queue time; plus bonus: prefix-cache hit
  rate, preemptions.
- Multi-instance: any number of vLLM sources, selectable in the UI; per-source
  isolation of metrics, models, launch spec.

### F2. Historical usage (PostgreSQL)
- Persist every raw sample with timestamp, source, model.
- History page: date-range, per source/model, throughput + latency percentiles.
- Retention: raw samples 30 days (configurable), then permanent hourly rollups.
- Purpose: user reviews usage "when I run projects" — must survive app restarts.

### F3. Model hot-swap (real, via Docker)
- DB-backed **model registry** per source (admin adds entries: name + optional args).
- Per-source **launch spec** in DB: image, ports, env, volumes, restart policy,
  run args. Editable in admin UI.
- Hot-swap = stop/remove backend vLLM container → recreate from stored spec with the
  model argument substituted → health-wait on `/v1/models` → flip active model.
- Uses Docker Engine API over `tcp://` (user will open it on all machines).
- Active model is persisted per source and is the default for quick-test + UI badge.

### F4. Quick-test inference probe
- Textarea + max_tokens → POST `/v1/completions` against the source's `inference_url`
  (direct backend, not the llama-swap router). Show returned text + latency ms.
- For Ollama sources: `/api/generate`.

### F5. Ollama integration (scaffold for MVP)
- Second source type: status (`/api/version`), model catalog (`/api/tags`),
  loaded models + VRAM (`/api/ps`), quick-test probe (`/api/generate`).
- No token-rate charts for Ollama (it doesn't expose them); show availability,
  loaded models, VRAM instead.
- Must be architecturally ready so a full implementation is additive only.

### F6. Authentication
- ASP.NET Identity, stored in PostgreSQL.
- First-run self-registration: **first registered user becomes admin**.
- Subsequent signups require admin approval (pending state).
- All pages and the SignalR hub require auth (`[Authorize]`).

### F7. Theming
- CSS custom properties; modes: dark (default), light, system-follow.
- Per-user preference, persisted (DB).
- Instant client-side switch (Blazor Server: CSS on `<html>` via JS interop).

### F8. Configurable everything (Settings page)
- Sources CRUD: label, type, metrics_url, inference_url, docker_host, interval.
- Models registry CRUD + active selection.
- Launch spec editor.
- Preferences: theme, default history window, UI update rate.
- System (admin): retention days, rollup toggle, user management/approvals.
- Changes apply at **runtime without restart** (config-changed event → polling
  service rebinds source; SignalR broadcast to connected clients).
- First-run defaults seeded from the verified environment (tes:9999 source).

## Non-functional requirements

- **N1. SOLID** — strict dependency direction `Web → Application → Core ←
  Infrastructure`; `Core` has zero project dependencies.
- **N2. Patterns** — Strategy (IMetricsSource), DI-backed Factory, Adapter,
  Repository, Observer (SignalR), Facade. See `03-design-patterns.md`.
- **N3. .NET 10** — SDK 10.0.300 is installed; target `net10.0`.
- **N4. Docker** — multi-stage `sdk:10.0` → `aspnet:10.0`, non-root user, published
  on :8080. Compose: dashboard + postgres:16. Postgres NOT published (internal).
- **N5. Tests** — unit (Prometheus parser, rate/percentile math, counter-reset
  handling) + integration (fake `/metrics` via HttpListener, hub round-trip).
- **N6. Robustness** — a down source must never crash the app; per-source error
  state shown in UI ("source unreachable"); metrics endpoint missing (old vLLM
  without `--enable-metrics`) is a distinct, surfaced state.
- **N7. Security** — no secrets in code; Docker API exposure limited to what's
  needed (document TLS as preferred); DataProtection keys persisted in a volume.

## Explicit non-goals (this MVP)

- **NG1.** Do NOT implement llama-swap replacement (model routing/swap at the
  router). llama-swap stays; it is out of scope beyond being a non-issue (dashboard
  bypasses it).
- **NG2.** Do NOT fake/derive Ollama token-rate metrics.
- **NG3.** No GPU monitoring (nvidia-smi / DCGM) — vLLM metrics only.
- **NG4.** No Grafana/Prometheus-side storage; Postgres is the only store.
- **NG5.** No OIDC/Keycloak; cookie auth only.
- **NG6.** No mobile native; responsive web is enough.
- **NG7.** No load testing / multi-user concurrent heavy-write optimization beyond
  sane batch inserts.
