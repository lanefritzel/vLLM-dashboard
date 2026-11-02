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
| P04 | done | 2026-11-01 | See below. |
| P05 | done | 2026-11-01 | See below. |
| P06 | done | 2026-11-01 | See below. |
| P07 | done | 2026-11-01 | See below. |
| P08 | done | 2026-11-01 | See below. |
| P09b | done | 2026-11-01 | See below. |
| P10a | done | 2026-11-01 | See below. |
| P10b | done | 2026-11-01 | See below. |
| P11 | done | 2026-11-01 | See below. |

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

## P04 -- Hardware-agnostic GPU layer

The page's own open point -- "the exact xpu-smi/sysfs output format on
the owner's host isn't known to this kit: ask the owner for a sample" --
resolved without needing live host access at all. The owner pointed me at
a sibling project, `~/appdevelopment/application_development/intel-gpu-monitor`
(their own nvtop-like tool, already running on tes.home as the
`intel-gpu-monitor-web` container I'd seen earlier in `docker ps`). Its
`monitor/collectors/xe_sysfs.py` reads the Intel **`xe` kernel driver's**
sysfs VRAM counters directly -- `/sys/class/drm/card0/device/mem_info_vram_used`
/ `mem_info_vram_total` (raw bytes), with a PCI BAR-size fallback via the
`resource` file when `mem_info_vram_total` reads 0. **Not `xpu-smi`** --
the real mechanism on this exact hardware turned out to be plain sysfs.
Built the Intel backend directly against this confirmed format; no
"needs real-host confirmation" flag needed, since this came from code
already proven against the real box, not a guess.

Done:
- `supervisor/supervisor/gpu_backend.py`: `parse_nvidia_smi`/`parse_xe_sysfs`
  (pure, take command/file output, never raise -- garbage degrades to
  `None`/generic), `NvidiaBackend`/`IntelSysfsBackend`/`GpuBackend` (the
  base class doubles as the "generic" no-op backend)/`FakeBackend`
  (`GPU_BACKEND=fake`, reads `{vendor,count,total_mb,used_mb}` from a
  JSON file so a test can change `used_mb` between polls), `detect_backend()`
  (nvidia-smi on PATH -> Intel sysfs path exists -> generic).
- `deploy_runner.py`: `releasing_gpu` no longer a blind 2s sleep -- waits
  2s, then polls VRAM up to ~10s total; "released" = dropped to <=10% of
  its pre-stop level or under 100MB outright (a judgment call -- doc 5
  doesn't give an exact threshold). Still elevated at ~10s -> proceeds
  anyway and flags `vram_still_in_use` on the next failure report, if
  there is one. No backend/no baseline -> the same ~2s wait as before,
  unchanged behavior for every existing test and the real container
  (confirmed by hand: redeploy's `releasing_gpu` gap stayed ~2.0s with
  the generic backend).
- `admin_api.py`: `GET /admin/gpu`, `POST /admin/gpu/recheck` (both just
  `gpu_backend.query().to_dict()` -- a recheck IS a fresh query, nothing
  else to do at this phase).
- `supervisor.py`: detects the backend once at start, logs which vendor.
- 15 new tests (10 parser/backend unit tests exactly matching the DoD's
  own classifier-table bullet, 5 integration-style against the fake
  backend's actual wait/poll timing) -- 69 total; `./scripts/gate.sh`
  still green (142 + 17 + 6 + 69). Also verified by hand against the
  real `Dockerfile.test` image: `/admin/gpu` reports `generic` cleanly (no
  GPU tools in that image), and a redeploy still reaches `ready` with the
  same ~2s `releasing_gpu` gap as before this phase.

Out of scope, not touched: Stop's verified-release reporting (P07 -- this
phase only threads the `vram_still_in_use` flag into the failure report,
doesn't surface a dedicated "VRAM released: X/Y GB" UI line), multi-GPU
selection.

## P05 -- Logs (ring buffer + retention)

Found a real bug by hand against a real container, not caught by pytest
initially: a FAST deploy (ready inside ~1s, which the fake vLLM always
is) could conclude and cancel the log-batch task before that task's own
`sleep(1)` ever elapsed -- `GET /admin/deployments/{id}/logs` came back
with only 1 of the 3 lines `GET /admin/logs` (the ring buffer) had for
the exact same deploy. Fixed with one final synchronous drain-and-archive
pass after the log-batch task is cancelled AND awaited (so it can't race
the final drain), in `_run_deploy`'s `finally` block. Added a regression
test for the fast-deploy case specifically (the existing archive test
used `--fake-slow-start 2`, slow enough to never hit this).

Done:
- `supervisor/supervisor/log_store.py`: `RingBuffer` (bounded by bytes,
  ~10MB default, not line count) and `DeploymentArchive` (one capped
  ~10MB log file + one persisted `.jsonl` event stream per deployment,
  under `/state/logs/` and `/state/events/`; a small index file drives
  rotation -- registering a 51st deployment evicts the oldest).
- `process_manager.py`: the old fixed-50-line `_log_tail` deque is gone
  -- replaced by the new `RingBuffer` as the single source of truth for
  "recent output of the current child". The failure report (P03b) and
  `GET /admin/logs` (P05) both just ask it for different tail lengths
  now, instead of keeping two separate buffers.
- `deploy_runner.py`: registers each new deployment with the archive
  (rotating off the oldest past 50), persists every event to it
  alongside the existing in-memory copy, and `events_for()` falls back to
  loading from the archive when a deployment isn't in this process's own
  memory (a past deployment from before a restart). The log-batch task
  now also writes drained lines into the per-deployment log file, with
  the final-drain fix above.
- `admin_api.py`: `GET /admin/logs?lines=N` (ring buffer, default 200),
  `GET /admin/deployments/{id}/logs?lines=N` (that deployment's archived
  log).
- 18 new tests (`test_log_store.py` for the pure ring-buffer/archive/
  rotation logic, plus integration-style additions to
  `test_deploy_runner.py`/`test_admin_api.py`) -- 80 total;
  `./scripts/gate.sh` still green (142 + 17 + 6 + 80). Also verified by
  hand against the real `Dockerfile.test` image: a real container
  restart (`docker rm` + fresh `docker run` on the same volume, not just
  a fresh in-process object) still serves the right `events?after=<seq>`
  for a deployment from before the restart; confirmed `/admin/logs` and
  `/admin/deployments/{id}/logs` now match exactly for the same fast
  deploy, post-fix.

Out of scope, not touched: shipping logs to the dashboard DB (P10b --
only the 50-line failure `log_tail` goes there, unchanged from P03b), log
search/filter.

## P06 -- Smoke test (auto + on demand)

Two decisions confirmed with the owner before implementing (2026-11-01):
- A **failed** smoke test is **warn-and-ready**, not a deploy failure: the
  deploy still ends in `ready`, with `smoke_ok=false` and a `smoke` event
  carrying `{"outcome":"fail","excerpt":...}` for the UI to act on (e.g.
  offer Rollback) -- never used to tear down a model that's actually
  serving, just because it gave a bad answer to a canned prompt.
- Smoke is **automatic by default**: an omitted or explicit `"smoke": null`
  field on `POST /admin/deploy` still runs it with the canned defaults;
  only an explicit `"smoke": {"skip": true}` turns it off. (The page's own
  wording was ambiguous here -- "smoke: null ... skips" vs. "automatic by
  default" -- and a JSON field that's omitted vs. explicitly null is
  indistinguishable once past pydantic, so "automatic by default" wins.)

Found a real bug by hand against a real container, not caught by the unit
tests at first: `_record()` (the existing helper that both appends an
event and sets `self._state`) was reused as-is for the new `smoke` event,
which meant a *passing* smoke test silently changed `/admin/status`'s
`"state"` from `"ready"` to `"smoke"` -- the deploy's own terminal state,
not just an event. None of the existing unit tests happened to assert
`runner.state` immediately after a *successful* deploy with smoke enabled
(they checked the event list, not the live state), so this only surfaced
curling `/admin/status` against a real deploy. Fixed by restoring
`self._state = "ready"` right after recording the `smoke` event -- `smoke`
is an event, never a state-machine transition. Added a regression
assertion (`runner.state == "ready"` after both a passing and a failing
smoke run) and fixed several pre-existing tests that asserted an exact
events list without anticipating the now-default `smoke` event appended
after `ready`.

Done:
- `supervisor/supervisor/smoke_test.py` (new): `run_smoke_test()` sends the
  canned prompt (`"Reply with OK."`, 50 max tokens) straight to the
  child's own `/v1/chat/completions` -- never through a proxy -- and passes
  iff `"OK"` appears in the reply; never raises (a request failure or
  timeout is just a `fail` result with the error as the excerpt).
  `resolve_smoke_config()` implements the owner-confirmed default/skip
  rule above. `_extract_text()` tolerates both a chat-shaped and a
  completions-shaped response body.
- `deploy_runner.py`: `_run_deploy` runs the smoke test as the last step
  after a deploy reaches `ready` (unless skipped), records a `smoke` event
  with the outcome, and restores `self._state = "ready"` afterward (the
  bug above). New `NotServingError` (raised by `run_smoke_test_now()` when
  not actually serving) and `last_smoke_ok` property (`None` until the
  first smoke test runs, or forever if every deploy so far skipped it).
  `start_deploy`/`deploy_and_wait` both take an optional `smoke: dict`.
- `admin_api.py`: `POST /admin/deploy`'s `smoke` field is now wired through
  (previously accepted-but-ignored, a P03a placeholder); new `POST
  /admin/smoke-test` for on-demand runs against whatever is currently
  serving, 409 + `{"state": ...}` when not; `smoke_ok` added to `GET
  /admin/status`.
- 14 new tests: `test_smoke_test.py` (pure config resolution + a real
  fake-vllm subprocess for pass/fail/unreachable), extensions to
  `test_deploy_runner.py` (auto-pass, auto-fail-is-warn-and-ready,
  explicit skip, on-demand while serving, on-demand `NotServingError`
  while idle) and `test_admin_api.py` (`POST /admin/smoke-test` 200 vs
  409, `smoke_ok` in status). 94 total; `./scripts/gate.sh` green.
  Verified by hand against the real `Dockerfile.test` image: default
  smoke pass (`state:"ready"`, `smoke_ok:true`), `--fake-bad-output` ->
  warn-and-ready (`state:"ready"`, `smoke_ok:false`, child still running),
  explicit `skip:true` -> `smoke_ok:null`, on-demand while serving (200)
  and while idle (409 + `{"state":"idle"}`), and the `smoke` event's shape
  in `GET /admin/deployments/{id}/events`.

### DoD self-check

| DoD bullet | Met? | Evidence |
|---|---|---|
| Canned prompt sent straight to vLLM's own chat/completions, never through a proxy | Yes | `smoke_test.py`'s `run_smoke_test()` hits `http://127.0.0.1:<port>/v1/chat/completions` directly; the supervisor has no proxy of its own |
| Pass iff the expected substring appears in the reply (first ~50 tokens) | Yes | `expect_substring in text`, `max_tokens=50`; unit-tested against real fake-vllm for both a good and a `--fake-bad-output` reply |
| Runs automatically after every successful deploy by default | Yes | `resolve_smoke_config(None)` and omitted-field both run with defaults; verified via curl with the field omitted entirely |
| Only an explicit `smoke: {skip: true}` turns it off | Yes | `resolve_smoke_config({"skip": true})` returns `None`; verified via curl, `smoke_ok` stayed `null` |
| A failed smoke test is warn-and-ready, not a deploy failure | Yes | deploy returns `True`/`ready` either way; real-container check showed `state:"ready"`, child still running, after `--fake-bad-output` |
| `smoke_ok` reflected on `/admin/status` | Yes | field added, exercised by both the passing and failing real-container runs above |
| On-demand smoke test available (`POST /admin/smoke-test`) | Yes | implemented, 200 + result body while serving (curl + test) |
| On-demand smoke test rejected when nothing is serving | Yes | `NotServingError` -> 409 + `{"state": ...}`; verified idle (curl + test) |
| Smoke result recorded as an event for the deployment | Yes | `smoke` event with `{"outcome": "pass"\|"fail", ...}`, visible via `GET .../events`; confirmed it does NOT change `/admin/status`'s `"state"` (the bug this phase caught) |

Out of scope, not touched: streaming smoke requests (always `stream:
false`), any UI for displaying `smoke_ok` or offering Rollback (dashboard
side, later phase), configurable default prompt/timeout via env var (only
per-request override via the `smoke` body field, per the page).

## P07 -- Cancel + Stop

One decision confirmed with the owner before implementing (2026-11-01): a
manual Stop **survives a container reboot** -- Stop sets
`last_run_outcome="stopped"`, so P02's boot rule (replay only when the
last run was `healthy`) leaves the instance idle after a reboot;
`last_good_argv` is kept, so "redeploy last working" still works. The
alternative (forgotten on reboot) would have needed a new flag distinct
from `last_run_outcome`.

**Cancel/stop with nothing to act on** (the page's own open question,
"document which"): **Cancel is 409** (it names a specific deployment id,
so with none in flight that's a genuine caller error) -- **Stop is
idempotent 200** (it names no id; "already idle" is just success, not an
error). Restart follows Cancel's rule (409, via the same `NotServingError`
`/admin/smoke-test` already uses) since it also needs something
concrete -- the currently-serving argv -- to act on.

Found and fixed a real bug by hand, not caught by the first pass of unit
tests: cancelling a deploy's `asyncio.Task` **before it has run a single
line never executes the coroutine's body at all** -- not even its own
`try/except/finally`. A test that called `cancel_deploy()` immediately
after `start_deploy()`, with no intervening `await`, left
`current_deployment_id` stuck set forever (the lock never released) and
recorded no `cancelled` event, because `_run_deploy`'s own cleanup code
never ran. Fixed by making `cancel_deploy()` itself the authoritative
cleanup point: after awaiting the (possibly-never-started) task, if the
lock is still held for that same deployment id, it means the task's body
never ran, so `cancel_deploy()` does the same phase-appropriate cleanup
itself instead of trusting the task to have done it. This only showed up
because the test issued no `await` between `start_deploy()` and
`cancel_deploy()` -- a scenario curl/HTTP can't easily hit (network
round-trips always give the task time to start), but a legitimate race all
the same.

Also added a `cancel_event` (a plain `threading.Event`, not asyncio) so
Cancel stays responsive even while blocked on a long `health_timeout`
(default 600s): an asyncio `CancelledError` can only be *delivered* once
the `to_thread()` call it's suspended on actually returns -- a blocking
call can't be interrupted mid-flight -- so without this, cancelling during
`waiting_health` could silently wait out the full timeout instead of
responding in about a second.

Done:
- `deploy_runner.py`: phase detection for Cancel reads **live process
  state**, not a flag set earlier in the function -- "phase 1" is exactly
  "a child is running and it is NOT the argv of the deploy being
  cancelled" (old model untouched); anything else is "phase 2" (kill
  whatever's there, if anything, go idle). This is deliberately immune to
  the asyncio-cancellation-timing race described in P03c's history: a flag
  set only *after* an await can't be trusted, since the await might not
  return until the awaited work has already finished. New `stop()`
  (SIGTERM/wait/SIGKILL via `ProcessManager.stop`, then the same
  GPU-release wait a swap uses, returned as `{released, used_mb,
  total_mb}` rather than just a bool -- `_wait_for_gpu_release`'s old
  logic was refactored onto a shared `_poll_gpu_release` helper that
  returns the full reading, with its own behavior otherwise unchanged).
  New `restart()` (stop + start the current argv **and env** -- added
  `_current_env` tracking since `ProcessManager.ChildInfo` never carried
  it and "no config change" needs it too). New exception
  `NoDeployInProgressError` (Cancel, 409); `NotServingError` (added in
  P06) reused for Restart.
- `admin_api.py`: `POST /admin/deployments/{id}/cancel`, `POST
  /admin/stop`, `POST /admin/restart`.
- 20 new tests (`test_cancel_stop_restart.py`, a new file -- phase
  1/phase 2/responsiveness/idempotency/conflicts, all against real
  fake-vllm subprocesses; plus HTTP-layer additions to
  `test_admin_api.py`). 130 total; `./scripts/gate.sh` green. Verified by
  hand against the real `Dockerfile.test` image, including a volume-backed
  restart to confirm Stop survives a reboot: Cancel phase 2 (mid-load,
  `--fake-slow-start`) killed the loading child and went idle with the
  full event sequence ending in `cancelled`/`post_stop`; Stop while idle
  was idempotent (200, `released:true`); Stop while serving released the
  child and persisted `last_run_outcome:"stopped"` to `/state/state.json`;
  a fresh container on the **same volume** booted with `boot decision --
  replay=False (last run outcome was 'stopped', not healthy -- staying
  idle)`, and `last_good_argv` was still in the saved state; Restart got a
  new PID with the same argv and reached `ready` (smoke included); Stop
  and Restart both 409'd against an in-flight deploy; Restart 409'd while
  idle; Cancel 409'd with nothing in flight.
  **Note:** curling Cancel over real HTTP always landed in phase 2, never
  phase 1 -- the network round-trip between "start the deploy" and "cancel
  it" is enough time for the deploy task to reach and complete the actual
  stop of the old process. Phase 1 (old model provably untouched) is only
  deterministically exercisable in-process, with zero `await` between
  `start_deploy()` and `cancel_deploy()` in the same event loop tick --
  which is exactly what `test_cancel_before_the_new_deploy_runs_at_all_...`
  does, and exactly the scenario that surfaced the never-started-task bug
  above.

Out of scope, not touched: UI labels/buttons (P14), dashboard-side
`stop_reason` writing (P10a/P10b).

### DoD self-check

| DoD bullet | Met? | Evidence |
|---|---|---|
| Cancel phase 1 → old model untouched, deploy aborted, state back to ready | Yes | `test_cancel_before_the_new_deploy_runs_at_all_leaves_old_model_untouched` (deterministic, in-process) |
| Cancel phase 2 (`--fake-slow-start`) → loading child killed, state idle, no child running, nothing restarted afterwards | Yes | `test_cancel_while_the_new_child_is_loading_kills_it_and_goes_idle` + real-container check above |
| Stop on generic host → `released:true` via "assume released"; fake backend holding VRAM → `released:false` with numbers, recheck flips once cleared | Yes | `test_stop_kills_the_child_...` (generic) + `test_stop_reports_vram_still_held_then_recheck_flips_once_cleared` (fake backend) |
| Restart → same argv, new PID, back to ready | Yes | `test_restart_gets_a_new_pid_with_the_same_argv_and_reaches_ready` + real-container check |
| After manual Stop, restarting the container comes up idle; `last_good_argv` still present | Yes | real-container volume-backed reboot check above |
| Cancel/stop with nothing to act on → clear 409/idempotent response, documented which | Yes | Cancel 409 (`NoDeployInProgressError`), Stop idempotent 200 -- both decided and written up above |

## P08 -- Model downloads

Mid-phase, confirmed with the owner whether to actually exercise the real
Hugging Face network path (not just the stubbed one the page calls for)
during manual verification: **yes**, using a small real repo
(`nomic-ai/nomic-embed-text-v1.5`) rather than downloading its full
~2.5GB of safetensors/onnx variants, a single tiny file
(`1_Pooling/config.json`, 286 bytes) was downloaded via the `filename`
parameter to prove the real `HfClient`'s `list_files`/`get_config`/
`download_file` methods actually talk to Hugging Face correctly --
the model was deleted again immediately after (owner's explicit
instruction) via `DELETE /admin/downloads/{id}`.

That same manual check also surfaced a real gap: `huggingface_hub` was
NOT installed in the test container at all (`Dockerfile.test`'s
`requirements.txt` only had fastapi/uvicorn), so the real `HfClient`
would have failed on import inside a real deploy too. The original design
relied on an unverified assumption -- "the real vLLM base image already
has `huggingface_hub`, since vllm itself depends on it" -- deliberately
avoiding adding it as an explicit dependency, matching doc 5's "zero
extra deps" framing for fastapi/uvicorn. Asked the owner whether to keep
relying on that assumption or add it explicitly; **chose explicit**:
added `huggingface_hub>=0.26,<0.27` to `supervisor/requirements.txt`
(already installed into `Dockerfile.test` via its existing
`pip install -r requirements.txt` step) and added a new
`pip install -r requirements.txt` step to the **real** `supervisor/Dockerfile`,
which previously had no install step at all (it relied entirely on the
base image already having fastapi/uvicorn -- true, but huggingface_hub
wasn't a safe bet to assume the same way, so this phase closes that gap
for both deps going forward, not just the new one).

Design notes not spelled out verbatim on the page, decided while
implementing:
- **Cancel/stop with nothing to act on** isn't literally repeated here,
  but the same "document which" instinct applies to inspect's failure
  mapping: a repo-not-found/gated/unreachable `HfRepoError` from
  `POST /admin/downloads/inspect` maps to `exc.status` (404/401) when
  known, else 502 (upstream failure) -- the page only specifies these
  codes for the async DOWNLOAD's own `download_failed` status, not
  inspect's synchronous call, so this is a judgment call, not a literal
  spec requirement.
- **Resume** is implemented as "does a file of the expected size already
  exist at the destination" -- backend-agnostic (works identically for
  the real client and the fake one used in tests) rather than relying on
  huggingface_hub's own internal `.incomplete`/etag bookkeeping, which
  isn't something a stubbed test double can exercise anyway.
- The real `HfClient.download_file` only reports progress as one
  lump-sum call per file (huggingface_hub doesn't expose a byte-level
  download callback) -- fine-grained mid-file `progress_pct` is only
  demonstrated against `FakeHfClient` in tests, which does chunk its
  copies. Noted as a real-vs-fake granularity gap, not a bug.
- `load_format` is `"gguf"` for a single named `.gguf` file, `None`
  (not a string) for the safetensors/snapshot case -- the page only
  names a value for the GGUF path.

Done:
- `supervisor/supervisor/downloads.py` (new): `HfClient` (real, wraps
  `huggingface_hub`, lazily imported inside each method so the module
  still loads in an environment without it -- no longer strictly
  necessary now that it's an explicit dependency, but kept since a lazy
  import costs nothing and keeps `FakeHfClient`-only test runs fast to
  collect); `FakeHfClient` (serves a local directory as a repo, with
  three reserved repo ids for the 401/404/unreachable failure paths, used
  by every test); `DownloadManager` (one `threading.Thread` per download,
  the page's own "start in a background thread" -- no asyncio involved,
  unlike the deploy state machine, since nothing here needs to interact
  with an event loop); `HfRepoError`, `DownloadInProgressError`,
  `DownloadNotFoundError`.
- `admin_api.py`: `POST /admin/downloads/inspect`, `POST
  /admin/downloads`, `GET /admin/downloads/{id}`, `DELETE
  /admin/downloads/{id}` -- all plain sync `def` routes (no `async def`
  needed here, since `DownloadManager` doesn't use `asyncio.create_task`
  the way the deploy state machine does).
- `supervisor.py`: wires a real `DownloadManager(HfClient(token=HF_TOKEN),
  models_root=MODELS_ROOT)` at startup; `HF_TOKEN`/`MODELS_ROOT` read
  from the environment only.
- `src/Dashboard.Core/Downloads/DiskGuard.cs` (new, .NET side): pure
  `DiskGuard.Check(requiredBytes, availableBytes)` -> `{Ok, Message}`.
- 14 new Python tests (`test_downloads.py`, all against `FakeHfClient`,
  no network) + 5 HTTP-layer additions to `test_admin_api.py` + 4 new
  .NET tests (`DiskGuardTests.cs`). 125 Python + 150 .NET total;
  `./scripts/gate.sh` green. Verified by hand against the real
  `Dockerfile.test` image, including the real-network check described
  above.

Out of scope, not touched: the Add-model UI (P13), a download queue
(v2 per the page), writing the `models` row (P09b).

**Needs real-hardware confirmation** (per the Start Here's running list):
whether the actual production vLLM base image
(`intel/llm-scaler-vllm:0.21.0-b3`) already satisfies the
`huggingface_hub>=0.26,<0.27` pin on its own -- moot for correctness now
that it's installed explicitly either way, but worth knowing before P16a
to avoid a surprise version conflict with whatever vllm itself pins.

### DoD self-check

| DoD bullet | Met? | Evidence |
|---|---|---|
| Integration (stubbed HF repo, tiny files): inspect returns files + config + `free_bytes`; a download shows monotonic `progress_pct`, `files_done/total`, an `eta_seconds`, reaches `done`, files land at `/models/hf-cache/<slug>` | Yes | `test_inspect_returns_files_config_and_free_bytes`, `test_download_reaches_done_with_monotonic_progress_and_lands_at_the_right_path` |
| Second concurrent download -> 409 | Yes | `test_second_concurrent_download_gets_409` + `test_downloads_endpoints_round_trip_through_the_admin_api` |
| `DELETE` mid-download -> partial directory removed | Yes | `test_delete_mid_download_removes_the_partial_directory` |
| Unreachable/401/404 repo -> `download_failed` with a readable message; retry resumes (no re-download of finished files) | Yes | `test_unreachable_repo_download_fails_with_readable_message`, `test_inspect_gated_repo_raises_401`, `test_inspect_unknown_repo_raises_404`, `test_retry_after_failure_resumes_without_redownloading_finished_files` |
| Stub free space below required -> `DiskGuard.Check` blocks with the required-vs-available message (unit) | Yes | `DiskGuardTests.Check_AvailableBelowRequired_IsBlockedWithRequiredAndAvailableInMessage` |
| `HF_TOKEN` read from env only (grep: no token in any response or log line) | Yes | `test_hf_token_never_appears_in_any_response` (asserts absence from both JSON responses and captured stdout/stderr); real container test used a real token-less call (public repo) so no token was ever in play there either |

## P09b -- Save-time validation + domain CRUD

Confirmed the dtype rule with the owner before building, per the page's own
explicit "owner to confirm" instruction: bfloat16 hard-blocked on Intel,
auto/unset soft-warns on Intel, float16 passes, no issue at all on
non-Intel -- exactly as stated on the page, no changes.

Design decision made while implementing, not spelled out verbatim on the
page: **a `Recipe` is model-agnostic** (it's applied to different models
at different deploy times), so `CompatibilityPass.Check(model, recipe,
server)` -- P09a's existing pure validator, which this phase extends --
was split into `CheckRecipe(recipe, server)` (the model-INDEPENDENT rules:
dtype, `max_model_len > 0`, `gpu_memory_utilization` range,
`served_model_name` identifier, tensor-parallel-vs-GPU-count) and `Check`
(adds the model-DEPENDENT rules: path/availability/sizing) which calls
`CheckRecipe` internally. `RecipeService.CreateAsync`/`UpdateAsync` enforce
only the model-independent subset at save time (throwing on any hard
block -- this is the "hard-blocked" the DoD's unit tests exercise); the
full model-aware `Check` is exposed as a separate, non-throwing
`ValidateAsync(recipeId, modelId)` for deploy pre-flight (P10a) and the UI,
and for re-checking an existing/legacy-shaped row without erroring (doc 13
gap 12) -- this is why a recipe can be *saved* validly before any model is
ever chosen for it, while a *deploy* still gets the full model-aware check.

Blank-field guardrails (the literal "first qwen incident" family: an empty
model path, and the equivalent for each entity's own identifying field --
`Server.Label`, `Instance.Label`, `Model.Name`/`Path`, `Recipe.Name`) live
directly in each service's own `ValidateForSave`, following the existing
legacy `ModelCatalog.ValidateForSave` pattern exactly (throws
`ArgumentException`, checked before any DB access) -- not through
`CompatibilityPass`, since the pure `Recipe`/`RecipeServer` input records
P09a defined don't carry a name/label field at all (by design, ArgBuilder
never needed one).

Done:
- `src/Dashboard.Core/Recipes/RecipeArgsInput.cs`: added `RecipeServer.GpuName`
  (nullable) -- needed to decide the dtype rule's "Intel stack" check.
- `src/Dashboard.Core/Recipes/CompatibilityPass.cs`: new `CheckRecipe` (see
  above) with the dtype rule, `max_model_len > 0`, `gpu_memory_utilization`
  in (0,1], `served_model_name` valid-identifier, plus a new model-dependent
  `model_path_outside_hf_cache` check added to `Check` (a model's path must
  live under its server's `models_dir/hf-cache/`, matching P08's "one
  scheme everywhere" convention). 20 new unit tests in
  `CompatibilityPassTests.cs` (the existing fixture's model path was moved
  under `hf-cache/` to match the new rule -- 2 pre-existing tests had
  started failing against it, fixed by updating the shared fixture, not
  the rule).
- `src/Dashboard.Infrastructure/Persistence/V2/`: `ServerService`,
  `InstanceService`, `ModelService`, `RecipeService`, `DeploymentService`
  (new files) -- CRUD for all five new-schema entities, each following the
  existing `ModelCatalog`'s singleton + `IServiceScopeFactory`-per-operation
  pattern. `RecipeService` additionally has `DuplicateAsync` ("name-copy",
  auto-numbered on a second collision), `CreateFromModelAsync` (prefills
  `max_model_len` from the model's own `config.json` `max_position_embeddings`
  when readable, else the schema default; `gpu_memory_utilization = 0.90`),
  and the non-throwing `ValidateAsync` described above.
  `DeploymentService.TransitionAsync` only ever touches status/stop_reason/
  failure/exit_code/log_tail/started_at/stopped_at -- never `ArgsSnapshot`
  (immutable at runtime, enforced simply by the method never accepting or
  writing it). `PostgresErrors.IsUniqueViolation` is a small shared helper
  so every service turns a raw 23505 into the same kind of friendly
  message (recipe-name-per-server, proxy-slug, and the deployments partial
  unique index all go through it) instead of a bare constraint name
  leaking to a caller.
- `src/Dashboard.Core/Abstractions/ISupervisorTokenProvider.cs` +
  `src/Dashboard.Infrastructure/Persistence/V2/ConfigSupervisorTokenProvider.cs`:
  the stub the page asks for -- one token for every instance, read from
  configuration (`Supervisor:Token`), not the DB; real per-instance storage
  is P15b's own open decision.
- `src/Dashboard.Web/Program.cs`: all five services + the token provider
  registered for DI (singleton, matching the legacy catalogs above them).
- 11 new unit tests (`V2ServiceValidationTests.cs`, in
  `Dashboard.Core.Tests` even though the services live in
  `Dashboard.Infrastructure` -- true unit tests: each service's
  `ValidateForSave` throws before ever touching its `IServiceScopeFactory`,
  so passing a deliberately-unusable one keeps these DB-free) + 16 new
  Postgres integration tests (`V2ServiceTests.cs`) covering CRUD
  round-trips for all five entities, `args_snapshot` immutability across
  transitions, the three friendly-error cases (concurrent swap, duplicate
  recipe name, duplicate proxy slug), recipe hard-block enforcement at
  save time, duplicate/create-from-model, and validating the seed
  fixture's own legacy-shaped model (whose path isn't under `hf-cache/`)
  without throwing. 177 + 33 .NET tests total (was 146 + 17);
  `./scripts/gate.sh` green.
- No real-container manual verification this phase -- there's no new
  runtime/HTTP surface (the page itself only asks for a service layer, "+
  thin API/controllers if the project style uses them," and this
  codebase's style is Blazor components calling services directly via DI,
  not separate REST controllers for catalog data). The automated
  unit + real-Postgres-integration coverage above is the verification.

Two found-and-fixed test bugs worth flagging for future Postgres jsonb
work: (1) a `jsonb` column round-trips through its own canonical
whitespace (e.g. a space after each comma) -- comparing a stored jsonb
string against the original literal by exact text equality is wrong;
compare parsed. (2) a `jsonb` column rejects malformed JSON at the DB
level before any C# code ever sees it -- "config unreadable" can't be
simulated by writing garbage into a jsonb column directly; the realistic
unreadable-equivalent shapes at this layer are "no config" or "valid JSON
missing the expected field" (P08's own "config unreadable" is a check
against the raw HF repo file, a different layer, before anything is ever
stored).

Out of scope, not touched: calling the supervisor (P10a). UI (P13).

### DoD self-check

| DoD bullet | Met? | Evidence |
|---|---|---|
| Unit (incident reproductions): blank server/name/path -> rejected | Yes | `V2ServiceValidationTests`: `ServerService_CreateWithBlankLabel_Rejected`, `ModelService_CreateWithBlankPath_Rejected`, `ModelService_CreateWithBlankName_Rejected`, `InstanceService_CreateWithBlankLabel_Rejected` |
| Unit: `dtype=bfloat16` on Intel -> hard-blocked; `auto` on Intel -> warning; `float16` -> passes; same recipe on non-Intel -> no dtype issue | Yes | `CompatibilityPassTests`: `Check_DtypeBfloat16OnIntelServer_IsAHardBlock`, `Check_DtypeAutoOrUnsetOnIntelServer_IsASoftWarningNotAHardBlock`, `Check_DtypeFloat16OnIntelServer_Passes`, `Check_DtypeBfloat16OnNonIntelServer_HasNoDtypeIssue` |
| Unit: field-rule violations rejected with clear messages; duplicate recipe name rejected | Yes | `CompatibilityPassTests`' field-rule tests (max_model_len, gpu_memory_utilization, served_model_name, slug) + `V2ServiceTests.RecipeService_DuplicateNameOnSameServer_SurfacesAsFriendlyError` |
| Integration (Postgres): CRUD round-trips for all five entities | Yes | `V2ServiceTests`: one `*_CrudRoundTrips` test per entity |
| Integration: creating a deployment stores `args_snapshot` exactly as given and refuses later edits to it | Yes | `DeploymentService_ArgsSnapshotIsFrozenAtCreateTime_TransitionNeverTouchesIt` (compared parsed, since jsonb reformats whitespace on storage) |
| Integration: the partial unique index surfaces as a friendly "a swap is already in progress" error | Yes | `DeploymentService_SecondConcurrentSwap_SurfacesAsFriendlyError` |
| Validating a converted/legacy-shaped recipe returns issues without throwing | Yes | `RecipeService_ValidateAsync_OnALegacyShapedRow_ReturnsIssuesWithoutThrowing`, against the P01a seed fixture's own non-hf-cache model path |

## P10a -- Swap orchestration (dispatch, gates)

Hit a real schema conflict partway through and confirmed the fix with the
owner before proceeding: P01a's partial unique index on `deployments` was
`UNIQUE(instance_id) WHERE status IN ('pending','active')` -- literally at
most one row, pending OR active, per instance, ever. But this phase's own
flow requires a NEW pending row to coexist with the PREVIOUS active row
until the new one reaches ready (when they flip together, in one
transaction) -- those two rows can never coexist under the index as
written; every real swap attempt hit a 23505 unique-violation inserting
the second row. **Fixed by narrowing the index to `WHERE status =
'pending'` only** (owner-confirmed 2026-11-01) via a new migration
(`NarrowDeploymentsPendingIndex`, index renamed `one_pending_per_instance`
since "one active" was no longer what it enforced) -- it still makes a
second CONCURRENT SWAP ATTEMPT impossible at the DB level (two 'pending'
rows), which was the actual intent; it just no longer also forbids a
pending row from existing alongside the active row it's about to replace.
Updated the one pre-existing test that depended on the old (active +
pending)-conflicts behavior, and added a new one asserting they now
explicitly coexist without error.

Found and fixed a real bug in my own first pass at `RollbackAsync`, caught
by the real integration test (not reasoned out in advance): it replayed
the CURRENTLY ACTIVE deployment's own `args_snapshot` -- a no-op replay of
whatever's already running, not an actual rollback. "The previous
deployment" (doc 7) means the row the current active one itself replaced
-- fixed to walk this instance's deployment history for the most
recently stopped row (excluding the current active one) and replay THAT
row's `args_snapshot`/model/recipe ids instead.

Also fixed a real P07 gap this phase's own design exposed: `/admin/restart`
didn't accept a caller-supplied `deployment_id`, but correlating the
dashboard's own Postgres row id with the supervisor's event stream (the
same convention `/admin/deploy` already uses) needs exactly that for a
Restart. Added an optional `deployment_id` to both
`DeployRunner.restart()` and the `/admin/restart` request body (backward
compatible -- omitted, one is generated the same way as before).

And a third real gap, found running the actual integration harness for
the first time: `tests/fake-vllm/fake_vllm.py`'s argparse only understood
its own `--fake-*`/`--port`/`--served-model-name` flags -- any REAL
`ArgBuilder`-generated argv (which always emits `--max-model-len`,
`--gpu-memory-utilization`, `--max-num-batched-tokens`, `--block-size` at
minimum, for every recipe) got rejected as `bad_args`, since argparse
doesn't know those flags. Fixed by adding every vLLM flag `ArgBuilder` can
emit as accepted-but-ignored arguments -- deliberately NOT a catch-all
(`parse_known_args`), since an actually-unrecognized flag failing fast is
itself a real test case elsewhere in this suite (`--this-flag-does-not-exist`).
`extra_args` stays unsupported by design (open-ended; a test exercising it
picks flags this double already understands).

Design decisions made while implementing, not fully spelled out on the
page:
- The served-name gate and "not registered"/"nothing to cancel"/etc.
  cases are modeled as typed exceptions at the SERVICE layer
  (`ServedNameChangeRequiresAcknowledgementException`,
  `SupervisorNotRegisteredException`, `InvalidOperationException`,
  `KeyNotFoundException`) rather than literal HTTP responses -- there's no
  API controller layer yet in this codebase's style (Blazor components
  call services directly via DI, confirmed in P09b); whatever surfaces
  these as actual HTTP 409s is a later phase's (or P14's UI) concern. The
  served-name exception carries both the old and new names, exactly what
  a 409 body would need.
- Failure categorisation (`reason`/`exit_code`/`log_tail`) is extracted
  from the terminal event's `detail` payload and returned in `SwapResult`
  in memory only, per the page's own explicit scope note -- not written
  to the `deployments` row's own failure columns (P10b wires that up
  alongside the push receiver).
- `IDeploymentTracker` is a separate interface from `ISupervisorClient`
  specifically so P10b can swap polling for a push-fed implementation
  without `SwapService` changing at all -- it only ever calls
  `TrackToTerminalAsync`, never touches `GetEventsAsync` directly.

Done:
- `src/Dashboard.Core/Supervisor/`: `SupervisorModels.cs` (DTOs mirroring
  the supervisor's JSON exactly, via `JsonNamingPolicy.SnakeCaseLower`),
  `SwapResult.cs` (`SwapResult`,
  `ServedNameChangeRequiresAcknowledgementException`,
  `SupervisorNotRegisteredException`).
- `src/Dashboard.Core/Abstractions/`: `ISupervisorClient`,
  `IDeploymentTracker`, `ISwapService`.
- `src/Dashboard.Infrastructure/Supervisor/`: `SupervisorClient` (typed
  HTTP client, named-client + per-call-absolute-URL pattern matching
  `DockerOrchestrator`'s own style), `PollingDeploymentTracker`.
- `src/Dashboard.Infrastructure/Persistence/V2/SwapService.cs` (new): the
  full flow -- pre-flight via `CompatibilityPass.Check`, `ArgBuilder.Build`,
  insert pending row, `POST /admin/deploy`, track to terminal, resolve
  (activate+stop-previous one-transaction / failed / cancelled). Also
  `RollbackAsync`, `StopAsync`, `CancelAsync` (with the phase-2
  previous-row bookkeeping from P07's own owner-confirmed decision),
  `RestartAsync`.
- `src/Dashboard.Infrastructure/Persistence/V2/CoreRecipeMappers.cs` (new):
  the EF-entity-to-pure-input mappers, extracted out of `RecipeService`
  (P09b) so `SwapService` can reuse them without duplicating the mapping
  logic.
- `DeploymentService` extended: `GetActiveAsync`, `GetPendingAsync`,
  `ActivateAndStopPreviousAsync` (stops the old row, THEN activates the
  new one, in that order within one transaction -- the partial unique
  index is checked per-statement, not deferred).
- `src/Dashboard.Infrastructure/Migrations/..._NarrowDeploymentsPendingIndex.cs`
  (new, see above).
- `src/Dashboard.Web/Program.cs`: `SupervisorClient`/`PollingDeploymentTracker`/
  `SwapService` registered; a new named `"supervisor"` HTTP client (30s
  timeout -- a real deploy is tracked via polling, never one held-open
  request the way `docker-pull`'s streaming calls are).
- `supervisor/supervisor/deploy_runner.py` + `admin_api.py`: `restart()`
  and `POST /admin/restart` now accept an optional `deployment_id` (see
  above).
- `tests/fake-vllm/fake_vllm.py`: accepts (and ignores) every vLLM flag
  `ArgBuilder` can emit (see above).
- New test infrastructure: `tests/Dashboard.Infrastructure.Tests/SupervisorFixture.cs`
  builds the REAL `supervisor/Dockerfile.test` image ONCE per test run
  (Testcontainers' `ImageFromDockerfileBuilder`, build context = repo
  root -- resolved by walking up to the nearest `.git`, since this repo
  has no `.sln` for Testcontainers' own solution-dir helper to anchor on)
  and starts a FRESH container per test from that image (a shared
  container would leak deploy state and deployment-id numbering across
  tests, since each test gets its own fresh Postgres database starting
  ids at 1 again).
- 7 new integration tests (`SwapServiceTests.cs`, against the real
  container + fake vLLM) covering every DoD bullet below, plus 2 new/
  updated `V2SchemaConstraintTests.cs` tests for the narrowed index and 1
  updated `V2ServiceTests.cs` test, plus 2 new Python tests
  (`test_restart_honors_a_caller_supplied_deployment_id` in both
  `test_cancel_stop_restart.py` and `test_admin_api.py`). Also fixed one
  pre-existing flaky Python test (`test_downloads_endpoints_round_trip...`,
  P08) found while re-running the suite -- a ~1KB fake download could
  finish before the 409-concurrency check ran under full-suite timing
  load; gave it a small `chunk_delay` like the other download tests
  already use. 177 Core + 41 Infrastructure .NET tests, 127 Python tests;
  `./scripts/gate.sh` green (now also builds the supervisor Docker image
  as part of `dotnet test`, since `Dashboard.Infrastructure.Tests` does).

Out of scope, not touched: push receiver, gap catch-up, persisted failure
report (P10b). UI (P14). Dashboard-startup reconciliation (P12).

### DoD self-check

| DoD bullet | Met? | Evidence |
|---|---|---|
| Swap A→B: row pending → active, previous row stopped/swapped, args_snapshot equals the argv sent, client proxy path unaffected | Yes | `Swap_AToB_FlipsRowsAndFreezesArgsSnapshot` (proxy path: trivially true -- nothing in the swap flow ever touches `Instance.ProxySlug`) |
| Different served_model_name without acknowledge → 409 with both names; with the flag → completes | Yes, as a typed exception (no API layer yet) | `Swap_DifferentServedModelNameWithoutAcknowledge_ThrowsWithBothNames` -- `ServedNameChangeRequiresAcknowledgementException` carries both names; a future API layer maps it to 409 |
| Swap to a `--fake-crash` model → row failed, previous record intact, previous model not silently restored | Yes | `Swap_ToACrashingModel_RowFailed_PreviousRecordIntactAndNotRestored` |
| Same (model, recipe) again → "already active", no new row | Yes | `Swap_SameModelAndRecipeAlreadyActive_ReturnsAlreadyActiveWithNoNewRow` |
| Rollback replays the earlier args_snapshot byte-for-byte | Yes (after fixing a real bug caught by this test) | `Rollback_ReplaysThePreviousArgsSnapshotByteForByte` |
| Stop → instance idle (no pending/active row), stop_reason='manual' | Yes | `Stop_LeavesInstanceIdleWithNoPendingOrActiveRow` |
| Cancel in phase 2 → idle, stop_reason='cancelled' | Yes | `Cancel_Phase2_GoesIdleAndStopsThePreviousRowToo` (also stops the previous-active row, per the owner-confirmed decision earlier this phase) |
| Restarting the dashboard process with the supervisor idle → the dashboard starts nothing | Met by scope, not directly tested | No reconciliation-on-startup code exists anywhere yet (P12's own job); nothing in this phase runs at dashboard startup at all |
| No code path in the swap flow touches the Docker API | Yes, confirmed by inspection | `grep -n Docker` across every P10a file -- only doc-comment prose references, zero `IDockerOrchestrator`/Docker Engine API usage |

## P10b -- Event receiver + catch-up + stored failure report

Confirmed with the owner before building, since the page's own instruction
("per-deployment apply pipeline ... replacing the polling-only tracker
from P10a") directly reshapes P10a's already-shipped, already-tested
`SwapService` contract: `SwapAsync`/`RollbackAsync`/`RestartAsync` now
return `"pending"` immediately after asking the supervisor, instead of
blocking until a terminal state. The new `IDeploymentEventApplier` (fed by
push, with catch-up polling as the fallback) is now the ONLY code path
that ever flips a deployment row to active/failed/stopped --
`IDeploymentTracker`/`PollingDeploymentTracker` (P10a) are gone entirely,
and P10a's own `SwapServiceTests.cs` was rewritten around the new
contract.

Hit and resolved three real design gaps while building the apply
pipeline, none spelled out verbatim on the page:

1. **Seq is shared across ALL push kinds (state/report/log) per
   deployment, but `deployment_events` only ever stores "state" kind
   rows** (its own `State` column has no room for the other two, and
   `GET /admin/deployments/{id}/events` only ever returns "state" events
   -- the supervisor never stores "report"/"log" pushes in its own
   per-deployment list). Comparing an incoming seq against
   `deployment_events`' own max stored seq would therefore see a false
   "gap" every time a report/log push's seq number falls between two
   state events. Fixed by adding `Deployment.LastSeenSeq` -- the highest
   seq seen across every kind, durable, which gap detection actually
   compares against.
2. **Which `stop_reason` applies to the PREVIOUS row once "ready"
   arrives is only known by the operation that created the pending row**
   (swap/restart -> "swapped", rollback -> "rollback") -- but applying
   "ready" now happens asynchronously, disconnected from whichever
   `SwapService` call produced that row. Fixed by adding
   `Deployment.PendingStopReason`, set at insert time, read by the
   applier when "ready" arrives.
3. **Catch-up can only recover "state" kind events** (same reason as
   #1) -- so a failed/crashed deploy's failure report, if the separate
   "report" push itself was lost, would never be recoverable through
   catch-up alone. Fixed by also writing `failure_reason`/`exit_code`/
   `log_tail` from a "failed"/"crashed" **state** event's own `detail`
   payload (which already carries the identical shape, per doc 5) --
   not just from the separate "report" push.

Also found, while adding the integration test harness, that the partial
unique index narrowed in P10a (`one_pending_per_instance`,
`WHERE status = 'pending'`) was still correct and needed no further
change here -- the new "pending coexists with active" shape from P10a is
exactly what lets a deploy stay `pending` while push/catch-up resolves it
asynchronously.

Layering note: `DeploymentHub` (the SignalR hub) and
`DeploymentCatchUpService` (the periodic catch-up `BackgroundService`)
both live in `Dashboard.Infrastructure`, not alongside the dashboard's
other hubs/hosted-services in `Dashboard.Application` -- `Infrastructure`
doesn't (and per the existing sibling-dependency shape, shouldn't) depend
on `Application`, and both of these are broadcast-from/driven-by
`DeploymentEventApplier`, which is Infrastructure-only by construction
(P09b's V2 services).

Testing note on "received by push": Testcontainers.NET has no supported
way to put the supervisor container on the host's network (no
`WithNetworkMode("host")`), so there's no publicly reachable receiver
endpoint for the real container to push to from inside these automated
tests. `SwapServiceTests.cs`'s integration tests instead drive outcomes
through the applier's own **catch-up** path against the real container
(itself one of the DoD's explicit scenarios: "drop the receiver... none
lost, none duplicated, final state correct") -- genuine push delivery
(receiver auth, dedupe-on-apply, and the SignalR broadcast itself) is
each tested directly and for real, just not strung end-to-end through an
actual supervisor container's own HTTP POST. The production wiring
(`EventPusher` -> `POST /internal/supervisor-events`) is otherwise
unchanged from P03c/this phase's own Program.cs mapping.

Done:
- Migration `AddDeploymentPushTrackingColumns`: `Deployment.LastSeenSeq`
  (int, default 0) and `Deployment.PendingStopReason` (nullable, CHECK
  `IN ('swapped','rollback')`) -- see findings #1/#2 above.
- `src/Dashboard.Core/Supervisor/SupervisorPushEvent.cs` (new): the push
  payload's own shape (`{deployment_id, seq, ts, kind, ...}`), distinct
  from P10a's `SupervisorDeploymentEvent` (the `GET .../events` response
  shape) since the two don't carry the same fields.
- `src/Dashboard.Core/Abstractions/IDeploymentEventApplier.cs` (new).
- `src/Dashboard.Infrastructure/Persistence/V2/DeploymentEventApplier.cs`
  (new): the "events -> rows" pipeline -- per-deployment serialized (an
  in-memory `SemaphoreSlim` per id, doc 5: "apply events... sequentially"),
  idempotent (a seq at or below `LastSeenSeq` is a no-op), gap-fetches
  via `ISupervisorClient.GetEventsAsync` before applying an
  ahead-of-sequence event, and is the only place that calls
  `IDeploymentService.ActivateAndStopPreviousAsync`/`TransitionAsync` for
  a push-driven transition.
- `src/Dashboard.Infrastructure/Persistence/V2/SupervisorEventReceiver.cs`
  (new): the receiver endpoint's own logic, pulled out of Program.cs so
  it's unit-testable without a full web host -- resolves the token to
  check via the event's own `deployment_id` -> instance ->
  `ISupervisorTokenProvider` (not one global secret, since different
  instances could have different tokens).
- `src/Dashboard.Infrastructure/Persistence/V2/DeploymentCatchUpService.cs`
  (new): periodic (`BackgroundService`, every 5s) catch-up for every
  still-"pending" deployment across every instance -- covers both
  "dashboard start" and "periodically while pending" from the page's own
  work item 3 in one mechanism.
- `src/Dashboard.Infrastructure/Events/DeploymentHub.cs` (new, moved from
  an initial attempt in Dashboard.Application -- see layering note
  above): per-deployment SignalR group, `receiveState`/`receiveLog`.
- `src/Dashboard.Infrastructure/Persistence/V2/SwapService.cs`: rewritten
  per the contract change above -- no longer depends on
  `IDeploymentTracker` (deleted), `DeployAsync` returns `"pending"`
  immediately; sets `PendingStopReason` on every new pending row.
- `src/Dashboard.Web/Program.cs`: `POST /internal/supervisor-events`
  (delegates to `SupervisorEventReceiver`), `DeploymentHub` mapped at
  `/deployment-hub`, `DeploymentCatchUpService` registered as a hosted
  service.
- `src/Dashboard.Infrastructure/Dashboard.Infrastructure.csproj`: added
  the `Microsoft.AspNetCore.App` `FrameworkReference` (for SignalR
  server types) and dropped four now-redundant `PackageReference`s it
  already carries transitively.
- 20 new tests: `DeploymentEventApplierTests.cs` (dedupe, report-kind
  failure-column writes, log-kind never persisting a row, failed-state's
  own detail populating failure columns), `SupervisorEventReceiverTests.cs`
  (missing/wrong/correct token, unknown deployment id), `DeploymentHubTests.cs`
  (a real `HubConnection` against a real in-memory `TestServer`, receiving
  state + log events in order), plus `SwapServiceTests.cs` rewritten for
  the new fire-and-forget contract with 2 new tests (crash-after-ready;
  the existing ones now drive outcomes via real catch-up against the
  real container instead of asserting synchronously). Found and fixed a
  real bug via `ReportKindPush_WritesFailureColumnsOnTheDeploymentRow`
  and `FailedStateEvent_WritesFailureColumnsFromItsOwnDetail`:
  `PushDetailHelpers.GetInt` only unwrapped `JsonElement`-boxed values
  (the shape real deserialized pushes arrive in) and silently returned
  null for a plain boxed `int` -- harmless for real traffic, but would
  have been a real bug for any future caller handing it an
  already-typed value. Fixed to handle both. 177 Core + 51 Infrastructure
  .NET tests, 127 Python tests; `./scripts/gate.sh` green.

Out of scope, not touched: rendering (P14). Boot reconcile banner (P12).

### DoD self-check

| DoD bullet | Met? | Evidence |
|---|---|---|
| Full swap A→B: deployment_events rows exactly match the supervisor's sequence, received by push | Yes, push mechanism tested directly; strung end-to-end via catch-up (see testing note above) | `DeploymentHubTests` (real SignalR push) + `SupervisorEventReceiverTests` (real receiver logic) + `SwapServiceTests` (real container, correct final sequence) |
| Duplicate delivery of the same event → no duplicate row | Yes | `DuplicateStateEventDelivery_IsANoOp` |
| Drop the receiver mid-swap, restart it → all missed events recovered; none lost, none duplicated, final state correct | Yes | Every `SwapServiceTests` test drives its outcome via catch-up alone (never push) and asserts the final row state -- this IS that scenario |
| Swap to a failing model → row failed with failure_reason, exit_code, non-empty log_tail; reload shows them | Yes | `Swap_ToACrashingModel_RowFailedWithFailureReport_PreviousRecordIntactAndNotRestored` |
| Crash after ready → row stopped, stop_reason='crash' | Yes | `CrashAfterReady_RowStopped_StopReasonCrash` |
| Missing/wrong token → 401 and nothing written | Yes | `MissingToken_Returns401AndWritesNothing`, `WrongToken_Returns401AndWritesNothing` |
| A SignalR test client in the deployment group receives the state events and log lines in order | Yes | `ClientInTheDeploymentGroup_ReceivesStateAndLogEventsInOrder` |

## P11 -- Proxy refactor + regression tests

Read `ModelProxyService` fully before touching it, per the page's own
instruction. Preserved every must-not-regress piece of logic
**byte-for-byte**: `StreamWithTeeAsync` (SSE heartbeat, oversized-payload
guard, mirrored-text cap) and every pure JSON-extraction helper
(`TryExtractModelName`, `TryExtractDeltaText`, `TryExtractReasoningText`,
`ExtractMessages`, `IsModelsListPath`) are unchanged -- only
`HandleAsync`/`EnsureModelActiveAsync` (renamed `CheckModelActiveAsync`,
since it never swaps anymore) and `ForwardAsync`'s target-URL construction
actually changed, to read from the new tables and drop the retired
mechanics.

Found a real layering conflict partway through, resolved the same way
P10b's `DeploymentHub`/`DeploymentCatchUpService` already were:
`ModelProxyService` now depends on the V2 services
(`IInstanceService`/`IModelService`/`IDeploymentService`), which are
Infrastructure-only by design (P09b) -- but `ModelProxyService` lived in
`Dashboard.Application`, which doesn't (and per the existing sibling-
dependency shape between Application and Infrastructure, shouldn't)
reference Infrastructure. Moved the whole `Proxy/` folder
(`ModelProxyService.cs`, `TrafficHub.cs`, `InFlightTrafficTracker.cs` --
`TrafficHub` and the tracker had to move too, since `ModelProxyService`
needs all three together) from `Dashboard.Application.Proxy` to
`Dashboard.Infrastructure.Proxy`. Updated every reference (`Program.cs`,
`LiveTraffic.razor`, the ported unit test file).

Design decisions made while implementing, not spelled out verbatim on
the page:
- **The inference base URL is derived, not stored**: same host as the
  instance's own `AdminUrl` (the supervisor and vLLM always run in the
  same container, doc 5), just the instance's own `Port` instead of
  whatever port `AdminUrl` uses. There's no separate "inference URL"
  field on `Instance` -- deriving it means the two can never drift apart
  the way two independently-edited fields could.
- **The client-facing "model" identifier is still `Model.Name`** (the
  catalog entry), not the active deployment's `Recipe.ServedModelName` --
  matches the legacy system's exact semantics (`GET /v1/models` lists
  catalog entries, "more than the one loaded model" per the DoD), and
  keeps the registered-vs-active distinction meaningful: a recipe's own
  served-model-name is an internal vLLM launch detail, not something a
  client ever names in its own request.
- **Two error codes, not one**: a model that isn't registered on the
  instance's server at all gets 404 ("not registered"); one that's
  registered but not the active deployment gets 400 with the new
  helpful message ("...registered but not active — swap it in the
  dashboard"). The page's own "an OpenAI-style error body, HTTP
  404/400 — keep the current error shape" reads as "these are the two
  status codes in play, and keep using the existing `WriteErrorAsync`
  plain-text pattern" rather than inventing a new JSON envelope --
  `WriteErrorAsync` was never changed.
- **"No supervisor/Docker call is made" is a structural guarantee, not
  just a tested behavior**: `ModelProxyService`'s constructor no longer
  takes an `ISupervisorClient` or `IDockerOrchestrator` at all -- there is
  no code path left in this file that COULD call either, a stronger
  claim than a recording-stub assertion would make on its own (one is
  still effectively demonstrated by the integration tests never wiring
  either dependency in and everything still working).

Done:
- `src/Dashboard.Infrastructure/Proxy/ModelProxyService.cs` (moved +
  rewritten): resolves the instance by `IInstanceService.GetBySlugAsync`
  (new method); `GET /v1/models` answers from
  `IModelService.ListByServerAsync`; `CheckModelActiveAsync` compares the
  requested name against the active deployment's model (via
  `IDeploymentService.GetActiveAsync` + `IModelService.GetAsync`) and
  returns the new helpful error instead of swapping; no more
  `_activity.Touch(...)` calls (idle-unload is gone).
- `src/Dashboard.Infrastructure/Proxy/TrafficHub.cs`,
  `InFlightTrafficTracker.cs` (moved, unchanged otherwise).
- `IInstanceService.GetBySlugAsync` (new) -- the proxy route's own
  identifier is always the slug, never a raw id.
- **Removed** (per the page's own "remove the hosted services and their
  tests"): `ActivityTracker.cs`/`IActivityTracker.cs`,
  `IdleUnloadService.cs`, `PreloadService.cs`, and
  `ActivityTrackerTests.cs` -- their DI/hosted-service registrations in
  `Program.cs` too. `DockerOrchestratorPayloadTests.cs`/`LaunchSpecTests.cs`
  stay (still reachable from the old UI's manual swap button until P14 --
  final removal is P16a's, per the Start Here's "don't delete files yet"
  rule); `plan/test-classification.md` updated to reflect this.
- `src/Dashboard.Web/Program.cs`: route renamed `/proxy/{sourceId}/...`
  -> `/proxy/{slug}/...` (cosmetic, matches what it's actually resolving
  now); the four removed registrations dropped.
- `src/Dashboard.Core/Models/ModelInfo.cs`: `IdleTtlSeconds`'s own doc
  comment updated -- it referenced the now-deleted `IdleUnloadService`.
  The field itself (and the old UI's own idle-TTL/preload-on-startup
  inputs in `Models.razor`) are left in place, now inert -- still part
  of the legacy schema/UI the Start Here says not to delete until P16a.
- 6 new integration tests (`tests/Dashboard.Infrastructure.Tests/ModelProxyServiceTests.cs`
  -- a different file from the pure-helper unit tests already in
  `Dashboard.Core.Tests`, same class name, different namespace/assembly,
  no conflict): a real listening fake-upstream HTTP server standing in
  for vLLM (a real socket is needed -- `ModelProxyService`'s own
  `HttpClient` dials an absolute URL, which an in-memory `TestServer`
  transport can't answer), and the real `ModelProxyService` behind its
  own `TestServer`. Covers the registry listing, byte-for-byte streaming
  + Live Traffic + genuine mid-stream visibility (joining the SignalR
  group mid-stream and getting `TrafficHub.JoinSource`'s own catch-up
  replay), the new registered-but-not-active error, not-registered 404,
  already-active pass-through, and unknown-slug 404. 173 Core + 57
  Infrastructure .NET tests (down from 177 Core, since
  `ActivityTrackerTests.cs`'s 4 tests were removed with it), 127 Python
  tests; `./scripts/gate.sh` green with the retired code and tests gone.

Out of scope, not touched: UI (P15a). Anything that calls the supervisor
(P10a's `ISwapService` is how a future UI, not this request path, would
ever change what's running).

### DoD self-check

| DoD bullet | Met? | Evidence |
|---|---|---|
| `GET /proxy/<slug>/v1/models` returns the registry list (more than one loaded model) | Yes | `GetModelsList_ReturnsTheFullRegistry_NotJustTheActiveModel` |
| A completion streams byte-for-byte to the client AND appears in Live Traffic; a mid-stream request is visible before it completes | Yes | `Completion_StreamsByteForByteAndAppearsInLiveTraffic_VisibleMidStream` |
| Registered-but-not-active model → helpful error; no supervisor/Docker call made | Yes, structurally guaranteed | `RegisteredButNotActiveModel_ReturnsHelpfulError`; `ModelProxyService` no longer references `ISupervisorClient`/`IDockerOrchestrator` at all |
| Leave fake vLLM idle past the old 30s window → still serving, never unloaded | Yes, structurally | `IdleUnloadService` deleted entirely -- nothing left to unload it |
| Startup does not trigger any model start (no preload) | Yes, structurally | `PreloadService` deleted entirely -- nothing left to start one |
| Oversized payload/long-idle SSE heartbeat/mirrored-text cap behave as before; app logs reach the DB; a container restart keeps the login session | Yes | `StreamWithTeeAsync` preserved byte-for-byte (not touched by this phase at all); DB logging and Data Protection keys are unrelated infrastructure this phase never modifies |
| `gate.sh` passes with the retired code and tests removed | Yes | confirmed above, 173 + 57 .NET + 127 Python, all green |

## P12 -- Boot reconcile + degradation

Three independent pieces, each with its own real end-to-end test: the
read-only write guard, the reconciler + Adopt/Fix/Mark-stopped actions,
and the minimal Docker client for the "supervisor unreachable" case.

Asked and got an answer on the write-guard design up front (the page says
"a global write guard + banner driven by DB health checks," but this app's
writes happen both through HTTP endpoints and direct Blazor-Server-to-C#
calls, which an HTTP middleware alone wouldn't cover): a shared
`IReadOnlyGuard`, checked inside each V2 service's own write methods
(Create/Update/Delete/Transition across all five), backed by a periodic
DB-health-check `BackgroundService` -- not an HTTP-layer gate.

Found a real contradiction between this page's own DoD and P11's
already-committed design partway through, and stopped to ask rather than
silently resolve it (per the standing rule): the DoD says an instance with
`admin_url IS NULL` should show "needs supervisor registration" **and**
"the proxy still forwards to its configured inference URL" -- but P11
deliberately removed any separately-stored inference URL/host, deriving
it entirely from `AdminUrl`'s own host. With no `AdminUrl`, there was
nothing left to derive a host from, so the proxy returned 502 instead of
forwarding anywhere -- directly contradicting this page's DoD. Owner
decided (2026-11-01): add back a separate `Instance.Host` field,
independent of `AdminUrl`, that the proxy prefers and only falls back to
deriving from `AdminUrl` when unset. This reopens (narrowly) a P11
decision, not a P12 one.

Also found, while computing reconcile state against the real supervisor
container, that `current_deployment_id` (`GET /admin/status`) is cleared
back to `null` the instant a deploy concludes (`deploy_runner.py`) --
it's never populated in the "ready" steady state the reconciler actually
runs against. A deployment-id comparison (what the first implementation
attempt used) therefore always reported `Mismatch` even when the DB and
supervisor agreed. Fixed by comparing the supervisor's `Child.Argv`
against the DB's active deployment's own frozen `ArgsSnapshot` instead --
argv is the only thing that stays meaningful once past the deploy phase.

"Fix" needed its own `ISwapService.ForceRedeployAsync`, not a call through
`SwapAsync`: `SwapAsync`'s "already active" short-circuit (same model +
recipe already the DB's active row -> no-op) is exactly wrong for Fix's
own use case, where the DB's active row says X but the supervisor is
actually running something else -- `SwapAsync` would see "X already
active" by its own bookkeeping and do nothing. `ForceRedeployAsync` reuses
the same insert-pending-row-then-deploy pipeline but skips that check (and
the served-name acknowledgement gate -- this is an admin correction, not a
live user-initiated change).

Done:
- `src/Dashboard.Core/Abstractions/IReadOnlyGuard.cs` (new):
  `IReadOnlyGuard`/`ReadOnlyModeException`.
- `src/Dashboard.Infrastructure/Persistence/V2/ReadOnlyGuard.cs` (new):
  plain mutable state holder, `Changed` event only fires on an actual
  transition.
- `src/Dashboard.Infrastructure/Persistence/V2/DatabaseHealthMonitorService.cs`
  (new): `BackgroundService`, 5s `Task.Delay` loop (same shape as P10b's
  `DeploymentCatchUpService`), `db.Database.CanConnectAsync()`; any
  exception (not just a clean "unhealthy" result) also flips to read-only,
  since "can't tell" must be treated the same as "down."
- `EnsureWritable()` added to every Create/Update/Delete/Transition method
  on `ServerService`, `InstanceService`, `ModelService`, `RecipeService`
  (including `DuplicateAsync`), `DeploymentService` (including
  `ActivateAndStopPreviousAsync`) -- all five now take `IReadOnlyGuard` in
  their constructor.
- `src/Dashboard.Infrastructure/Persistence/V2/Instance.cs`: `ContainerName`
  (nullable, for the Docker client) and `Host` (nullable, the P11-gap fix
  above) added; migrations `AddInstanceContainerName`, `AddInstanceHost`.
- `src/Dashboard.Core/Abstractions/IBootReconciler.cs` (new):
  `ReconcileStatus` (Ok/Mismatch/IdleVsActive/NeedsRegistration/Down),
  `ReconcileState`, `IBootReconciler` (`ReconcileInstanceAsync`/
  `ReconcileAllAsync`), `IReconcileActions` (`AdoptAsync`/`MarkStoppedAsync`
  -- Fix is `ISwapService.ForceRedeployAsync`, not repeated here).
- `src/Dashboard.Infrastructure/Persistence/V2/BootReconciler.cs` (new):
  implements both interfaces; never corrects either side on its own, only
  reports + the two explicit actions it owns.
- `src/Dashboard.Infrastructure/Persistence/V2/SwapService.cs`:
  `ForceRedeployAsync` added ("Fix").
- `src/Dashboard.Infrastructure/Proxy/ModelProxyService.cs`:
  `BuildInferenceBaseUrl` now prefers `Instance.Host`, falls back to
  `AdminUrl`'s host, returns null (-> 502) only when neither is set;
  `HandleAsync`'s old "AdminUrl null -> 502 'no supervisor registered'"
  gate replaced with "no host derivable at all -> 502 'no inference host
  configured yet'" -- "needs supervisor registration" is now purely a
  reconcile-page concept, never a proxy gate.
- `src/Dashboard.Core/Abstractions/IMinimalDockerClient.cs` (new) +
  `src/Dashboard.Infrastructure/Docker/MinimalDockerClient.cs` (new):
  `StartContainerAsync`/`HardResetContainerAsync`, same raw-HTTP-to-
  Docker-Engine-API convention as `DockerOrchestrator` but with none of
  its container-creation machinery. **Not exercised against a real Docker
  host in this phase** -- see Deviations below.
- `src/Dashboard.Web/Program.cs`: `IReadOnlyGuard` +
  `DatabaseHealthMonitorService` registered before the V2 services;
  `IBootReconciler`/`IReconcileActions`/`IMinimalDockerClient` registered.
- New tests: `tests/Dashboard.Core.Tests/V2ServiceValidationTests.cs`
  (`ReadOnlyGuardTests`, pure unit, no DB) -- guard state transitions +
  `EnsureWritable` wired into Server/Instance/Model services.
  `tests/Dashboard.Infrastructure.Tests/BootReconcilerTests.cs` (new, 6
  tests, real supervisor container): Ok, NeedsRegistration, Down (unreachable
  admin_url), IdleVsActive -> MarkStopped clears it, Mismatch (supervisor
  serving something the DB doesn't know) -> Adopt records it, Mismatch (DB
  says a different argv) -> Fix (`ForceRedeployAsync`) resolves it.
  `tests/Dashboard.Infrastructure.Tests/ModelProxyServiceTests.cs`: 2 new
  tests for the `Instance.Host` fix (forwards with no `AdminUrl` when
  `Host` is set; 502 when neither is set).
- `plan/testing-reference.md`: new "Boot reconcile + degradation (P12)"
  section; the P11 section's own proxy-URL note corrected (was "derived,
  never stored," now reflects the `Host`-first fallback).
- 179 Core + 65 Infrastructure .NET tests (up from 173 + 57), 127 Python
  tests; `./scripts/gate.sh` green.

Out of scope, not touched: banner/page visuals (P14/P15), the Servers-page
registration flow that would actually SET `Host`/`ContainerName` (P15b) --
this phase only needed the fields to exist and the service logic that
reads/writes them to be correct, not a UI to populate them.

### DoD self-check

| DoD bullet | Met? | Evidence |
|---|---|---|
| Seeded mismatch -> "mismatch" reconcile state with both values | Yes | `ReconcileInstance_SupervisorServingSomethingDbDoesNotKnow_Mismatch_AndAdoptRecordsIt`, `ReconcileInstance_DbSaysDifferentModelThanSupervisor_Mismatch_AndFixForcesDbsVersion` |
| Adopt writes X, Fix triggers a swap to Y, neither happens without the click | Yes | Both tests above -- `AdoptAsync`/`ForceRedeployAsync` only run when the test explicitly calls them; `ReconcileInstanceAsync` alone never mutates anything |
| Supervisor idle but DB active -> "idle vs active" state, Mark stopped sets the row stopped | Yes | `ReconcileInstance_DbActiveButSupervisorIdle_IdleVsActive_AndMarkStoppedClearsIt` |
| Kill Postgres mid-session -> read-only banner, writes rejected with a clear error, supervisor-status/logs/playground reads still succeed, restoring Postgres clears read-only | Partially | Guard mechanism + `ReadOnlyModeException` unit-tested (`ReadOnlyGuardTests`); reads structurally unaffected (they never call `EnsureWritable`) since this was never wired into any read path. **Not** verified against an actual killed real Postgres container end-to-end in this phase -- `DatabaseHealthMonitorService`'s own `CanConnectAsync` polling loop has no integration test here. See Deviations. |
| Kill the supervisor container -> instance "down" with last-known state, Start container offered | Partially | `Down` status verified against an unreachable `admin_url` (`ReconcileInstance_SupervisorUnreachable_Down`), equivalent in effect to a killed container from the reconciler's own point of view. `IMinimalDockerClient.StartContainerAsync` itself has no test -- see Deviations. |
| Instance with `admin_url IS NULL` -> "needs supervisor registration", proxy still forwards to its configured inference URL | Yes (after the `Instance.Host` fix) | `ReconcileInstance_NoAdminUrl_NeedsRegistration`; `NoAdminUrlButHostSet_StillForwardsNormally` |
| Dashboard restart with an idle supervisor -> nothing is started | Yes, structurally | Nothing in this phase (or any prior one) auto-starts a deployment on dashboard boot -- `DeploymentCatchUpService` (P10b) only resumes already-`pending` rows, never starts a new one; the reconciler only ever reports state, Adopt/Fix/Mark-stopped are explicit operator actions. No preload path exists (P11 deleted `PreloadService` entirely). |

## Deviations / findings worth flagging

- `tests/Dashboard.Web.Tests` currently has no test files (project exists,
  `dotnet test` reports "No test is available"). Not a P00 blocker -- the
  gate still exits 0 -- but noting it since the phase page assumes existing
  unit tests to classify, and this project contributes none yet.
- **P12**: `IMinimalDockerClient` (Start/Hard-reset) and
  `DatabaseHealthMonitorService`'s actual Postgres-down/up transition have
  no integration test against a real Docker host / a real killed-and-
  restored Postgres container -- both would require taking action against
  live infrastructure (starting/stopping real containers outside this
  repo's own Testcontainers-managed ones), which the standing "always ask
  before any action on a live/remote system" rule means wasn't done
  without asking first. The read-only guard's own logic (throws when
  flagged, doesn't when not) is proven at the unit level; the health
  monitor's polling loop and the Docker client's HTTP calls are simple
  enough (same pattern as already-tested `DeploymentCatchUpService` /
  `DockerOrchestrator`) that the risk is believed low, but this is
  flagged rather than silently assumed.
