# Test stack: try a build beside a live deployment

A throwaway copy of the whole stack (supervisor + Postgres + dashboard) that can run on a host
that already has a live dashboard, without colliding with it. Used to test new builds on real
hardware (Intel XPU and NVIDIA) before they reach a live install.

## Ground rules

- **Never touch running containers.** The live stack keeps running the whole time. Only the
  `vis-*` containers of project `vis-test` are ever started, stopped or removed.
- **Ports may need swapping.** Only the dashboard port is published (`VIS_DASHBOARD_PORT`,
  default `18080`). If another service on the host already uses it, `up` refuses; pick a free
  one, e.g. `VIS_DASHBOARD_PORT=18090 VIS_GPU_FREE=1 ./vis-stack.sh up`. If you change anything
  else to publish, check it against `ss -ltn` and the running containers' ports first.
- Work only inside one dev folder on the host, install nothing on the host, and ask before any
  other host action.

## How it avoids collisions

- Compose project `vis-test`; container names `vis-supervisor`, `vis-postgres`, `vis-dashboard`;
  images `vis-supervisor:test` / `vis-dashboard:test`; its own volumes and network.
- The supervisor owns a private network namespace. Postgres and the dashboard join it with
  `network_mode: "service:supervisor"`, so 8080, 5432, 18001, 9998 and 9999 exist only inside it.
- The only published port is the dashboard UI: `VIS_DASHBOARD_PORT` (default `18080`).
- Model weights are mounted read-only from `MODELS_DIR` (default `./models`).
- Database password `dashboard` is a throwaway value; Postgres is not reachable from the host.

## `vis-stack.sh`

| Command | What it does |
|---|---|
| `check` | Verify the dashboard port is free and no other project owns the names |
| `build` | Build both images |
| `up` | Check, build, start. Needs `VIS_GPU_FREE=1` (you confirm the GPU is not in use) |
| `status` / `logs [svc]` | Inspect |
| `down` | Stop and remove the project's containers (volumes kept) |
| `down purge` | Same, plus the project's volumes (fresh database next time) |

It only ever runs `docker compose` against project `vis-test`; it never stops, removes or kills
anything else. `test-stack.test.sh` checks those properties with a stubbed `docker`/`ss`.

## Procedure used (tes.home, Intel; same shape on serenity, NVIDIA)

Work only inside one dev folder on the host, e.g. `/media/external6tb/docker-dev/vllm-visualizer`.
Install nothing on the host; everything runs in containers.

1. **Copy the source up** (from the repo root, over ssh):
   - dashboard: `tar c --exclude=bin --exclude=obj --exclude=node_modules src deploy/Dockerfile | ssh HOST "tar x -C $DEV/dashboard-src"`
   - supervisor: `tar c -C supervisor --exclude=__pycache__ --exclude=.pytest_cache . | ssh HOST "tar x -C $DEV/supervisor-src"`
   - this folder: copy `docker-compose.yml`, `vis-stack.sh` and `test-stack.test.sh` into `$DEV`.
   - Note the git commit in a `STAGED.txt` so you know what is deployed.
2. **Pick the vendor**: the compose file builds `dashboard` from `./dashboard-src` and
   `supervisor` from `./supervisor-src`, and defaults to the Intel base image
   (`BASE_IMAGE=intel/llm-scaler-vllm:0.26.0-b2`, device `/dev/dri`). For NVIDIA set
   `BASE_IMAGE=vllm/vllm-openai:v0.33.0` and replace the `/dev/dri` device with a GPU request.
   Set `MODELS_DIR` to the host's weights folder.
3. **Run the unit checks**: `./test-stack.test.sh` (20 checks, no Docker needed).
4. **Free the GPU**: stop/unload the live model (the live containers can stay running).
5. **Start**: `VIS_GPU_FREE=1 ./vis-stack.sh up`, then open `http://HOST:18080`. The first account
   registered becomes admin; the database is empty, so re-add the model and recipe.
6. **Check**: swap steps follow real phases (starting, loading with shard %, warming up), the
   neuron map shows activity (needs Enforce eager on), tok/s is sensible.
7. **Tear down**: `./vis-stack.sh down purge`, then delete the dev folder's staged source
   (and `docker image rm vis-supervisor:test vis-dashboard:test` to reclaim disk).

## Gotchas learned

- Cold model loads are disk-bound (HDD ~55 MB/s); a repeat load is seconds once cached.
- The neuron map is blank with CUDA graphs / compiled code; use Enforce eager, or set
  `VIS_ENABLED=0` (Environment vars, not Extra args) on speed recipes.
- Agentic tok/s is lower than a plain essay (long context, prefill per tool call).
- A harmless `BrokenPipeError` from the plugin's HTTP tap can appear in logs when a poll hangs up.
