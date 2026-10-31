# Conversion mapping (P01b)

Old column -> new column, one line each, derived from doc 4 "Conversion" + doc 13
gap 3, cross-checked against a restored copy of the live DB (2026-10-31). Per the
Build Plan's rule, the owner reviews this before any conversion code gets written.

## Live DB snapshot (what we're actually converting)

- 2 `PhysicalServers`: `tes.home` (Intel, has 1 source) and `Serenity` (Nvidia, 0
  sources today -- still converts to a `servers` row with no instances; nothing in
  the rules says to skip an empty one).
- 1 `Sources` row (vLLM, slug `tes-home`), 0 Ollama sources.
- 4 `Models` rows on `tes.home`'s catalog: 1 active (`qwen3.8-vllm`), 3 inactive
  catalog entries never deployed.
- 1 `LaunchSpecs` row (tied to the vLLM source) -- not converted per the rules;
  its stored image (`0.21.0-b3`) is already stale vs. the live container's
  `0.26.0-b2`, which is further reason it shouldn't be trusted going forward.
- 1 Identity user.

## servers (from PhysicalServer)

| Old (`PhysicalServer`) | New (`servers`) | Note |
|---|---|---|
| `Label` | `label` | |
| `DockerHost` | `docker_host` | |
| `GpuVendor` | `gpu_name` | Free-text now (doc 4 says no vendor enum) -- store the vendor string as-is (`"intel"`, `"nvidia"`); not reformatted into "Intel (12 GB)" since GpuVramTotalGb isn't always set (tes.home's is blank). |
| `GpuVramTotalGb` | `gpu_mem_gb` | Nullable on both sides; blank stays null. |
| *(none)* | `gpu_count` | No old equivalent -- defaults to `1`. |
| `HfCacheHostPath` | `models_dir` | Direct carry-over. |
| `CreatedAt` | `created_at` | |
| `HfToken`, `UpdatedAt` | *(dropped)* | Not part of the new `servers` shape; HfToken is empty on both live rows today anyway. |

## instances (from Source)

| Old (`Source`) | New (`instances`) | Note |
|---|---|---|
| `Label` | `label` | |
| `Slug` | `proxy_slug` | **Kept byte-for-byte** (invariant 1). |
| `PhysicalServerId` | `server_id` | Resolved through the already-converted `servers` row for that `PhysicalServer`. |
| *(parsed from `InferenceUrl`)* | `port` | Parse the port out of `InferenceUrl` (e.g. `http://tes.home:9999` -> `9999`); fall back to `9999` if unparseable. The live row parses cleanly to 9999. |
| *(none)* | `admin_url` | Always `NULL` on conversion -- "needs supervisor registration" until the operator registers one (doc 13). |
| *(none)* | `poll_interval_ms` | No old equivalent -- defaults to `1000`. |
| `Active` | `is_active` | Direct carry-over. |
| `CreatedAt` | `created_at` | |
| `Type`, `MetricsUrl`, `InferenceUrl`, `DockerHost`, `ContainerName`, `ActiveModelId`, `PreloadModelId`, `ScrapeIntervalSeconds`, `SortOrder`, `UpdatedAt` | *(dropped)* | `Type`: an Ollama-typed source would be dropped entirely, not just these columns (see below) -- there are none live today. `ActiveModelId`/`PreloadModelId`: explicitly not converted (the supervisor's own `last_good` replay decides what runs). `MetricsUrl`/`InferenceUrl`/`DockerHost`/`ContainerName`: compose/supervisor-owned now. |

## models (from Model) + one recipes row each

| Old (`Model`) | New (`models`) | Note |
|---|---|---|
| `Name` | `name` | |
| `DisplayName` | `display_name` | |
| `PhysicalServerId` | `server_id` | Same server resolution as instances. |
| `ModelSource` (`"local"`/`"huggingface"`) | `source_type` | Direct carry-over of the two allowed values. |
| `ModelPathOrRepoId` (when `ModelSource="huggingface"`) | `repo_id` | |
| `ModelPathOrRepoId` (when `ModelSource="local"`) | `path` | For a `huggingface` model, `path` has no old equivalent -- left blank/flagged in the report rather than guessed (none of the 4 live models are affected: the one `local` model already has a real path; the 3 `huggingface` ones have no on-disk path yet since they were never downloaded). |
| *(derived)* | `load_format` | `"gguf"` when the model name/path indicates a GGUF build (e.g. the live `ggml-org/Qwen3.8-27B-GGUF` catalog entry); `"auto"` otherwise. |
| *(none yet)* | `size_bytes`, `quantization`, `config` | No old equivalent -- null until a later phase (P08 downloads / model inspection) populates them. |
| *(derived)* | `status` | `"available"` for the `local` model with a real path. For a `huggingface`-sourced model with no file on disk yet, **owner-confirmed 2026-10-31**: `"download_failed"` (closest existing value), with an explicit report line per such model noting it was never downloaded -- not an actual failure -- so this isn't misread as an incident later. |
| `CreatedAt` | `created_at` | |
| `SourceId`, `SwapArgs`, `OptionsJson` (direct column) | *(dropped as columns, but `OptionsJson` feeds the new recipe below)* | `SourceId` is deprecated even in the old schema. |
| `IsActive`, `IdleTtlSeconds`, `PreloadOnStartup` | *(not converted)* | Explicitly listed as not converted (old active-model pointers, idle-TTL, preload). |
| `ImageOverride` | *(not converted)* | Launch-spec-owned, compose owns images now. |

### recipes (one per converted model, named `<model> (migrated)`)

`OptionsJson` keys map to recipe dials where one exists; everything else goes to
`extra_args`. From the live active model's actual stored options:

| `OptionsJson` key | Recipe field | 
|---|---|
| `dtype` | `dtype` |
| `enable-auto-tool-choice` | `enable_auto_tool_choice` |
| `tool-call-parser` | `tool_call_parser` |
| `reasoning-parser` | `reasoning_parser` |
| `max-model-len` | `max_model_len` |
| `gpu-memory-utilization` | `gpu_memory_utilization` |
| `mm-encoder-tp-mode` | *(no dial)* -> `extra_args: [{key: "mm-encoder-tp-mode", value: "data"}]` |

Fields the dial table has but `OptionsJson` never stored for this model
(`kv_cache_dtype`, `enforce_eager`, `max_num_batched_tokens`, `block_size`,
`speculative_decoding`/`speculative_tokens`) get the recipe's own defaults, **not**
the live compose's values -- the stored row is the source of truth for conversion,
not the compose file (the page is explicit: "use the oracle for pass/fail, and
report any differences from this to the owner" -- those differences are exactly
this list, and they'll show up in the conversion report, not be silently
back-filled from the compose reference). `served_model_name` comes from the old
`Model.Name` ("qwen3.8-vllm"), which already matches the live compose's
`--served-model-name` exactly.

## Dropped / not converted (confirmed against the live data)

- Ollama sources and their models: none exist in the live DB today, so nothing to
  drop in practice, but the routine still needs the code path (and a report line
  "0 Ollama sources found") so it's exercised and correct when/if one exists later.
- `LaunchSpecs` (image, devices, memory, labels, `ImageOverride`, ports): not
  converted. The live row's stored image is already stale vs. the running
  container, reinforcing that it shouldn't be trusted as a source of truth.
- Idle-TTL, preload flags, active-model pointers: not converted (all three columns
  are blank/false on every live row anyway).
- Identity, Data Protection keys, metrics/rollup/app-log tables: untouched.

## Open questions -- resolved (owner-confirmed 2026-10-31)

1. **`models.status` for a never-downloaded `huggingface` catalog entry**:
   `"download_failed"`, with an explicit per-model report line making clear it was
   never downloaded, not an actual failure.
2. **Model path scheme**: the page's "doesn't follow hf-cache/&lt;slug&gt;" note was
   about the container-side mount, not the stored host path. The live qwen
   model's stored path does follow the scheme format-wise -- but see the
   finding below, added after the owner shared the real production compose.

Mapping approved; proceeding to code P01b against it.

## Finding added 2026-10-31 (after seeing the real production compose)

The owner rarely uses the model/options UI -- values are set directly in the
production compose file, so the DB's `Model`/`LaunchSpec` rows have drifted from
what's actually running. Confirmed against the real compose:

- The options drift already documented above (stored `gpu-memory-utilization .80`
  vs. compose `0.90`; missing `enforce-eager`/`kv-cache-dtype`/`block-size`/
  `max-num-batched-tokens`/`speculative-config`; extra `mm-encoder-tp-mode` with no
  dial) is confirmed real, not a fixture artifact.
- **New finding**: the stored `Model.ModelPathOrRepoId`
  (`/media/external6tb/llm-models/hf-cache/Qwen3.8-27B-INT4`) is a *stale* path --
  the real compose mounts `/home/lanef/hf-models/Qwen3.8-27B-INT4` (with the
  `/media/external6tb/...` line present but commented out, apparently an older
  model location). This is a genuine path-reconciliation item for P16a/b prep, not
  a formatting issue -- the conversion report (run against a restored copy of the
  live DB) will carry an explicit note about it.
