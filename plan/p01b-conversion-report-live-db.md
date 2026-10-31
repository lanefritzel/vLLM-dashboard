# P01b conversion report — restored copy of the live DB

Run 2026-10-31 against a `pg_dump` of the live `tes.home` database, restored to a
throwaway local Postgres (not the live DB itself — pulled via `pg_dump` over SSH,
copied locally, restored, converted, then the throwaway container and dump file
were deleted). Produced by `LegacyConversionService` (P01b); re-run is idempotent
(verified: a second pass against the same data reports zero for everything below).

```
Servers converted: 2
Instances converted: 1
Models converted: 4
Recipes created: 4
Ollama sources dropped: 0
Notes: 4
  [model_never_downloaded] Model 'Qwen3.8-27B-GSQ-RCO' was registered but never downloaded -- marked download_failed; this is not an actual download failure.
  [model_never_downloaded] Model 'Intel/gemma-4-31B-it-int4-AutoRound' was registered but never downloaded -- marked download_failed; this is not an actual download failure.
  [options_without_dial] Model 'qwen3.8-vllm': option(s) mm-encoder-tp-mode have no recipe dial -- moved to extra_args.
  [model_never_downloaded] Model 'ggml-org/Qwen3.8-27B-GGUF' was registered but never downloaded -- marked download_failed; this is not an actual download failure.
```

## What this means, in plain terms

- **Both physical servers converted** (`tes.home`, `Serenity`) — `Serenity` has no
  sources today, so it converts to a `servers` row with zero instances. Nothing
  to review there.
- **1 instance converted** (`tes-home`, proxy slug kept byte-for-byte). `admin_url`
  is `NULL` — it'll show "needs supervisor registration" until you register one.
- **4 models converted**, 1 "available" (`qwen3.8-vllm`, the one actually in use),
  3 marked `download_failed` because they were registered in the catalog but never
  actually downloaded — not a real failure, just "not usable yet." These are the
  3 inactive catalog entries you never deployed.
- **4 recipes created** (one per model, named `<model> (migrated)`).
- **0 Ollama sources** — matches what you told me; nothing dropped.

## Things worth your attention before P16a

1. **`qwen3.8-vllm`'s converted recipe doesn't match what's actually running.**
   The DB's stored options (`gpu-memory-utilization .80`, missing
   `enforce-eager`/`kv-cache-dtype`/`block-size`/`max-num-batched-tokens`/
   `speculative-config`, plus an extra `mm-encoder-tp-mode` with no recipe dial)
   reflect what was last saved through the dashboard's UI, not the real compose
   file you shared with me, which has `0.90`, `enforce-eager`, a `kv-cache-dtype`
   of `fp8_e4m3`, etc. You told me you mostly set values via compose directly
   rather than the UI — this conversion only has the DB to work from, so the
   migrated recipe inherits that drift. **You'll want to hand-edit this recipe
   (or re-derive it from the real compose) before actually deploying from it.**
2. **`qwen3.8-vllm`'s stored path is stale.** The DB says
   `/media/external6tb/llm-models/hf-cache/Qwen3.8-27B-INT4`; the real compose
   mounts `/home/lanef/hf-models/Qwen3.8-27B-INT4` (the old path is commented out
   there). The conversion carries the DB's path over as-is — it doesn't move
   files or guess at the real one. Worth fixing before P16a.
3. **The 3 `download_failed` models** are just unused catalog clutter (never
   downloaded, never deployed) — fine to leave as-is, or delete them before P16a
   if you'd rather start clean.

## Review

This needs your sign-off before P01b is considered done — not just that the
routine ran, but that the above is what you'd expect it to produce.
