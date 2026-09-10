# 08 — Open Issues & Risks

Open items and how they are handled. A fresh session should work these top-down.

## A. Open items (need input or later action)

| # | Item | Status | Handling |
|---|---|---|---|
| A1 | **Percentiles (P50/P95/P99) for History** | open | MVP computes from raw avg (honest "avg" label). True percentiles require retaining histogram `_bucket` series (more storage). Decision: ship avg-based, flag in UI ("avg, not percentile") until buckets are stored. If user wants true percentiles: extend parser to keep buckets + rollup percentiles. |
| A2 | **llama-swap ↔ hot-swap port continuity** | open | After a hot-swap recreates the backend container, llama-swap (tes:8000) must still route to the same backend port. Verify `LaunchSpec.Ports` host port == 9999 (or whatever llama-swap targets). Documented in `05-D` step 6. Confirm with user before first real swap. |
| A3 | **Docker API TLS certs** | open | `tcp://:2375` is unauthenticated (full host control). User chose tcp:// on all machines. Mitigation: document firewall restriction to dashboard IP + TLS 2376 as upgrade path; app supports cert-based client if provided. Provisioning certs is out of MVP scope. |
| A4 | **Docker API availability on lf.home / serenity** | open | Only tes.home probed (2375 closed as of verification — needs enabling). Confirm each host exposes 2375/2376 before enabling hot-swap on that source (UI: disable swap when `DockerHost` unset/unreachable). |
| A5 | **lf.home role** | open | May get vLLM later. Nothing to do now — multi-instance makes it additive (add a source). Its current :8000 server identity is unknown/irrelevant. |
| A6 | **Ollama → vLLM migration (serenity)** | open | When done: edit the serenity source (type `ollama`→`vllm`, URLs to the new vLLM port). History is preserved (same SourceId). No code change. |
| A7 | **Intel/vLLM version drift** | open | Primary+legacy metric-name fallbacks handle minor drift. If a future intel/vllm renames core metrics, update `05-A` table + parser tests. |
| A8 | **GPU device requests in LaunchSpec** | open | vLLM on Intel GPU (Intel fork → likely Intel GPU, not CUDA). `DeviceRequests`/`--gpus` mapping depends on the driver plugin (intel-gpu / HAXM etc.). MVP: free-text `GpuOptions` + document the exact `docker run --device`/compose `deploy.resources` the user currently uses; capture it in the LaunchSpec before first swap. |

## B. Risks & mitigations

| # | Risk | Impact | Mitigation |
|---|---|---|---|
| R1 | Real backend metrics differ from the probed intel/vllm surface | wrong/missing KPIs | Primary+legacy fallbacks; parser never throws; per-metric null → UI shows "n/a"; live-verified mapping in `05-A`. |
| R2 | Hot-swap kills the only live model (user is talking through it) | loss of the live session's backend | Swap is user-triggered + confirm dialog + health-wait; on failure no active flip + banner. **Strongly document: do not swap the source that is serving your current session.** A2 + UI warning. |
| R3 | Docker API over unauthenticated tcp:// on LAN | host compromise if exposed | Firewall to dashboard IP (doc), TLS path (A3), per-source DockerHost (don't expose globally), swap only when configured. |
| R4 | Postgres outage stalls live loop | dashboard freezes | Batched inserts fire-and-forget with overflow queue; live ring + broadcast independent of DB; UI shows "storage degraded" if queue saturates. |
| R5 | Counter-reset / clock skew corrupts rates | misleading spikes | Reset-safe aggregation (unit tested); rate null on reset (no spike); `Ts` from app clock, consistent source. |
| R6 | Chart.js over LAN without internet | charts fail to load | Vendor Chart.js into `wwwroot/lib` (no CDN). |
| R7 | SignalR reconnect storms on flaky LAN | hub overload | Auto-reconnect with backoff; `JoinSource` re-sends full ring window on (re)join (idempotent chart re-seed). |
| R8 | First-run self-register race (two users register simultaneously) | two "first" admins | `HasUsers` check inside a transaction with a unique "first-admin" guard (or a `SystemConfig` row `first_user_claimed`); second concurrent registration → pending. |
| R9 | Scope creep: "replace llama-swap" | MVP delay | Explicitly out of scope (NG1); multi-instance + hot-swap are independent; replacement is a future feature if requested. |
| R10 | Intel fork may not expose `/metrics` by default on older setups | no data | `/metrics` confirmed live on tes:9999 (verified). If a future host lacks it → surfaced "metrics unavailable" state (N6), with a hint to add `--enable-metrics`. |

## C. Deviations log (fill during build)

| Date | Milestone | What deviated from plan | Why | Decision |
|---|---|---|---|---|
| 2026-09-08 | M1 | Flask draft (`app.py`, `static/`) could not be deleted | This checkout contains only `plan/` + `.git` — the Flask files were never committed here (they live in the user's other working copy). Nothing to delete. | No action; M1 task 8 satisfied trivially. |
| 2026-09-08 | M1 | Docker verification run on port **8090**, not 8080 | `8080` is already bound by the user's `model-forge-web-1` container. Did not disturb it. | Verify on 8090 via `DASHBOARD_PORT=8090 docker compose up`. Compose still maps 8080 by default. |
| 2026-09-08 | M1 | Identity password policy relaxed (min 6, no upper/digit/non-alnum) | `AddIdentityCore` default policy rejected the test password; LAN dashboard users need simple local credentials. | Relaxed in `Program.cs` (`RequireUppercase/Lowercase/Digit/NonAlphanumeric = false`). Re-tighten via Settings later if desired. |
| 2026-09-08 | M1 | Register must create the `admin` role before `AddToRoleAsync` | `AddIdentityCore(...).AddRoles<IdentityRole>()` registers the role store but does not seed rows; EF role store throws "Role ADMIN does not exist". | Register.razor creates the `admin` `IdentityRole` on first user if absent (idempotent). |
| 2026-09-08 | M1 | `Npgsql.EntityFrameworkCore.PostgreSQL` pinned to **10.0.3** | NuGet has no stable 10.0.8 of Npgsql (only Microsoft.* packs go to 10.0.12). | Use 10.0.3 for Npgsql, 10.0.12 for the Microsoft.* packs. |
| 2026-09-09 | M2 | Pages call services directly via DI instead of `/api/*` REST endpoints (M3 task 6, M4) | Blazor InteractiveServer components can resolve the same `ISourceCatalog`/`IModelCatalog`/`IMetricsHistory` services directly; a parallel REST layer would duplicate validation and antiforgery handling. | Pages inject scoped services; `IMetricsHistory` etc. are consumed in-circuit. No `/api/history`, `/api/sources` endpoints. |
| 2026-09-09 | M2 | Dropped `receiveState` and `modelChanged` hub events; `configUpdated` covers all rebinds | State already travels inside each sample; model changes raise `ConfigStore.Changed`, and the polling service broadcasts `configUpdated` on every rebind. | Overview + SourceSelector re-read on `configUpdated` only. Simpler contract, one fewer event to keep in sync. |
| 2026-09-09 | M2 | `chart_push_all` batch JS function added | Four parallel `chart_push` fire-and-forget calls raced on the same Chart.js instances. | Single `chart_push_all(label, groups, max)` call per sample batch. |
| 2026-09-09 | M2 | Single SignalR connection per browser: only Overview subscribes to the hub | `hub.js` holds one shared `HubConnection`; a second `hub_start` from SourceSelector would have torn down the first. | SourceSelector is a plain interactive island (DI + `IConfigStore.Changed` event, no hub). |
| 2026-09-09 | M2/M3 | `VllmPrometheusSource` normalizes `MetricsUrl` (appends `/metrics` if missing) | Per `04-data-model.md`, `MetricsUrl` is the base (`http://tes.home:9999`); the first live run requested the bare base and got 404. | `NormalizeMetricsUrl` in the scraper ctor — tolerant of users entering either form. |
| 2026-09-09 | M2/M3 | Scoped `SourceCatalog`/`ModelCatalog` now inject `IConfigStore` (with `RaiseChanged()` added to the interface) instead of the concrete `ConfigStore` | Concrete class is registered only under the interface → DI validation failed on app start. | Interface got `RaiseChanged()`; both catalogs depend on `IConfigStore`. |
| 2026-09-09 | M2/M3 | `MetricsPollingService` (singleton) resolves `ISourceCatalog` via `IServiceScopeFactory` inside `RebindAsync` | Singleton hosted service cannot consume the scoped catalog (DI validation error). | Scope-per-rebind, matching `RollupService`'s existing pattern. |
| 2026-09-09 | M2/M3 | `DbInitializerHostedService` moved to the front of hosted-service registration | Hosted services start in registration order; polling started before migrations ran → `relation "Sources" does not exist`, and the polling service gave up on first failure. | DB init (migrate + seed) now starts before `MetricsPollingService`/`RollupService`. |

## D. Confirmed decisions (do not re-litigate without user)

- .NET 10 Blazor Server + SignalR; PostgreSQL 16; Docker (:8080 published, pg internal).
- First-run self-register; first user admin; later signups need approval.
- Theming dark/light/system, per-user persisted.
- Mock-first; real tes:9999 as primary seed; quick-test via direct backend port.
- 30-day raw + hourly rollups.
- Real hot-swap via Docker Engine API (tcp://, all machines) with DB model registry +
  launch spec.
- Ollama scaffold only (no token-rate fabrication).
- llama-swap stays; dashboard bypasses it (metrics + probe → direct backend).
- Multi-instance first-class; config everything in-app at runtime.
