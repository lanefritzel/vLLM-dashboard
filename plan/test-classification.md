# Existing unit test classification (P00)

Classifying `tests/Dashboard.Core.Tests/*` per the P00 page's keep/port vs
drop-later buckets. **Nothing deleted in P00** -- this is bookkeeping for
later phases to act on.

## Keep / port

Tests covering logic the refactor keeps (proxy, metrics/rollups, auth/
identity, catalog validation):

| File | Why |
|---|---|
| `ModelProxyServiceTests.cs` | Tests `ModelProxyService`'s pure helpers -- `TryExtractModelName`, `IsModelsListPath`, `TryExtractDeltaText`. (Corrected: an earlier pass here said "swap-trigger logic" -- a grep of the file found no swap-related assertions; the actual swap decision in `EnsureModelActiveAsync` isn't covered by this file at all.) Still keep/port: these are the request-parsing helpers the `/proxy/<slug>/v1` route depends on (invariant 1), just worth a read before P11 to confirm swap-path coverage isn't silently missing. |
| `SlugTests.cs` | Slug generation/parsing backs the proxy URL invariant -- converted slugs must stay byte-for-byte. |
| `MetricsAggregatorTests.cs` | Rollups/history -- kept per the Start Here page. |
| `PrometheusParserTests.cs` | `/metrics` scraping -- kept, independent of launch/container mechanics. |
| `IdentityModelTests.cs` | Auth/identity -- Identity + Data Protection keys carry over per the conversion decisions. |
| `HuggingFaceModelLookupServiceTests.cs` | Parses HF repo metadata (safetensors param count) -- a model-catalog utility with no DockerOrchestrator/LaunchSpec coupling; P08 (model downloads) will still need this. |

## Judgment call -- re-check at the named phase, not settled

| File | Why it's not a clean keep |
|---|---|
| `ModelInfoTests.cs` | Tests `ModelInfo.RequiresReload`, whose own doc comment says it exists to decide when "a container already running this model won't pick the change up until it's recreated," and it explicitly references `DockerOrchestrator.BuildCreatePayload`. That's hot-swap/launch-command coupling, not pure catalog validation -- closer to the drop-later bucket than I first classified it. Revisit in P09 (arg builder / save-time validation) once recipes replace the launch-command path this logic is actually gating. |

## Drop later (not now)

Tests tied to mechanics the refactor removes (the dashboard creating/
recreating containers, per-model `LaunchSpec`, idle-unload):

| File | Why |
|---|---|
| `DockerOrchestratorPayloadTests.cs` | Explicitly named in the P00 page -- container create/recreate payloads go away; the supervisor owns the vLLM process (invariant 4). |
| `LaunchSpecTests.cs` | Explicitly named -- `LaunchSpec` is replaced by recipes (P09a/P09b). |
| `ActivityTrackerTests.cs` | Backs `IdleUnloadService`'s idle-TTL check; idle-unload is retired per the Start Here decisions. |

## Not yet classified

None -- all 10 files in `tests/Dashboard.Core.Tests/` are listed above.
`tests/Dashboard.Web.Tests/` currently has no test files to classify.
