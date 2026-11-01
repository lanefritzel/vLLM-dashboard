# Refactor progress

Tracked here, not in the Outline KB, per the Build Plan's working rules.
Copy-pasteable commands/vars for re-running manual verification (ports,
tokens, SSH/pg_dump commands, Docker build/run invocations) live in
`plan/testing-reference.md`, not duplicated here.

| Phase | Status | Date | Notes |
|---|---|---|---|
| P00 | done | 2026-10-31 | See below. |
| P01a | done | 2026-10-31 | See below. |
| P09a | done | 2026-10-31 | Done out of table order, before P01b -- see below. |
| P01b | code + tests done; report awaiting owner sign-off | 2026-10-31 | See below. |
| P01c | done -- soak passed | 2026-10-31 | See below. |
| P02 | done | 2026-10-31 | See below. |
| P03a | done | 2026-11-01 | See below. |
| P03b | done | 2026-11-01 | See below. |
| P03c | done | 2026-11-01 | See below. |

## P00 -- Baseline & test harness

Branch: `refactor/supervisor-recipes`.

Done:
- `scripts/gate.sh`: build -> `dotnet test` -> fake-vLLM pytest smoke, one
  command, non-zero exit on failure. Ran clean on the unmodified app (build
  succeeded, 123/123 Core.Tests passed, 5/5 fake-vllm smoke tests passed).
- `tests/fake-vllm/fake_vllm.py`: stdlib-only HTTP test double. `GET /health`,
  `GET /v1/models`, `POST /v1/completions` + `/v1/chat/completions` (canned
  completion containing `OK`, SSE when `stream: true`). Flags: `--port`,
  `--served-model-name`, `--fake-slow-start <secs>`, `--fake-crash`,
  `--fake-exit-after <secs>`, `--fake-bad-output`. Logs one line per request.
- `tests/fake-vllm/test_fake_vllm.py`: pytest coverage for all of the above
  (scripted conversation, crash exit code, slow-start delay, bad-output).
- `deploy/docker-compose.test.yml`: bridge-network postgres + dashboard
  (built from the working tree) + fake-vllm (own `tests/fake-vllm/Dockerfile`).
  Supervisor service joins in P02. Verified with the owner's go-ahead: built
  and started all three containers, dashboard ran EF migrations and came up
  clean, and a sidecar `curl` container on the same bridge network confirmed
  `fake-vllm:9999/health` and `/v1/models` both respond -- network-level
  reachability is proven (no source is pre-seeded in the DB pointing at
  fake-vllm, since that's out of scope here). Torn down afterward
  (`docker compose down -v`), nothing left running.
- `plan/live-compose-reference.md`: the live host's actual vLLM/Postgres/
  dashboard compose, written from the P00 page's text (this repo's own
  `deploy/docker-compose.yml` doesn't include a vLLM service at all today --
  the live container is created by `DockerOrchestrator`, not a checked-in
  compose entry; noted in the file so it isn't mistaken for a gap).
- `plan/test-classification.md`: all 10 files in `tests/Dashboard.Core.Tests/`
  classified keep/port vs drop-later. Nothing deleted.
- `.gitignore`: added `.venv/`, `__pycache__/`, `.pytest_cache/` (gate.sh's
  venv and pytest's cache dir).

Decided with owner (2026-10-31):
- **Owner-assisted inventories** (`plan/inventory.md`): proxy-URLs-in-use and
  Ollama-sources done (no live clients/Ollama sources identified). On-disk
  model layout and the stock-image delivery check stay deferred to P16a
  prep -- both need the actual live host, which this dev machine isn't.
- **Gate scope**: kept `gate.sh` dotnet+pytest-only (no compose step wired
  in) -- dashboard-against-fake-vllm stays a manual
  `docker compose -f deploy/docker-compose.test.yml up` check, run by hand
  when wanted, not on every gate run.
- **Repo layout**: confirmed as proposed (`supervisor/` doesn't exist yet --
  that's P02; `tests/fake-vllm/`, `tests/integration/` (empty, for later),
  `plan/PROGRESS.md` all exist).
- **Owner's standing instruction going forward**: run the full
  gate/verification after each phase, not just at sign-off.

## Post-review fixes (2026-10-31, after owner feedback)

- `fake_vllm.py`: added `--host` (defaults to `127.0.0.1`; compose now passes
  `--host 0.0.0.0` explicitly) so a local pytest run doesn't bind every
  interface by default.
- `fake_vllm.py` / `test_fake_vllm.py`: added `test_fake_exit_after_dies_mid_run`
  -- `--fake-exit-after` had no coverage, and P03b depends on it.
- `deploy/docker-compose.test.yml`: `fake-vllm`'s `restart: unless-stopped` ->
  `restart: "no"` -- the old policy would have silently restarted the double
  after `--fake-crash`/`--fake-exit-after`, hiding the exact behavior those
  flags exist to test.
- `plan/test-classification.md`: corrected `ModelProxyServiceTests.cs`'s
  rationale (it covers pure parsing helpers, not swap-trigger logic -- no
  swap-related assertions exist there) and moved `ModelInfoTests.cs` out of
  "keep" into a new "judgment call, re-check at P09" bucket, since
  `RequiresReload` is explicitly hot-swap/launch-command coupled per its own
  doc comment.
- Re-ran `./scripts/gate.sh` (now 6/6 fake-vllm tests) and re-verified the
  test compose end to end after these changes -- both clean.

## P01a -- New schema (EF migrations)

New, additive-only tables for the refactor (`servers`, `instances`, `models`,
`recipes`, `deployments`, `deployment_events`), matching doc 4's reference
DDL exactly (`GENERATED ALWAYS AS IDENTITY`, the two CHECK constraints, the
partial unique index on `deployments(instance_id)`).

Done:
- Entity classes in `src/Dashboard.Infrastructure/Persistence/V2/` (namespace
  `Dashboard.Infrastructure.Persistence.V2`), mapped in `AppDbContext` via new
  `*V2` DbSet properties, as the phase page's naming guidance asked for --
  avoids any collision with the legacy `Model`/`Source`/`PhysicalServer`
  entities those old code paths keep using until P16.
- One migration (`20261031210349_AddV2Schema`) creates all six tables; legacy
  tables and Identity untouched (diff is purely additive -- verified in the
  migration file and the model snapshot diff).
- Test-seed fixture (1 server, 1 instance, 2 models, 2 recipes) in the new
  `tests/Dashboard.Infrastructure.Tests` project, specifically *not* in
  `Dashboard.Infrastructure` -- nothing in the production app can load it.
- New `tests/Dashboard.Infrastructure.Tests` project (xunit + Testcontainers.PostgreSql,
  per your call on the test-DB-strategy question) covers every DoD bullet.
  Tightened twice after an initial pass that was weaker than the literal DoD
  wording: the old-schema-copy test now dumps **every column of every row of
  every legacy table** via raw ADO.NET before/after the upgrade and asserts
  the dumps are identical strings -- not just a few fields I'd named by hand
  (`Label`/`DockerHost` would've missed a regression in, say, `HfToken` or
  `UpdatedAt`). The empty-DB boot test now actually instantiates and runs
  `Dashboard.Web.DbInitializerHostedService` (the real startup path:
  migrate + seed config defaults + backfills) against a fresh DB, with a
  capturing logger asserting no warning/error was logged -- not just a
  direct `AppDbContext` query, which never exercised that service at all.
  10/10 pass.
- Also ran the literal DoD wording by hand against a throwaway local
  Postgres container (not Testcontainers): `dotnet ef database update`
  against a fresh DB (exit 0), then against a DB rolled back to the last
  pre-V2 migration with a `PhysicalServers` row inserted, re-applied forward
  (exit 0), and confirmed that row's `Label`/`DockerHost` were unchanged.
  Container removed afterward.
- `./scripts/gate.sh` still passes (123 Core.Tests + 10 new Infrastructure.Tests
  + 6 fake-vllm smoke tests).

Note for next time `dotnet test`/`gate.sh` runs: this phase added a test
project that starts a real Postgres container via Testcontainers --
`dotnet test` now invokes Docker automatically, by your choice when I asked
about the test-DB strategy (the alternative was an env-var-gated manual
Postgres that silently skips if unset).

Out of scope, not touched: reading/writing these tables from UI/API (P09b+),
the legacy tables, conversion (P01b).

## P09a -- Arg builder + compatibility pass

Done out of the table's listed order. P01b's own Definition of done needs a
golden test comparing a converted recipe's arg-builder output to the oracle
argv -- but P01b is listed as needing only P01a, and P09a (the arg builder)
didn't exist yet. Flagged this to the owner rather than stub it; owner chose
to do P09a first, then come back to P01b.

Open point (a) from the phase page (owner-confirmed 2026-10-31, before
coding): added `recipes.speculative_tokens` (nullable int) via a follow-up
migration (`AddRecipeSpeculativeTokens`); `ArgBuilder` hard-codes the
speculative-config method to `"mtp"` (the only method the schema supports).

Open point (b) from the phase page (not actionable yet, logged for later):
P03a's deploy body will need an `env` map so `extra_env` actually reaches
the child process -- P03a doesn't exist yet in this repo, so this is a
one-line follow-up to make when P03a is built, not something to act on now.

Done:
- `Dashboard.Core.Recipes.ArgBuilder.Build(model, recipe, instance, server)`
  -- pure, no I/O -- plus plain input records (`Recipe`, `RecipeModel`,
  `RecipeInstance`, `RecipeServer`, `RecipeExtraArg`, `RecipeEnvOverride`)
  deliberately decoupled from the EF `V2.*` entities, so this stays pure
  code with zero DB dependency as the phase page requires; a caller maps
  from the persisted recipe once that wiring exists (P09b+).
- `Dashboard.Core.Recipes.CompatibilityPass.Check(model, recipe, server)` --
  hard-blocks (tensor-parallel-size > GPU count, missing model path, model
  not "available") and soft-warnings (oversized max_model_len, GPU memory
  utilization too low for the model's weight size). Decides, doesn't
  enforce -- that's P09b/P10a/the UI's job.
- Golden-table tests (`tests/Dashboard.Core.Tests/ArgBuilderTests.cs`,
  `CompatibilityPassTests.cs`): every DoD bullet, including the live-qwen
  fixture compared against the oracle as a flag->value dictionary rather
  than positionally -- the oracle command places `--port` right after
  `--served-model-name` (the old app's own convention), while this
  builder's fixed precedence always injects `--port` last, so a strict
  positional match would fail even on fully correct output. Noted this
  interpretation explicitly in the test's doc comment since it's a
  judgment call on what "flag-for-flag" means.
- `./scripts/gate.sh` still passes (142 Core.Tests, up from 123 -- 19 new;
  10 Infrastructure.Tests; 6 fake-vllm smoke tests).

Out of scope, not touched: persistence, UI, calling the supervisor (P10a),
save-time validation rules (P09b).

## P01b -- Conversion migration (old rows -> new)

Needs a copy of the live DB -- owner pulled one via SSH + `pg_dump` (not read
directly by me; restored locally, converted/verified, then the throwaway
container and dump file were deleted, per the page's "do not read the live
DB yourself" rule).

Key findings along the way (all owner-confirmed before coding/finalizing):
- `plan/conversion-mapping.md` -- old column -> new column, approved before
  any code was written, including two resolved open questions (status for
  never-downloaded models -> `download_failed` with a report note; the path
  scheme note was about the container-side mount, not the stored host path).
- Owner shared the real production compose file: confirmed the DB's stored
  model options and path for `qwen3.8-vllm` have drifted from what's
  actually running (owner sets values via compose directly, rarely uses the
  dashboard's model UI). This isn't a bug to fix -- it's exactly what the
  conversion report exists to surface, and now does (see
  `plan/p01b-conversion-report-live-db.md`).
- The golden test needed two comparison exclusions beyond the page's own
  "modulo the instance port and compose-owned settings" wording, both
  owner-confirmed: the container model path (old resolves it from the
  model's *served name*, new resolves it from the model's *host path* --
  genuinely different, redesigned schemes, not a bug) and flags the new
  arg builder always emits with a default (`--max-num-batched-tokens`,
  `--block-size`) even when the legacy row never configured them -- a
  subset match (every oracle flag must match) rather than set equality.

Done:
- `src/Dashboard.Infrastructure/Conversion/`: `LegacyConversionService`
  (transactional, idempotent -- keyed by the same natural keys the new
  schema already enforces uniqueness on, so a second run finds everything
  already there and inserts nothing), `ConversionReport`/`ConversionReportLine`,
  `ILegacyConversionService`.
- `tests/fixtures/legacy-oracle/*.json`: `live-qwen.json` (the real live row,
  including its known drift), `ollama-source.json`, `two-sources-one-server.json`.
- `tests/Dashboard.Infrastructure.Tests/`: `LegacyOracleFixture`/`LegacyOracleFixtureSeeder`
  (load + seed the above), `LegacyConversionServiceTests` (7 tests covering
  every DoD bullet), `LiveDbConversionManualCheck` (env-var-gated, skipped by
  default -- re-run instructions in its doc comment for when this needs
  re-verifying, e.g. before P16a).
- Ran the routine against the actual restored live DB copy: 2 servers, 1
  instance, 4 models (1 available, 3 `download_failed` -- never downloaded,
  not a real failure), 4 recipes, 0 Ollama sources, idempotent on a second
  run, Identity row (1 user) unchanged. Full report:
  `plan/p01b-conversion-report-live-db.md` -- **awaiting owner review**, per
  the DoD's last bullet.
- `./scripts/gate.sh` still passes (142 Core.Tests, 17 Infrastructure.Tests
  [10 + 6 new + 1 env-gated skip], 6 fake-vllm smoke tests).

Out of scope, not touched: dropping the legacy tables and wiring this into
the real migration chain (P16a), moving model files on disk, registering
supervisors (P15b).

## P01c -- Postgres-over-bridge soak test

Design decision: the page's two bullets ("open a pooled connection -> idle ->
query -> repeat" and "include a Blazor-like pattern: a long-lived circuit
that touches the DB only occasionally") describe the same thing, not two
separate tests -- a long-lived connection, opened once, touched only
occasionally. Implemented as one harness: a single `NpgsqlConnection` opened
once and held open for the whole run (never returned to the pool between
cycles), queried once per idle gap. This also meant `Connection Idle
Lifetime=0` (my first attempt, to stop Npgsql's own pool-pruning from
masking a real network reset) was pointless and actively wrong -- Npgsql's
idle-pruning only applies to connections sitting idle *in the pool* after
Close/Dispose, never to one the application is still holding open, and
Npgsql also flatly rejects `0` as a value (crashed on startup: "Connection
Idle Lifetime 00:00:00 under ConnectionPruningInterval 00:00:10"). Removed
the setting entirely once I understood why it didn't apply here.

Done:
- `tests/integration/postgres-soak/` (standalone console app, plain Npgsql,
  not part of the main solution -- it's Docker-only integration tooling, not
  a unit test `dotnet test`/`gate.sh` should ever run): opens one connection,
  idles, queries, repeats for N cycles; recovers with a fresh connection
  after a failed cycle so one broken cycle doesn't hide whether later ones
  also fail; prints a pass/fail summary and a matching exit code.
- `deploy/docker-compose.soak.yml`: postgres + the harness on one bridge
  network. Postgres has no `ports:` entry at all -- `expose` only -- so
  there is no published-port/NAT path anywhere in the test, which is the
  entire point.
- `scripts/soak-postgres-bridge.sh [idle-minutes] [cycles]`: one command,
  parameterised, builds+runs+tears down. Defaults match the page's spec
  (10 min x 3 cycles); sanity-checked first with `0.1 2` (passed, 0 errors)
  before committing to the full-length run.
- Official run (10 min x 3 cycles, ~30+ min) kicked off in the background
  with the owner's go-ahead; result below once it finishes.

### Official soak result

**PASSED.** 3 cycles x 10 minute idle gaps, bridge-only Postgres (no
published port anywhere in the path), zero connection errors, exit code 0:

```
postgres-soak: 3 cycle(s) x 10 minute(s) idle, connecting to host 'postgres'.
postgres-soak: opened the long-lived connection (Blazor-circuit-like -- held open for the whole run).
postgres-soak: initial query, before any idling -> 1
postgres-soak: cycle 1/3 -- idling 10 minute(s)...
postgres-soak: cycle 1 post-idle query -> 1
postgres-soak: cycle 2/3 -- idling 10 minute(s)...
postgres-soak: cycle 2 post-idle query -> 1
postgres-soak: cycle 3/3 -- idling 10 minute(s)...
postgres-soak: cycle 3 post-idle query -> 1
postgres-soak: PASSED -- zero connection errors across 3 cycle(s), idle gap 10 minute(s) each.
```

The production incident this guards against (a pooled connection silently
reset while idle, reached through Docker's published-port/NAT path) does
not reproduce over a bridge network with no published port in the path --
the new design's bridge topology is **not blocked** by this risk. No
fallback to host networking needed for Postgres<->dashboard. (A benign
Npgsql GSS-auth-library warning -- `Cannot load library libgssapi_krb5.so.2`
-- appears in the log; it's a fallback-path warning, not an error, and
every query still succeeded.)

## P02 -- Supervisor skeleton (image, spawn, boot replay)

First phase of the Python track. Layout matches the page's proposal:
`supervisor/supervisor.py` (entrypoint script) + `supervisor/supervisor/`
(the `process_manager.py`/`state.py`/`admin_api.py` package) +
`entrypoint.sh`/`Dockerfile`/`Dockerfile.test`/`tests/`.

Two real design/implementation issues surfaced while verifying the DoD by
hand against actual containers (not just unit tests) -- both fixed, not
just noted:

1. **The retry-cap logic as first written could never reach 3.** My initial
   `decide_boot_action` treated "last_run_outcome != healthy" as an
   unconditional refusal to replay on every future boot -- but that means
   the FIRST failed replay already blocks all later attempts, so
   `consecutive_failed_replays` could never climb past 1 across separate
   container restarts; the retry-cap check was dead code. Doc 5's
   "consecutive boot replays" turned out to mean retries *within one
   supervisor run* (give a transient failure a few automatic retries before
   giving up), not across separate container restarts. Fixed by moving the
   retry loop into `supervisor.py`'s boot sequence itself: the "only if
   last_run_outcome == healthy" gate decides whether to attempt a replay at
   all on a given boot; once attempting, the same argv is retried in-process
   up to `RETRY_CAP` (3) times before persisting a final non-healthy outcome
   and going idle. Verified end-to-end: `consecutive_failed_replays` reaches
   exactly 3 within one container run, and a subsequent restart correctly
   stays idle (the persisted backstop).
2. **The supervisor never reaped its own direct child.** Confirmed the
   DoD's actual "grandchild -> PID 1" scenario works correctly (tini/
   `docker-init` properly reaps an orphaned grandchild once its immediate
   parent is gone) -- but while building that test, found a zombie
   accumulating under the supervisor's own PID instead: `ProcessManager`
   called `subprocess.Popen` but never called `.wait()`/`.poll()` on it
   except during the boot health-check window, so a child that exited
   later (crashed, or just finished) sat as a zombie under the supervisor
   indefinitely. Fixed with one background daemon thread per spawn whose
   only job is the blocking `wait()` call (and `stop()` now polls rather
   than calling `wait()` itself, so the two never race on the same `Popen`).
   Re-verified by hand: kill the grandchild, kill the child, no zombie
   remains anywhere in the tree.

Also verified by hand (all via real `docker run`/`docker exec` against the
built `Dockerfile.test` image, not just unit tests): PID+argv visible via
`/admin/status`; state.json written on first boot with the `vllm` prefix
normalized (via the `vllm-shim.sh` that execs the fake, since there's no
real vLLM CLI in the test image); a second container, given a *different*
command, correctly ignores it and replays the persisted `last_argv`
instead; a hand-edited `last_run_outcome=failed` state.json produces an
idle boot; `settings.AUTO_START=off` produces an idle boot regardless of
outcome; the supervisor needs no DB env vars (true by construction -- the
code never references Postgres/a connection string at all).

Done:
- `supervisor/supervisor/state.py`: atomic `state.json` read/write
  (temp-file + `os.replace`), `normalize_argv` (prepends `vllm` only when
  the first element is literally `"serve"`), `decide_boot_action` (pure --
  the initial replay-or-not gate).
- `supervisor/supervisor/process_manager.py`: spawn in its own session/
  process group; a background reaper thread per spawn; `stop()` (SIGTERM
  the group, poll, SIGKILL if still alive).
- `supervisor/supervisor/admin_api.py`: `GET /admin/health` (no auth),
  stub `GET /admin/status` (pid/argv/started_at). Everything else
  (deploy/stop/restart/events/auth/GPU) is P03a+.
- `supervisor/supervisor.py`: ties the above together -- boot decision,
  retry loop, health check via stdlib `urllib` (zero added dependencies,
  per the page), then serves the admin API via uvicorn. `ADMIN_PORT`
  defaults to 8000 -- doc 5 says this port is genuinely TBD ("any free
  number inside the compose network"); picked a default rather than
  blocking on it, since the page doesn't ask this phase to settle it.
- `supervisor/Dockerfile` (derived image, `ARG BASE_IMAGE`) and
  `supervisor/Dockerfile.test` (python:slim + the fake vLLM + a `vllm` shim
  script, so CI doesn't need the real Intel/NVIDIA base image). `tini` as
  PID 1 relies on `init: true` (compose) / `--init` (docker run) rather
  than baking tini into the image -- P00's stock-image check (does the
  real base already ship tini) is still deferred to P16a prep, so baking
  it in now would mean guessing at an unknown base image's package manager.
- 19 pytest unit tests (`supervisor/tests/`) for the pure decision logic
  and process lifecycle; added as a `gate.sh` section (fast, no Docker).
- Added a `supervisor` service to `deploy/docker-compose.test.yml`
  (work item 4), spawning the fake vLLM as its own child -- separate from
  that compose's existing standalone `fake-vllm` service.

Out of scope, not touched: deploy API, the full state machine, auth, GPU,
log retention, event push -- all P03a onward.

## P03a -- Admin API + state machine (happy path, auth)

One state machine implementation (`deploy_runner.py`), two callers: the
boot-replay retry loop from P02 (now refactored to call it directly instead
of duplicating spawn/health-check logic) and the new `POST /admin/deploy`
HTTP handler -- same transitions, same event recording, whichever path
triggered it.

One real bug caught while writing the HTTP layer, before it ever ran: the
`/admin/deploy` handler must be `async def`, not a plain `def` -- FastAPI
runs sync routes in a worker thread, where `DeployRunner.start_deploy()`'s
internal `asyncio.create_task()` call would have no running event loop on
that thread to attach to. Caught by actually calling the endpoint in a
test, not by inspection.

One test-design dead end worth recording so it isn't retried: tried to
test the 409-on-concurrent-deploy behavior through TestClient with a real
slow deploy in flight across two separate `client.post()` calls. Empirically,
TestClient drives its event loop to completion per call, so a background
task scheduled during the first request had already finished (clearing the
in-flight id) before the second request ran -- the HTTP-layer test couldn't
observe the race at all. Real concurrency is exercised directly against
`DeployRunner` in `test_deploy_runner.py` (one asyncio event loop held open
for the whole scenario); the admin-API-level test was narrowed to just the
HTTP translation (`DeployInProgressError` -> 409 with the id in the body),
monkeypatching `start_deploy` to raise it directly.

Verified every DoD bullet two ways: 33 pytest tests (real subprocesses --
the actual fake vLLM test double, real HTTP health checks, no mocks for
the state machine itself) and by hand against the real `Dockerfile.test`
image via `docker run` + `curl` (deploy from idle, redeploy with the full
stopping/releasing_gpu sequence, env propagation, 409, `events?after=`,
401/200 auth, and `AUTO_START=off` surviving an actual container restart).

Done:
- `supervisor/supervisor/deploy_runner.py`: `DeployRunner` -- `start_deploy`
  (synchronous admission check + fire-and-forget background task, for the
  HTTP handler) and `deploy_and_wait` (same admission + state machine,
  awaited inline, for the boot sequence). States recorded as
  `{deployment_id, seq, ts, state, detail}`, `seq` per-deployment strictly
  increasing from 1, kept in memory (persistence is P05).
- `supervisor/supervisor/process_manager.py`: `spawn` now takes an optional
  `env` dict, merged over the container's own env (doc 5) -- needed for a
  recipe's `extra_env`.
- `supervisor/supervisor/admin_api.py`: bearer-token `Depends` on every
  `/admin/*` route except `/admin/health` (which stays token-free);
  `POST /admin/deploy`, `GET /admin/deployments/{id}/events?after=`,
  `GET`/`PUT /admin/settings`; `/admin/status` now reports
  `{state, child, vllm_healthy, exit_code, current_deployment_id}`.
- `supervisor.py`: boot sequence now calls `deploy_runner.deploy_and_wait`
  in its retry loop instead of duplicating spawn/health-check code.
- Added `GET /debug/env` to `tests/fake-vllm/fake_vllm.py` (P00's test
  double) so a deploy's `env` can be verified reaching the child without
  parsing log lines.
- 14 new pytest tests (`test_deploy_runner.py`, `test_admin_api.py`), on
  top of P02's 19 (33 total); `./scripts/gate.sh` still green (142 + 17 +
  6 + 33).

Out of scope, not touched: failure classification/reason codes (P03b --
a timed-out health check just records a plain `"failed"` event here),
event push to the dashboard (P03c -- events are polled, not pushed), real
GPU release checking (P04 -- still a fixed ~2s sleep), log retention (P05),
the smoke test (P06 -- the request body's `smoke` field is accepted and
ignored), cancel/stop/restart (P07).

## P03b -- Fail-fast + failure report

One real bug caught by the DoD's own "child killed" bullet before I'd have
otherwise noticed it: a deploy that failed via `health_timeout` (child
still alive, never got healthy) recorded the failure correctly but never
actually killed the still-running child -- `_run_deploy` just returned
`False` and left it running. Fixed: that branch now stops the child before
returning. The fail-fast branch (child already exited on its own) didn't
have this gap since there's nothing left to kill.

One test-harness lesson repeated from P03a, worth internalizing rather
than re-discovering again: a scenario spanning "deploy, then observe a
later async transition" (here, the post-ready crash watcher) must run
inside ONE `asyncio.run()`/event loop, not two separate calls -- the first
`asyncio.run()` tears down its loop (and anything still scheduled on it,
including a background task started during that call) the moment its own
coroutine returns. Wrapped the whole scenario in one `async def` instead.

Done:
- `supervisor/supervisor/failure_report.py`: `classify`/`build_report`,
  pure and unit-tested directly against the DoD's own classifier table
  (10 tests, `test_failure_report.py`).
- `process_manager.py`: now captures a bounded ~50-line tail of the
  child's combined stdout/stderr (still echoed to this container's own
  stdout line-by-line, so "forwarded to the container log" still holds),
  for the failure report's `log_tail`.
- `deploy_runner.py`: fail-fast (`_wait_for_health` now takes an
  `is_child_alive` callback and returns the moment it goes false, instead
  of sleeping out the rest of `health_timeout`); a `failed` transition
  always carries a `FailureReport`; a `_watch_for_crash` background task
  (started only after `ready`, cancelled at the start of the next deploy
  so an intentional stop is never misreported) distinguishes `crashed`
  (died after ready) from `failed` (never got there).
- `state.py`: `SupervisorState.last_failure_report` persists the report so
  `/admin/status` can show it after a restart into idle, from a brand new
  `DeployRunner` with no in-memory history at all.
- `supervisor.py`: boot-retry loop simplified -- `deploy_and_wait` now
  persists the failure outcome/report itself, so the loop just reloads
  state and adds its own retry-cap bookkeeping, and no longer needs (or
  makes) its own crashed-vs-failed guess.
- 15 new tests (5 integration-style in `test_deploy_runner.py`, 10 in
  `test_failure_report.py`, 1 in `test_admin_api.py`) -- 48 total in the
  supervisor project; `./scripts/gate.sh` still green (142 + 17 + 6 + 48).
- Verified by hand against the real `Dockerfile.test` image too (not just
  pytest): `--fake-crash` fails in ~1s with `process_exited`; a
  `--fake-slow-start` past a short `health_timeout` fails with
  `health_timeout` and the child is confirmed actually dead (`docker top`
  -- no zombie); `--fake-exit-after` past `ready` transitions to `crashed`;
  restarting the container after a crash comes up idle (P02 rule) and
  `/admin/status` still shows the pre-restart report. Commands in
  `plan/testing-reference.md`.

Noted, not fixed (out of scope for this phase, revisit if P07 touches
`/admin/status`'s `child` field): after `ProcessManager.stop()`, `/admin/
status`'s `child.pid`/`exit_code` still show the stopped child's stale
info instead of clearing -- `stop()` resets `self._process` but not
`self._info`. Harmless today (nothing currently reads that field expecting
it to reflect "no child" immediately after a stop -- the DoD only requires
the process to actually be dead, confirmed separately via `docker top`),
but worth a one-line fix whenever `/admin/status`'s `child` semantics are
revisited.

Out of scope, not touched: pushing the report to the dashboard (P03c),
storing it in the DB (P10b).

## P03c -- Event push (supervisor -> dashboard)

Hit the same class of bug a FOURTH time this build, finally fixed at the
root instead of patched around again: `supervisor.py` used to run the boot
sequence under its own `asyncio.run()` call and then hand off to the
synchronous `uvicorn.run()` for the server -- `asyncio.run()` tears down
its event loop (and anything still scheduled on it, e.g. the event
pusher's sender task, started during boot) the moment its own coroutine
returns. Restructured so the ENTIRE process -- boot sequence, the event
pusher's background task, and the admin API's server -- runs on one
event loop for its whole lifetime (`uvicorn.Config`/`uvicorn.Server(...).serve()`
awaited inside the same `main_async()` as the boot sequence, instead of
two separate run calls). Documented prominently in `supervisor.py`'s own
module docstring so this doesn't get re-introduced by a future edit; also
hit (and fixed the same way) twice more while writing this phase's own
tests, which is what made the pattern obvious enough to fix for good.

Done:
- `supervisor/supervisor/event_push.py`: `EventPusher` -- a bounded queue
  (default 1000), a background sender task with exponential backoff
  (1s/2s/4s/.../max 30s), `push()` itself synchronous/non-blocking. At
  capacity, evicts the oldest droppable ("log") item anywhere in the queue
  to make room; a state/report item is never dropped, even if that means
  growing past the nominal cap. Disabled cleanly (no queue growth, no
  task, no network calls at all) when `url` is falsy.
- `process_manager.py`: a second, separately-drained buffer of new output
  lines (`drain_pending_log_lines()`) alongside the existing bounded tail
  (P03b's `log_tail()`) -- the log-batch pusher consumes the former
  roughly once a second; the failure report still reads the latter.
- `deploy_runner.py`: every `_record()` (state transition) now also
  pushes a `kind: "state"` item with the same seq; `failed`/`crashed`
  additionally push a separate `kind: "report"` item (own, later seq);
  a log-batch task runs only while a deploy is actually in flight
  (started after spawn, cancelled when the deploy concludes). `seq` is
  shared across all three kinds per deployment, via one counter.
- `supervisor.py`: restructured as above; `DASHBOARD_EVENT_URL` wires an
  `EventPusher` into the one `DeployRunner` for the process's lifetime.
- 11 new tests (`test_event_push.py`, including a real local recording
  HTTP server standing in for the dashboard -- `tests/recording_stub.py`)
  -- 54 total in the supervisor project; `./scripts/gate.sh` still green
  (142 + 17 + 6 + 54).
- Verified by hand against the real `Dockerfile.test` image too: a real
  deploy's pushes landing on a real local HTTP stub over `--network host`,
  in order, with the right `Authorization` header, a log batch, and a
  final `ready` -- commands in `plan/testing-reference.md`.

Out of scope, not touched: the dashboard-side receiver (P10b), persisting
logs (P05 -- the log batches are pushed, but nothing on the supervisor
side stores them beyond the existing P03b tail/buffer).

## Deviations / findings worth flagging

- `tests/Dashboard.Web.Tests` currently has no test files (project exists,
  `dotnet test` reports "No test is available"). Not a P00 blocker -- the
  gate still exits 0 -- but noting it since the phase page assumes existing
  unit tests to classify, and this project contributes none yet.
