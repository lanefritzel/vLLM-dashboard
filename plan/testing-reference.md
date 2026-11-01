# Testing reference

Concrete commands/vars used while verifying each phase by hand, so a future
session doesn't have to re-derive them. `plan/PROGRESS.md` has the
per-phase narrative and findings; this is the copy-pasteable how-to.

## How testing is done, phase by phase

Every phase in this refactor goes through the same sequence before it's
considered done. Follow it in order -- skipping straight to "tests pass" has
repeatedly missed real bugs (see below).

1. **Implement**, then write tests alongside the code (pytest for the
   supervisor, xUnit for .NET) -- real subprocesses/real fake-vllm as a test
   double, not mocks, for anything state-machine- or process-shaped.
2. **Run the narrow test file first** (`pytest supervisor/tests/test_x.py
   -q`), then the full suite (`./.venv/bin/python -m pytest supervisor/tests
   -q` from the repo root -- use the project's own venv, not the system
   `python3`, which won't have pytest installed).
3. **Run `./scripts/gate.sh`** -- the full project gate (.NET build/test +
   fake-vllm pytest + supervisor pytest). Nothing is "done" until this is
   green.
4. **Build `supervisor/Dockerfile.test` and verify by hand in a real
   container** -- this is not optional busywork; it has caught bugs pytest
   alone missed every single time it's been skipped:
   - P02: a zombie-reaping bug (child never reaped outside the boot
     health-check window) only showed up watching a real process tree.
   - P03c: an event-loop teardown bug (background tasks silently dying)
     only reproduced across a real container restart, not in-process.
   - P05: a fast deploy losing its first log lines to the archive only
     showed up diffing `GET /admin/logs` against `GET
     /admin/deployments/<id>/logs` on a live container.
   - P06: smoke recording initially clobbered the terminal `ready` state
     with `smoke` (see `plan/PROGRESS.md`) -- caught by a real deploy +
     curl against `/admin/status`, not by the unit tests, which didn't
     happen to assert `runner.state` immediately after a passing smoke run.

   Build once (`docker build -f supervisor/Dockerfile.test -t
   supervisor-test .` from the repo root), run with `-e
   SUPERVISOR_TOKEN=...`, and curl every endpoint the phase touches --
   including ones from *earlier* phases that the new code runs alongside
   (e.g. P06's smoke test runs inside the same deploy that P03b's failure
   report and P04's GPU release already instrumented). Always `docker rm
   -f` the container (and any volume) when done -- see each phase's
   "Cleanup" note below.
5. **Self-check the Definition of Done explicitly**, bullet by bullet, in
   `plan/PROGRESS.md` -- not "tests pass so it's done." A bullet with no
   corresponding assertion or manual curl gets called out as a gap, not
   assumed.
6. **Update this file** with the new phase's copy-pasteable commands/vars,
   in the same pass as the DoD self-check -- not as an afterthought later.
   Future sessions should never have to re-derive a curl command or an env
   var that a past session already worked out.
7. Wait for the owner's explicit "commit" before committing anything --
   never automatic, regardless of how clean the phase looks.

## The gate

```bash
./scripts/gate.sh
```
Runs: `dotnet build` -> `dotnet test` (all 3 .NET test projects, including
`Dashboard.Infrastructure.Tests`, which starts a real Postgres via
**Testcontainers** -- this means `dotnet test`/`gate.sh` invokes Docker
automatically, by design, since P01a) -> fake-vllm pytest smoke -> supervisor
pytest unit tests. One command, non-zero exit on any failure.

The Python steps reuse a single venv at `.venv/` (gitignored), created on
first run. If you add a new Python test dir, add its
`requirements-test.txt` install + `pytest` invocation to `gate.sh` the same
way the existing sections do.

## .NET: live-DB-copy tests (P01b)

`tests/Dashboard.Infrastructure.Tests/LiveDbConversionManualCheck.cs` --
skipped by default (no env var set = no-op pass). To re-run the conversion
routine against a fresh restored copy of the live DB (e.g. before P16a):

```bash
P01B_LIVE_DB_CONNECTION="Host=localhost;Port=<port>;Database=dashboard;Username=dashboard;Password=dashboard" \
  dotnet test tests/Dashboard.Infrastructure.Tests --filter FullyQualifiedName~LiveDbConversionManualCheck \
  --logger "console;verbosity=detailed"
```
(`--logger` is required to actually see the printed report -- without it,
`dotnet test` swallows stdout from a passing test.)

### Pulling a fresh copy of the live DB

**Never read the live DB directly** -- P01b's page is explicit about this.
Pull a `pg_dump`, copy it locally, restore it into a throwaway container,
work from that, then delete both the container and the dump file. Always
confirm with the owner before doing this (per the global "ask before any
live/remote action" rule) -- this isn't a standing authorization.

```bash
# On tes.home: the dashboard's postgres container (found via `docker ps`)
ssh lanef@tes.home "docker exec llm-stack-postgres-1 pg_dump -U dashboard -d dashboard -F c -f /tmp/dump.pgdump"
ssh lanef@tes.home "docker cp llm-stack-postgres-1:/tmp/dump.pgdump /tmp/dump.pgdump"
scp lanef@tes.home:/tmp/dump.pgdump <local-scratch-path>/dump.pgdump
ssh lanef@tes.home "rm -f /tmp/dump.pgdump && docker exec llm-stack-postgres-1 rm -f /tmp/dump.pgdump"  # clean up on the remote host

# Locally: throwaway postgres + restore
docker run -d --name live-db-copy -e POSTGRES_DB=dashboard -e POSTGRES_USER=dashboard \
  -e POSTGRES_PASSWORD=dashboard -p 15433:5432 postgres:16
docker cp <local-scratch-path>/dump.pgdump live-db-copy:/tmp/dump.pgdump
docker exec live-db-copy pg_restore -U dashboard -d dashboard --no-owner --no-privileges /tmp/dump.pgdump

# Bring the restored copy's schema up to the current migrations (legacy schema only, until P01a's migration is applied):
dotnet ef database update --project src/Dashboard.Infrastructure --startup-project src/Dashboard.Web \
  --connection "Host=localhost;Port=15433;Database=dashboard;Username=dashboard;Password=dashboard"

# When done:
docker rm -f live-db-copy
```

Live DB facts (as of 2026-10-31, may drift -- re-check rather than trust
this blindly): container `llm-stack-postgres-1`, db/user/password all
`dashboard`/`dashboard`/`dashboard`, 2 `PhysicalServers` (`tes.home`,
`Serenity`), 1 vLLM `Sources` row (slug `tes-home`), 0 Ollama sources, 4
`Models` rows (1 active: `qwen3.8-vllm`), 1 Identity user. Full details and
the known DB-vs-live-compose drift: `plan/conversion-mapping.md`.

## Postgres-over-bridge soak test (P01c)

```bash
scripts/soak-postgres-bridge.sh [idle-minutes] [cycles]   # defaults: 10 3 (~30+ min)
scripts/soak-postgres-bridge.sh 0.1 2                     # fast sanity check (~15s)
```
One command, builds+runs+tears down `deploy/docker-compose.soak.yml`
itself. No manual cleanup needed.

## Supervisor Docker image (P02 / P03a)

Build the CI-friendly test image (build context must be the **repo root**,
not `supervisor/`, since it also pulls in `tests/fake-vllm/fake_vllm.py`):

```bash
docker build -f supervisor/Dockerfile.test -t supervisor-test .
```

Run it (the bare minimum -- add `-e SUPERVISOR_TOKEN=...` to use any
`/admin/*` route other than `/admin/health`, and a volume at `/state` to
persist `state.json` across restarts):

```bash
docker volume create supervisor-state
docker run -d --name supervisor-dev --init \
  -v supervisor-state:/state \
  -e SUPERVISOR_TOKEN=secret \
  -p 18100:8000 \
  supervisor-test serve /models/fake --served-model-name fake-model --port 9999
```
- `--init` gives the container a `tini`-equivalent PID 1 (the test image
  doesn't bake `tini` in itself -- see `supervisor/Dockerfile`'s comment).
- The trailing command is the **compose `command`** fallback, only used on
  first boot (no `state.json` yet). `serve ...` gets normalized to
  `vllm serve ...`; `vllm` here resolves to `/usr/local/bin/vllm`, the shim
  (`supervisor/vllm-shim.sh`) that execs the real fake vLLM double.
- `ADMIN_PORT` (default `8000`) and `BOOT_HEALTH_TIMEOUT_SECONDS` (default
  `30`) are both overridable via `-e`.

Admin API calls (all need the token except `/admin/health`):
```bash
AUTH='Authorization: Bearer secret'
curl -s -H "$AUTH" http://localhost:18100/admin/status
curl -s -H "$AUTH" -H 'Content-Type: application/json' -X POST http://localhost:18100/admin/deploy -d '{
  "argv": ["python3", "/app/fake_vllm.py", "--port", "9001", "--served-model-name", "model-a"],
  "deployment_id": "d1",
  "health_timeout": 15
}'
curl -s -H "$AUTH" http://localhost:18100/admin/deployments/d1/events
curl -s -H "$AUTH" "http://localhost:18100/admin/deployments/d1/events?after=2"
curl -s -H "$AUTH" -X PUT http://localhost:18100/admin/settings -d '{"AUTO_START":"off"}'
```
`env: {"FOO": "bar"}` in the deploy body reaches the child; confirm via the
fake vLLM's own debug endpoint (added for exactly this, P03a):
```bash
docker exec <container> python3 -c "import urllib.request,json; print(json.loads(urllib.request.urlopen('http://127.0.0.1:<child-port>/debug/env').read()).get('FOO'))"
```

To simulate a failing/crashing boot replay, pass `--fake-crash` or
`--fake-slow-start <secs>` on the fake vLLM's own argv (after `--port`
etc.) -- these are the same flags documented in
`tests/fake-vllm/fake_vllm.py`'s own `--help`.

### Failure scenarios (P03b)

```bash
# Fail-fast: deploy with --fake-crash -- fails in ~1s (not health_timeout), reason=process_exited
curl -s -H "$AUTH" -H 'Content-Type: application/json' -X POST http://localhost:18100/admin/deploy -d '{
  "argv": ["python3", "/app/fake_vllm.py", "--port", "9001", "--served-model-name", "model-a", "--fake-crash"],
  "deployment_id": "d1", "health_timeout": 300
}'

# health_timeout: --fake-slow-start longer than health_timeout -- fails at
# ~health_timeout, reason=health_timeout, and the still-alive child is killed
curl -s -H "$AUTH" -H 'Content-Type: application/json' -X POST http://localhost:18100/admin/deploy -d '{
  "argv": ["python3", "/app/fake_vllm.py", "--port", "9002", "--served-model-name", "model-b", "--fake-slow-start", "30"],
  "deployment_id": "d2", "health_timeout": 3
}'

# crashed (not failed): --fake-exit-after dies AFTER reaching ready
curl -s -H "$AUTH" -H 'Content-Type: application/json' -X POST http://localhost:18100/admin/deploy -d '{
  "argv": ["python3", "/app/fake_vllm.py", "--port", "9003", "--served-model-name", "model-c", "--fake-exit-after", "2"],
  "deployment_id": "d3", "health_timeout": 15
}'
```
`/admin/status`'s `failure_report` field (`{reason, exit_code, log_tail}`)
is read from `state.json`, not in-memory -- it survives a container
restart, so this is also how you check "idle boot after a failed run still
shows the report" (P02/P03b).

### Event push to a real stub (P03c)

The container needs to reach a stub running on the host -- simplest is
`--network host` for a one-off manual check (not how it'd run in compose,
where the dashboard is just another service on the same bridge network).

```bash
# A minimal recording stub (writes each POST to a file, one JSON line each):
cat > /tmp/stub_server.py << 'EOF'
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
LOGFILE = "/tmp/stub_requests.jsonl"
class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length) if length else b"{}"
        with open(LOGFILE, "a") as f:
            f.write(json.dumps({"headers": dict(self.headers), "body": json.loads(body)}) + "\n")
        self.send_response(200); self.end_headers()
    def log_message(self, *a): pass
open(LOGFILE, "w").close()
ThreadingHTTPServer(("0.0.0.0", 18999), Handler).serve_forever()
EOF
nohup python3 /tmp/stub_server.py > /tmp/stub_server.log 2>&1 &

docker run -d --name supervisor-dev --init --network host \
  -e SUPERVISOR_TOKEN=secret -e DASHBOARD_EVENT_URL=http://127.0.0.1:18999/events \
  -e ADMIN_PORT=18500 supervisor-test

# ... trigger a deploy via /admin/deploy as above, then:
cat /tmp/stub_requests.jsonl | python3 -c "
import sys, json
for line in sys.stdin:
    r = json.loads(line)
    print(r['headers'].get('Authorization'), '|', r['body'].get('kind'), r['body'].get('seq'),
          r['body'].get('state') or list(r['body'].get('report', {}).keys()) or r['body'].get('lines'))
"
```
For the test project's own equivalent (no Docker, used by the actual
pytest suite), see `supervisor/tests/recording_stub.py`'s `RecordingStub`.

### GPU backend (P04)

```bash
curl -s -H "$AUTH" http://localhost:18100/admin/gpu
curl -s -H "$AUTH" -X POST http://localhost:18100/admin/gpu/recheck
```
### Logs (P05)

```bash
curl -s -H "$AUTH" "http://localhost:18100/admin/logs?lines=50"                  # current process, ring buffer
curl -s -H "$AUTH" "http://localhost:18100/admin/deployments/d1/logs?lines=50"   # one past deployment's archived log
```
Persisted under the `/state` volume: `/state/logs/<id>.log`,
`/state/events/<id>.jsonl`, `/state/deployment_index.json` (rotation
tracking, last 50 kept). To check restart-survival for real: `docker rm`
the container, `docker run` a fresh one on the **same volume**, then hit
`GET /admin/deployments/<id>/events?after=0` for a deployment from before
the restart -- a fresh in-process object isn't a real enough test, since
Python-level state obviously doesn't survive; the volume is what matters.

Real Intel VRAM data on tes.home comes from the `xe` driver's sysfs
counters (`/sys/class/drm/card0/device/mem_info_vram_used`/`_total`), **not**
`xpu-smi` -- confirmed via the owner's sibling project
`~/appdevelopment/application_development/intel-gpu-monitor`
(`monitor/collectors/xe_sysfs.py`), not a live host check. To force the
fake backend for manual testing: `-e GPU_BACKEND=fake` and write
`{"vendor":"fake","count":1,"total_mb":12288,"used_mb":N}` to the path in
`GPU_BACKEND_FAKE_PATH` (default `/tmp/fake_gpu_backend.json`).

Finding zombies/process trees in a running container (no `ps`/`kill`
binaries in the slim test image): use `docker top <cid>` from the host for
the tree, and `docker exec <cid> python3 -c "..."` with `os.listdir('/proc')`
+ `os.kill` for anything that needs signaling from inside the container's
own PID namespace -- see the git history of this file's P02 section if you
need the exact snippets again.

Cleanup:
```bash
docker rm -f supervisor-dev
docker volume rm -f supervisor-state
docker rmi supervisor-test
```

### Cancel + Stop + Restart (P07)

All three share the single-operation lock with `/admin/deploy`: Stop/Restart
409 (`{"deployment_id": ...}`) against an in-flight deploy; Restart also
409s (`{"state": ...}`) while idle/not ready, same rule as `/admin/smoke-test`.
Cancel 409s (plain string detail) with nothing in flight or a stale id --
unlike Stop, which is **idempotent** (200, `released:true`) when nothing
is running at all.

```bash
AUTH='Authorization: Bearer secret'

# Cancel phase 1 vs phase 2 is decided from LIVE process state, not timing --
# curling two separate requests always gives the deploy task enough time to
# actually stop the old process, so manual curl testing always lands in
# phase 2 (below). Phase 1 (old model provably untouched) is only
# deterministic in-process, with zero `await` between start_deploy() and
# cancel_deploy() in the same event loop tick -- see
# test_cancel_before_the_new_deploy_runs_at_all_leaves_old_model_untouched
# in test_cancel_stop_restart.py if you need to re-verify that path.

# Phase 2: deploy with a slow-loading child, cancel mid-load.
curl -s -H "$AUTH" -H 'Content-Type: application/json' -X POST http://localhost:18100/admin/deploy -d '{
  "argv": ["python3", "/app/fake_vllm.py", "--port", "9001", "--served-model-name", "model-b", "--fake-slow-start", "20"],
  "deployment_id": "d4", "health_timeout": 60, "smoke": {"skip": true}
}'
# (wait ~2-3s so it's actually in waiting_health, loading child spawned)
curl -s -X POST -H "$AUTH" http://localhost:18100/admin/deployments/d4/cancel
curl -s -H "$AUTH" http://localhost:18100/admin/status   # -> "state":"idle", child not running
curl -s -H "$AUTH" "http://localhost:18100/admin/deployments/d4/events?after=0"
# -> last event: {"state":"cancelled","detail":{"phase":"post_stop"}}
# (phase is "pre_stop" instead if the old model was never actually touched)

# Cancel with nothing in flight / a stale id -> 409.
curl -s -w '\n%{http_code}\n' -X POST -H "$AUTH" http://localhost:18100/admin/deployments/nope/cancel

# Stop -- idempotent while idle, releases + persists last_run_outcome="stopped" while serving.
curl -s -X POST -H "$AUTH" http://localhost:18100/admin/stop
# -> {"state":"idle","vram":{"released":true|false,"used_mb":...,"total_mb":...}}
docker exec <container> cat /state/state.json   # -> "last_run_outcome": "stopped", last_good_argv still present

# Restart -- same argv (+env), new PID, back through the normal deploy pipeline (smoke included).
curl -s -X POST -H "$AUTH" http://localhost:18100/admin/restart
curl -s -H "$AUTH" http://localhost:18100/admin/status   # -> new "pid", same "argv"
```

### Verifying Stop survives a reboot (the P07 owner decision)

Needs the `/state` volume from the P02/P03a section above -- a fresh
in-process object is NOT a real enough test here either, same reasoning as
P05's restart-survival check.

```bash
docker volume create supervisor-state
docker run -d --name supervisor-dev --init -v supervisor-state:/state \
  -e SUPERVISOR_TOKEN=secret -p 18100:8000 supervisor-test
# ... deploy, then stop (commands above) ...
docker rm -f supervisor-dev
docker run -d --name supervisor-dev --init -v supervisor-state:/state \
  -e SUPERVISOR_TOKEN=secret -p 18100:8000 supervisor-test
docker logs supervisor-dev
# -> "boot decision -- replay=False (last run outcome was 'stopped', not healthy -- staying idle)"
curl -s -H "$AUTH" http://localhost:18100/admin/status   # -> "state":"idle", nothing replayed
```

### Smoke test (P06)

Auto-runs as the final step of every successful deploy unless the body
says `"smoke": {"skip": true}` -- omitted or `"smoke": null` both still run
it with the canned defaults (`"Reply with OK."`, expect `"OK"`, 30s
timeout). Reaches vLLM's own `/v1/chat/completions` directly -- never
through a proxy.

```bash
AUTH='Authorization: Bearer secret'

# Default smoke (omit the field) -- --served-model-name with no extra flags
# answers "OK" by default -> smoke passes, deploy still ends in "ready".
curl -s -H "$AUTH" -H 'Content-Type: application/json' -X POST http://localhost:18100/admin/deploy -d '{
  "argv": ["python3", "/app/fake_vllm.py", "--port", "9001", "--served-model-name", "model-a"],
  "deployment_id": "d1", "health_timeout": 15
}'
curl -s -H "$AUTH" http://localhost:18100/admin/status   # -> "state":"ready", "smoke_ok":true

# Failing smoke (warn-and-ready, owner-confirmed 2026-11-01): the deploy
# still reaches "ready" -- a bad canned-prompt answer never tears down a
# model that's actually up. Check smoke_ok and the "smoke" event, not the
# deploy's own outcome.
curl -s -H "$AUTH" -H 'Content-Type: application/json' -X POST http://localhost:18100/admin/deploy -d '{
  "argv": ["python3", "/app/fake_vllm.py", "--port", "9002", "--served-model-name", "model-b", "--fake-bad-output"],
  "deployment_id": "d2", "health_timeout": 15
}'
curl -s -H "$AUTH" http://localhost:18100/admin/status   # -> "state":"ready", "smoke_ok":false

# Explicit skip -- the only thing that turns smoke off.
curl -s -H "$AUTH" -H 'Content-Type: application/json' -X POST http://localhost:18100/admin/deploy -d '{
  "argv": ["python3", "/app/fake_vllm.py", "--port", "9003", "--served-model-name", "model-c"],
  "deployment_id": "d3", "health_timeout": 15, "smoke": {"skip": true}
}'
curl -s -H "$AUTH" http://localhost:18100/admin/status   # -> "smoke_ok":null (not run)

# On-demand, against whatever is currently serving -- 409 + {"state": ...} when idle/not ready.
curl -s -w '\n%{http_code}\n' -H "$AUTH" -X POST http://localhost:18100/admin/smoke-test

# Per-deployment smoke event, alongside the usual state events:
curl -s -H "$AUTH" "http://localhost:18100/admin/deployments/d1/events?after=0"
# -> last entry: {"state":"smoke","detail":{"outcome":"pass"}} (or
#    {"outcome":"fail","excerpt":"..."}) -- note this is an EVENT, not a
#    state-machine transition: runner.state (and /admin/status's "state")
#    stays "ready" through it, never flips to "smoke".
```
