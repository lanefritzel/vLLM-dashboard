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
| — | — | (none yet) | — | — |

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
