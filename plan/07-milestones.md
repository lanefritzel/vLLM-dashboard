# 07 — Milestones (build order)

Build strictly in order. Each milestone: implement → `dotnet build` → `dotnet test`
→ commit-ready state. A fresh session resumes at the first milestone not marked done.

Status legend: `[ ]` not started · `[~]` in progress · `[x]` done

---

## M1 — Skeleton: solution, auth, theming, nav  `[ ]`

Tasks:
1. `dotnet new sln -n Dashboard`; projects: `src/Dashboard.Core` (classlib,
   net10.0), `src/Dashboard.Infrastructure` (classlib), `src/Dashboard.Application`
   (classlib), `src/Dashboard.Web` (blazor server via `dotnet new blazor --interactivity Server --auth Individual`), tests (xunit).
2. Reference graph per `02-architecture.md` (Core has zero project deps; enforce).
3. Identity (Individual auth template) + EF Core provider **Npgsql**; connection
   string from env. Migrations generated.
4. First-run self-register: `HasUsers` check on Register page (register page only
   when no users); first user auto-admin + `Status=active`; later users `Status=pending`.
   Cookie validation rejects `pending` (redirect to "awaiting approval" screen).
5. Theming: `theme.css` tokens (dark default/light/system), `JS/theme.js`,
   per-user `ThemeMode` persisted + applied on login.
6. Layout shell: header (logo, source selector stub, theme switcher, user menu),
   left nav (Overview, History, Models, QuickTest, Settings — all `[Authorize]`),
   login/register/awaiting screens.
7. `deploy/`: `Dockerfile` (multi-stage sdk:10.0 → aspnet:10.0, non-root, volume
   `/data` for DP keys + nothing else for now), `docker-compose.yml`
   (dashboard + postgres:16, dashboard :8080 published, pg internal),
   `.env.example` per `05-F`.
8. **Delete the Flask draft**: `app.py`, `static/` (superseded; user confirmed).
9. Seed step stub: on first run create default `Config` rows (no sources yet —
   sources come in M3 with the Settings page; or seed here per `04` §Seeding —
   decision: seed here so the app is usable immediately).

Deliverables: app runs in Docker (`docker compose up`), register → admin,
login, theme switches, nav renders, Postgres initialized, migrations applied.
Verification: manual checklist (register first user; second user → pending screen;
theme persists after refresh; `docker compose up` from clean volumes).

---

## M2 — vLLM live: scrape → aggregate → SignalR → charts  `[ ]`

Tasks:
1. `PrometheusParser` (Core or Infrastructure? → **Infrastructure/Scrapers**,
   tested in Core.Tests via public API) per `05-A` parser requirements (labels,
   sums, histograms, reset-safe, never throws).
2. `MetricsSnapshot` / `MetricsSample` records in Core.
3. `VllmPrometheusSource` (HttpClient via `IHttpClientFactory`; 5 s timeout;
   label-aware; primary+legacy name fallback per `05-A` table).
4. `MockMetricsSource`: random-walk generator (running 0–8, waiting 0–4, KV 20–95,
   gen 0–1500 tok/s with bursts, TTFT 80–800 ms, E2E 0.5–8 s) — deterministic
   seed optional; used until real wiring confirmed.
5. `IMetricsSourceFactory` (DI) + `IMetricsAggregator` (rate/hist math, reset-safe;
   unit tested).
6. `MetricsService` (Facade) + `MetricsPollingService` (per-source `PeriodicTimer`,
   rebind on `IConfigStore.Changed`) + `MetricsBroadcastThrottler` (coalesce to
   ~2 Hz) + `MetricsHub` (groups per source; `samples`, `source-state`).
7. `ISampleStore` in-memory ring (per source, 900 pts @ 2 s ≈ 30 min) + broadcast.
   (Durable writes land in M3; for M2 the ring + broadcast is enough — store
   interface already shaped for EF impl.)
8. UI: Overview page — KPI cards + 4 live charts (Chart.js vendored) + bonus strip;
   source selector wired to hub `JoinSource`; JS interop `chart_init/chart_push`;
   window selector (1/5/30 min); source status dots.
9. Error states: scrape failure → red pill + last-data faded; no crash.
10. Seed the tes source (`http://tes.home:9999`) active + model `qwen3.8-vllm`;
    verify live numbers from the real server.

Deliverables: `dotnet test` green (parser + aggregator unit tests); Overview shows
live data from `tes:9999` (and from mock when URL is unreachable).
Verification: point at real tes:9999 → charts move; kill nothing (read-only env);
switch to a bad URL → red state, no crash; `dotnet test` passes.

---

## M3 — Persistence: Postgres samples, History page, retention  `[ ]`

Tasks:
1. EF models + migrations: `Sources`, `Models`, `LaunchSpecs`, `MetricsSamples`
   (nullable `SourceId` + `SourceLabel` snapshot per `04`), `MetricsRollups`,
   `Config`, `Users` extensions (ThemeMode, Status).
2. `SampleStore` durable impl: batched `AddRange` + single `SaveChanges` per
   scrape batch; fire-and-forget with in-memory overflow queue (drop-oldest) on
   DB failure; live loop never blocks on DB.
3. `MetricsHistory` (repository): window queries with automatic raw→rollup cutoff;
   percentiles from raw (buckets retained from M2 parser? → parser keeps
   `_sum`/`_count`; percentiles at MVP = avg-based approximation, flagged in
   open issues).
4. `RollupService` + `RetentionService` (IHostedService): hourly upsert +
   30-day prune (batched deletes); reads `Config` for retention.
5. Settings page (all sections) + `IConfigStore` (typed, `Changed` event) +
   `ConfigStore` impl; runtime rebind of polling on source edits; "Test
   connection" button.
6. History page: controls + 4 charts + summary table (server-rendered data via
   `/api/history`).
7. First-run seeding finalized (tes source + model + config + optional serenity
   ollama inactive).

Deliverables: samples durably in Postgres; History shows 24 h+ after restart;
retention job demonstrable (set retention=0.1 d in a scratch DB, watch prune).
Verification: `docker compose up` with a fresh volume; check rows in
`metrics_samples`; restart container → history intact; edit a source in Settings →
polling rebinds without restart; `dotnet test` green (repository tests, rollup math).

---

## M4 — Models + hot-swap (Docker)  `[ ]`

Tasks:
1. `ModelCatalogService` (registry CRUD + single-active invariant) + Models page
   (registry table, active badge, add/edit/delete, activate).
2. `ILaunchSpecStore` + LaunchSpec editor (admin; JSON validation for ports/env/
   volumes).
3. `DockerOrchestrator` (Engine API `HttpClient` to `source.DockerHost`): inspect,
   stop, remove, pull-if-missing, create (config from `LaunchSpec`, `{model}`
   substitution), start, health-wait on `/v1/models` (2 s × 90 = 180 s).
4. Swap flow per `05-D` (steps 1–6) with per-step progress events over the hub
   (`swap-progress` message), confirmation UI, failure banner (active model NOT
   flipped on failure).
5. Quick-test page (`IInferenceProbe` → `/v1/completions` for vLLM, `/api/generate`
   for Ollama) — model defaults to active.
6. `model-changed` hub message + UI badge update.
7. Safety: hot-swap only enabled when `source.DockerHost` set; otherwise the button
   is disabled with a tooltip ("no Docker host configured").

Deliverables: Models page complete; swap flow works against a disposable Docker
host (test with a small image, e.g. a dummy container, to validate API calls;
real vLLM swap is user-triggered later and NOT run in CI/tests).
Verification: unit tests for model-arg substitution + launch-spec → container
config mapping; integration: orchestrator against local Docker (if available) or
recorded HTTP fixtures; swap UI shows steps. `dotnet test` green.

---

## M5 — Ollama scaffold  `[ ]`

Tasks:
1. `OllamaSource` (`/api/version`, `/api/tags`, `/api/ps`) → Ollama-shaped
   `MetricsSnapshot` (availability, loaded models, VRAM, catalog count).
2. `OllamaProbe` (`/api/generate`, non-stream).
3. Overview Ollama variant (KPI row: status/loaded/VRAM; note instead of charts).
4. Settings: source type `ollama` selectable; "Test connection" hits `/api/version`.
5. Seed (optional, inactive): serenity source per `04` §Seeding.

Deliverables: add an Ollama source → see status/catalog/loaded/VRAM live;
quick-test returns text from serenity (if up).
Verification: point at `serenity.home:11434` → catalog + loaded models show;
probe returns text; `dotnet test` green (Ollama parser with fixtures).

---

## M6 — Packaging, polish, handoff  `[ ]`

Tasks:
1. `Dockerfile` hardening: non-root, healthcheck (`/healthz` app endpoint),
   resource limits in compose; Postgres `healthcheck`; named volumes
   (`pgdata`, `dataprotection`).
2. `.env.example` finalized; compose `env_file` wiring.
3. `README.md` (repo root): quickstart (compose up, first-run admin, add sources),
   architecture overview, per-host setup notes (open :9999 metrics,
   `dockerd -H tcp://0.0.0.0:2375` on each host + firewall note, TLS 2376
   preferred), migration notes (serenity Ollama→vLLM: edit source type/URL).
4. Full test pass: `dotnet test` (unit + integration incl. fake `/metrics`
   HttpListener + hub round-trip).
5. Lint/format: `dotnet format --verify-no-changes` (or repo style) green.
6. End-to-end smoke on the real env (read-only where possible): compose up on this
   machine → Overview shows live tes:9999 data → History after a few minutes →
   add serenity Ollama source → catalog shows. (Hot-swap NOT exercised against the
   real vLLM — user-triggered.)
7. Update `07` statuses to `[x]`; log any deviations in `08`.

Deliverables: `docker compose up -d` from a clean clone + `.env` → working
dashboard; all tests green; docs complete.

---

## Cross-milestone rules

- `dotnet build` + `dotnet test` green at the end of every milestone (no drift).
- No secrets committed; `.env` gitignored; `.env.example` committed.
- Config changes only through `IConfigStore`/services (events fire).
- UI uses theme tokens only.
- If a contradiction with this plan is discovered during build: fix in code, log in
  `08-open-issues-and-risks.md` under "Deviations", do not silently deviate.
