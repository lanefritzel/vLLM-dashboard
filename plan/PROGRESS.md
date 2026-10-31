# Refactor progress

Tracked here, not in the Outline KB, per the Build Plan's working rules.

| Phase | Status | Date | Notes |
|---|---|---|---|
| P00 | done | 2026-10-31 | See below. |

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

## Deviations / findings worth flagging

- `tests/Dashboard.Web.Tests` currently has no test files (project exists,
  `dotnet test` reports "No test is available"). Not a P00 blocker -- the
  gate still exits 0 -- but noting it since the phase page assumes existing
  unit tests to classify, and this project contributes none yet.
