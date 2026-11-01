# Testing reference

Concrete commands/vars used while verifying each phase by hand, so a future
session doesn't have to re-derive them. `plan/PROGRESS.md` has the
per-phase narrative and findings; this is the copy-pasteable how-to.

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
