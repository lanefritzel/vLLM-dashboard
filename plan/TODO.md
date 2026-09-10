# TODO

Deferred work — not scheduled, revisit when the need actually arises.

## Groups (Phase 4 of the proxy/auto-swap/idle-unload/preload work)

Phases 1–3 are built and live-verified: transparent proxy with auto-swap-on-request,
TTL idle-unload, and startup preloading. Phase 4 — named `swap`/`exclusive` groups, so
e.g. a small always-on embedding model can coexist with a large exclusive one without
one evicting the other — is deferred. Low likelihood right now of needing to run more
than one model at a time.

**Real gap found before deferring, not yet solved:** today a source has exactly one
container name and one serving port, because only one model is ever active at once.
Two genuinely-concurrent active models need their own container identity (name +
port) each, not just their own database row. Any future groups work must add
per-model (or per-group) container naming/port allocation — not just a `ModelGroup`
entity plus activation bookkeeping — or the UI would end up claiming two models are
active while only one container can actually exist.

Revisit when running multiple models at once on the same source becomes a real need.
