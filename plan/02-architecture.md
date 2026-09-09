# 02 — Architecture

## Stack (confirmed)

- **Runtime:** .NET 10 (`net10.0`), SDK 10.0.300 installed at `/home/lanef/.dotnet10/sdk`.
- **Frontend:** Blazor **Server** (interactive server-side rendering) + SignalR for
  real-time push. Charts via **Chart.js** through JS interop (no heavy UI framework).
- **Backend services:** EF Core 10 + Npgsql (PostgreSQL 16). ASP.NET Identity.
- **Container:** multi-stage Dockerfile; `docker-compose.yml` with dashboard +
  postgres; dashboard published on **:8080**, Postgres internal **:5432**.
- **Docker API:** dashboard reaches remote Docker Engine API via
  `DOCKER_HOST=tcp://<host>:2375` (HTTP) or `:2376` (TLS) — plain `HttpClient`,
  no SDK dependency.

## Solution layout

```
Dashboard.sln
├── src/
│   ├── Dashboard.Core/                    # NO project dependencies (pure)
│   │   ├── Models/
│   │   │   ├── MetricsSample.cs           # one scraped+aggregated snapshot
│   │   │   ├── MetricsSnapshot.cs         # raw parsed values (pre-aggregation)
│   │   │   ├── SourceInfo.cs              # runtime view of a Source
│   │   │   ├── ModelInfo.cs
│   │   │   ├── LaunchSpec.cs
│   │   │   ├── InferenceRequest.cs / InferenceResult.cs
│   │   │   └── SourceType.cs              # enum: Vllm, Ollama, Mock
│   │   └── Abstractions/
│   │       ├── IMetricsSource.cs          # Strategy: ScrapeAsync(), HealthAsync()
│   │       ├── IMetricsSourceFactory.cs   # Create(SourceType) -> IMetricsSource
│   │       ├── IMetricsAggregator.cs      # snapshot + prev state -> sample (rates)
│   │       ├── ISampleStore.cs            # ring buffer + append
│   │       ├── IMetricsHistory.cs         # query window from Postgres
│   │       ├── IModelCatalog.cs           # registry CRUD + active model
│   │       ├── IInferenceProbe.cs         # completions/generate
│   │       ├── IDockerOrchestrator.cs     # stop/recreate/health-wait
│   │       ├── ILaunchSpecStore.cs
│   │       ├── IConfigStore.cs            # typed config + Changed event
│   │       ├── IRetentionService.cs       # prune + rollup
│   │       └── ITimeProvider.cs
│   ├── Dashboard.Infrastructure/          # deps: Core
│   │   ├── Scrapers/
│   │   │   ├── PrometheusParser.cs        # text -> MetricsSnapshot (label-aware)
│   │   │   ├── VllmPrometheusSource.cs
│   │   │   ├── OllamaSource.cs            # scaffold: /api/* only
│   │   │   └── MockMetricsSource.cs       # deterministic-ish random walk
│   │   ├── Persistence/
│   │   │   ├── AppDbContext.cs            # Identity + Source + Model + Sample + Rollup + Config
│   │   │   ├── SampleStore.cs             # in-memory ring + batch EF insert
│   │   │   ├── MetricsHistory.cs
│   │   │   ├── RollupService.cs
│   │   │   └── ConfigStore.cs
│   │   └── Docker/
│   │       └── DockerOrchestrator.cs      # Engine API client
│   ├── Dashboard.Application/             # deps: Core
│   │   ├── MetricsPollingService.cs       # per-source Timers
│   │   ├── MetricsHub.cs                  # SignalR hub
│   │   ├── MetricsService.cs              # Facade: scrape->aggregate->store->broadcast
│   │   ├── ModelCatalogService.cs
│   │   ├── InferenceProbeService.cs
│   │   └── MetricsBroadcastThrottler.cs   # coalesce to ~2 Hz
│   └── Dashboard.Web/                     # deps: Application, Infrastructure
│       ├── Program.cs                     # DI, Identity, EF, hosted services, hub
│       ├── Components/                    # Layout, Nav, pages (see 06)
│       │   ├── Pages/                     # Overview, History, Models, QuickTest, Settings
│       │   ├── Components/SourceSelector.razor, KpiCards, MetricCharts
│       │   └── JS/                        # charts.js (Chart.js bridge)
│       ├── wwwroot/css/theme.css          # custom properties, dark/light
│       └── ...
├── tests/
│   ├── Dashboard.Core.Tests/              # parser, aggregator math, counter-reset
│   └── Dashboard.Web.Tests/               # integration: fake /metrics, hub round-trip
└── deploy/
    ├── Dockerfile
    ├── docker-compose.yml
    └── .env.example
```

## Dependency rules (SOLID — enforce via csproj)

- `Core` → **no project references** (only BCL). Abstractions + records only.
- `Infrastructure` → Core. Implements scrapers + persistence + Docker client.
- `Application` → Core. Orchestrates; depends only on abstractions (DIP).
- `Web` → Application + Infrastructure (composition root registers concrete
  implementations for Core abstractions).
- **No cycles.** Application must NOT reference Infrastructure; DI in `Web/Program.cs`
  wires `IMetricsSourceFactory` → concrete sources.

## Runtime data flow (live loop)

```
[per source, every N sec]
MetricsPollingService timer
  → IMetricsSourceFactory.Get(source)      (cached per source, re-created on config change)
  → IMetricsSource.ScrapeAsync()           (adapter: parses wire format)
  → IMetricsAggregator.Aggregate(prev, snapshot, now)   (counter→rate, hist→avg, reset-safe)
  → ISampleStore.Append(sample)            (ring buffer + async batched EF insert)
  → IMetricsHistory (implicit via store)
  → MetricsBroadcastThrottler → MetricsHub.Clients.All.SendAsync("samples", batch)
                                          (coalesced ~2 Hz)
Blazor client: on "samples" → update Chart.js instances + KPI values (JS interop)
```

Error path: any scrape failure → source state = `unreachable` (with error text) →
broadcast `source-state` (not a crash). UI shows red pill per source.

## Hosted services (Program.cs)

1. `MetricsPollingService` (IHostedService) — owns per-source `PeriodicTimer` tasks;
   rebuilds timers on `IConfigStore.Changed`.
2. `RetentionService` (IHostedService) — hourly: delete raw samples older than
   retention; upsert hourly rollups; run at startup + daily for safety.
3. `DbInitializer` (one-shot) — `Database.Migrate()`, seed defaults (tes source,
   admin-less first-run flag).

## SignalR hub contract (v1)

- Endpoint: `/metrics-hub` (auth required — cookie auth + hub authorization policy).
- Client → server: `JoinSource(string sourceId)`, `LeaveSource()`, `SetUiRate(int ms)`.
- Server → client:
  - `samples(SampleBatchDto)` — coalesced new samples since last send (per group).
  - `source-state(SourceStateDto)` — up/down/error per source.
  - `config-updated(ConfigVersion)` — clients re-fetch `/api/config`.
  - `model-changed(ModelInfo)` — after hot-swap / active-model flip.
- Grouping: hub groups per source id so a client only receives its selected source;
  a `global` group for state/config events.

## Background/async notes

- Scraping uses `HttpClient` with short timeouts (5 s metrics, 60 s probe).
- Batched EF inserts (e.g., per source per scrape, or every 5 s) — single `SaveChanges`
  per batch; never per-sample.
- All timers are `CancellationToken`-bound to app lifetime; config changes cancel +
  restart cleanly (no leaked timers).
- Ring buffer per source (default 900 pts ≈ 30 min @ 2 s) for live charts; Postgres
  is source of truth for history.

## Configuration & secrets

- App config from env/`appsettings`: `ConnectionStrings__Db`, `DockerHost` (fallback),
  `Urls`, retention defaults.
- **Runtime** config (sources, models, launch specs, preferences) is in **Postgres**
  (not appsettings) — the Settings page is the source of truth after first run.
- DataProtection keys → persisted in `/data/dataprotection` (volume) so auth cookies
  survive restarts.
- `.env.example` in `deploy/` documents every env var.

## Testing strategy (N5)

- **Unit (Core.Tests):** `PrometheusParser` (labels, multi-engine sum, type lines,
  malformed lines), `MetricsAggregator` (rate calc, counter reset, first-sample
  baseline, histogram avg), `RollupService` math.
- **Integration (Web.Tests):**
  - Fake vLLM: `HttpListener` on loopback serving canned `/metrics` + `/v1/models` +
    `/v1/completions`; assert scrape → aggregate → broadcast round-trip.
  - Hub: `HubConnectionBuilder` against `TestServer` (or in-memory) — join, receive.
  - EF: InMemory or ephemeral Postgres for repository tests.
- Every milestone ends with `dotnet build` green + `dotnet test` green.
