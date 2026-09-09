# 03 — Design Patterns (decided)

The user explicitly wants patterns "when they fit." This file records the decision
so a fresh session does not re-debate them.

## Used (and where)

### 1. Strategy — `IMetricsSource` (core of the design)
Different backends (vLLM Prometheus, Ollama REST, mock) share one contract:
```csharp
public interface IMetricsSource
{
    SourceType Type { get; }
    Task<SourceHealth> HealthAsync(CancellationToken ct);
    Task<MetricsSnapshot> ScrapeAsync(CancellationToken ct);
}
```
- Implementations: `VllmPrometheusSource`, `OllamaSource`, `MockMetricsSource`.
- **OCP**: adding a backend = new class + DI registration. No other code changes.

### 2. Factory — `IMetricsSourceFactory` (DI-backed, NOT a switch)
```csharp
public interface IMetricsSourceFactory
{
    IMetricsSource Create(SourceType type, SourceConfig config);
}
```
- Implementation resolves from DI (`IHttpClientFactory` + options per type). It is
  **not** a `switch` that news up concrete types — that would be the anti-pattern.
  The factory centralizes per-source configuration (URLs, intervals, HttpClient
  naming) and keeps the polling service ignorant of concrete types.
- Rationale for a factory here (not everywhere): sources are created/dropped at
  runtime from DB config; a single construction point keeps lifecycle + DI clean.

### 3. Adapter — wire formats → one `MetricsSnapshot`
Each source normalizes its native format (Prometheus text / Ollama JSON / mock) into
the same `MetricsSnapshot` record. The aggregator and everything downstream never
see raw wire formats. This is the adapter role (sometimes merged with the Strategy
class — acceptable; the *intent* is normalization).

### 4. Repository — `ISampleStore`, `IMetricsHistory`, `IModelCatalog`, `ILaunchSpecStore`
EF Core implementations. Clean data access; swappable for tests (in-memory fakes).
- `ISampleStore.Append` = ring buffer (live) + batched EF insert (durable).
- `IMetricsHistory` = windowed queries + rollup-aware reads.

### 5. Observer / Pub-Sub — `MetricsHub` (SignalR) + `IConfigStore.Changed`
- `MetricsHub` broadcasts samples/state to connected clients (observer over the network).
- `IConfigStore.Changed` (C# event) lets `MetricsPollingService` react to Settings
  edits without polling the DB for config (observer in-process).

### 6. Facade — `MetricsService`
Single entry point for the live loop: `RunScrapeCycleAsync(SourceId)`. Orchestrates
source → aggregator → store → broadcast. The polling service calls this; the facade
hides the pipeline from timers and tests.

### 7. Template Method (light) — scrape pipeline base
Optional `ScrapeAsync` skeleton in a base class (fetch → parse → map). Low value;
include only if the three sources start diverging messily. **Default: skip** unless
needed.

### 8. Singleton (composition root only)
EF `DbContext` is scoped (per unit of work), not singleton. Hosted services are
singletons. Nothing else is intentionally singleton.

## Explicitly rejected (and why) — do NOT introduce these

| Pattern | Why rejected |
|---|---|
| **Builder** | Source config is a flat POCO + validation; a builder adds ceremony with no benefit. |
| **Service Locator** | Violates DIP; defeats DI. Never `IServiceProvider.Get<T>()` in features. |
| **God class** | The anti-pattern the SOLID split exists to prevent. `MetricsService` is a thin facade, not a god object. |
| **Naive switch-factory** | `switch(type){case Vllm: return new Vllm(...)}` — defeats DI/testability. Use the DI-backed factory. |
| **Mediator** | No complex inter-component messaging beyond the hub; YAGNI. |
| **Proxy** | Remote proxies are not needed; plain `HttpClient` is fine. |
| **Command/Observer-for-everything** | Over-engineering risk. Only the hub + config event use pub-sub. |

## Mapping table (quick reference)

| Concern | Pattern | Type/Interface |
|---|---|---|
| Per-backend scraping | Strategy + Adapter | `IMetricsSource` (+ impls) |
| Create sources from config | Factory (DI) | `IMetricsSourceFactory` |
| Live push to UI | Observer | `MetricsHub` |
| React to config edits | Observer (event) | `IConfigStore.Changed` |
| Data access | Repository | `ISampleStore`, `IMetricsHistory`, `IModelCatalog`, `ILaunchSpecStore` |
| Pipeline orchestration | Facade | `MetricsService` |
| Rate/histogram math | (plain logic, tested) | `IMetricsAggregator` |
| Docker hot-swap | (dedicated service) | `IDockerOrchestrator` |
