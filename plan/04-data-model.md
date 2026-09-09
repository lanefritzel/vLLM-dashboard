# 04 — Data Model (PostgreSQL / EF Core)

Single database (PostgreSQL 16). One `AppDbContext` (Npgsql). Identity tables are
created by ASP.NET Identity; custom tables below live in the same context.

## Tables

### Sources
| Column | Type | Notes |
|---|---|---|
| Id | uuid PK | |
| Label | text | display name (e.g. "tes — main vLLM") |
| Type | text | `vllm` \| `ollama` \| `mock` |
| MetricsUrl | text | e.g. `http://tes.home:9999` |
| InferenceUrl | text | e.g. `http://tes.home:9999` |
| DockerHost | text? | e.g. `tcp://tes.home:2375` (for hot-swap) |
| ContainerName | text? | backend container name (hot-swap target) |
| ScrapeIntervalSeconds | int | default 5 |
| Active | bool | enabled toggle |
| SortOrder | int | UI ordering |
| CreatedAt / UpdatedAt | timestamptz | |

### Models (registry, per source)
| Column | Type | Notes |
|---|---|---|
| Id | uuid PK | |
| SourceId | uuid FK→Sources | |
| Name | text | model id (e.g. `qwen3.8-vllm`) |
| DisplayName | text? | optional pretty name |
| SwapArgs | text? | extra args substituted on hot-swap (e.g. `--model X --served-model-name X`) |
| IsActive | bool | at most one per source (app-enforced) |
| SortOrder | int | |

### LaunchSpecs (per source; drives Docker hot-swap)
| Column | Type | Notes |
|---|---|---|
| Id | uuid PK | |
| SourceId | uuid FK (unique) | |
| Image | text | e.g. `ghcr.io/intel/vllm:latest` |
| ModelArgTemplate | text | e.g. `--model {model}` — `{model}` substituted from Models.SwapArgs/Name |
| Ports | jsonb | `[{"host":"9999","container":"8000"}, ...]` |
| Env | jsonb | `{"HUGGINGFACE_TOKEN":"...","CUDA_VISIBLE_DEVICES":"0"}` |
| Volumes | jsonb | `[{"host":"/models","container":"/models","ro":true}, ...]` |
| RestartPolicy | text | `unless-stopped` etc. |
| ExtraRunArgs | text? | free-form |
| GpuOptions | text? | e.g. `--gpus all` (compose/docker run mapping) |

### MetricsSamples (raw; 30-day retention)
| Column | Type | Notes |
|---|---|---|
| Id | bigserial PK | |
| Ts | timestamptz | sample time (app clock) |
| SourceId | uuid FK (nullable) | index `(SourceId, Ts desc)`; `SET NULL` on source delete |
| SourceLabel | text | snapshot of source label (history survives source deletion) |
| ModelName | text? | from metric label `model_name` |
| Running | int | num_requests_running (summed over engines) |
| Waiting | int | num_requests_waiting |
| KvPct | numeric(5,2)? | 0–100 |
| GenTps | numeric(12,4)? | delta rate |
| PromptTps | numeric(12,4)? | |
| ReqTps | numeric(12,4)? | |
| TtftAvgSec | numeric(12,4)? | |
| ItlAvgSec | numeric(12,4)? | |
| E2eAvgSec | numeric(12,4)? | |
| QueueAvgSec | numeric(12,4)? | |
| PrefixHitRatio | numeric(5,4)? | bonus |
| PreemptionsTotal | bigint? | bonus |
| Up | bool | scrape succeeded |

Partitioning note: at MVP scale a plain table is fine; add `PARTITION BY RANGE (Ts)`
later only if > ~100M rows (documented as future work).

### MetricsRollups (permanent; hourly)
| Column | Type | Notes |
|---|---|---|
| Id | bigserial PK | |
| BucketStart | timestamptz | top of hour, UTC |
| SourceId | uuid FK | unique `(SourceId, ModelName, BucketStart)` |
| ModelName | text | |
| Samples | int | count of raw samples in bucket |
| AvgRunning / AvgWaiting / AvgKvPct | numeric | |
| MaxKvPct | numeric | |
| TokensGenerated | bigint | counter delta over bucket (exact, not avg) |
| TokensPrompt | bigint | |
| RequestsCompleted | bigint | |
| P50/P95/P99 E2eSec, TtftSec | numeric | from raw (or running approx) |
| AvgGenTps / PeakGenTps | numeric | |

Rollup semantics: `TokensGenerated`/`RequestsCompleted` are **deltas over the hour**
(counter diffs), so summing buckets = true totals. `MetricsHistory` reads raw for
recent windows and rollups for older ones (cutoff = retention boundary).

### Config (key/value, for app-level settings)
| Column | Type | Notes |
|---|---|---|
| Key | text PK | e.g. `retention.days`, `default.history.window` |
| Value | jsonb | |
| UpdatedAt / UpdatedBy | | |

Stores: `retention.days` (default 30), `rollups.enabled` (default true),
`ui.updateRateMs` (default 1000), `default.history.windowMinutes` (default 60).

### Users (ASP.NET Identity defaults + additions)
Standard Identity tables (`AspNetUsers`, roles, etc.) + one column/table:
| Column | Type | Notes |
|---|---|---|
| UserId | PK/FK→AspNetUsers | |
| ThemeMode | text | `dark` \| `light` \| `system` |
| Status | text | `active` \| `pending` (first user auto-`active`+admin) |

`IUserClaimsPrincipalFactory` / cookie validation rejects `pending` users
(login blocked or force-logout on rejection).

## EF Core notes

- Provider: **Npgsql** (`Npgsql.EntityFrameworkCore.PostgreSQL` 10.x).
- Migrations generated at build time; `Database.Migrate()` at startup.
- `MetricsSamples` inserts are **batched** (`AddRange` + one `SaveChanges`),
  fire-and-forget with retry; a DB outage must not stall the live loop (queue in
  memory, drop oldest, log).
- Soft delete: none at MVP. Sources/models can be removed; `MetricsSamples.SourceId`
  is nullable with `ON DELETE SET NULL` (plus the `SourceLabel` snapshot above) so
  history survives source deletion. `Models`/`LaunchSpecs` cascade on source delete.

## Retention & rollup job (RetentionService)

- Every hour (and at startup):
  1. Compute rollups for the last full hour (bucket now-2h → now-1h to be safe),
     `INSERT ... ON CONFLICT DO UPDATE` (idempotent).
  2. `DELETE FROM metrics_samples WHERE ts < now() - :retentionDays` (batched
     deletes of 10k rows to avoid long locks).
- Retention days from `Config` (`retention.days`, default 30), editable in Settings.

## Seeding (first run)

On first run (no `Sources` rows), seed:
1. Source `tes — main vLLM`, type `vllm`, MetricsUrl `http://tes.home:9999`,
   InferenceUrl `http://tes.home:9999`, DockerHost `tcp://tes.home:2375`.
2. Model `qwen3.8-vllm` (active) for that source.
3. `Config` defaults.
4. (Optional, commented) source `serenity — Ollama`, type `ollama`,
   MetricsUrl/InferenceUrl `http://serenity.home:11434`, inactive by default.

All seeded values remain editable in Settings.
