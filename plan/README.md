# vLLM Dashboard — Plan (read this first)

A self-contained plan folder for building a **Blazor (Server) + SignalR** monitoring
dashboard for **vLLM** model servers, in **.NET 10**, containerized with Docker,
backed by **PostgreSQL** for auth + historical metrics.

This folder is written for a **fresh session**: every requirement, decision, and
open question from the design discussions is captured here. Work top to bottom:

| File | Contents |
|---|---|
| `01-requirements.md` | Functional + non-functional requirements, explicit non-goals |
| `02-architecture.md` | Stack, solution layout, dependency rules, runtime data flow, services |
| `03-design-patterns.md` | Which patterns are used where, and which were rejected (and why) |
| `04-data-model.md` | Entities, Postgres tables, EF Core notes, retention/rollup design |
| `05-protocols-and-config.md` | vLLM metric name mapping (verified live), Ollama API, config store, Docker hot-swap protocol |
| `06-ui-specification.md` | Pages, components, theming contract, SignalR client contract, charts |
| `07-milestones.md` | Build order with per-milestone tasks, deliverables, and verification |
| `08-open-issues-and-risks.md` | Unresolved items, risks, and how each is mitigated |
| `09-environment-verification-log.md` | Raw read-only probe results of the real hosts (ground truth) |

## Project root

`/home/lanef/appdevelopment/application_development/vLLM-dashboard`

Pre-existing, to be **deleted** once milestone 1 lands (superseded Flask draft):
`app.py`, `static/`.

## Decisions at a glance (all confirmed with the user)

| Area | Decision |
|---|---|
| Framework | .NET 10 Blazor Server (SDK 10.0.300 installed) + SignalR |
| DB | PostgreSQL 16 (containerized, internal :5432); Identity + config + metrics all in PG |
| Dashboard port | 8080 (published) |
| Auth | ASP.NET Identity in Postgres; first-run self-register; **first user = admin**; later signups need admin approval |
| Theming | CSS custom properties; dark (default) / light / system-follow; per-user, persisted |
| Multi-instance | First-class: any number of vLLM + Ollama sources; UI source selector |
| Primary source | `tes.home:9999` — real intel/vllm 0.21.1.dev0, model `qwen3.8-vllm` (verified live) |
| Mock-first | `MockMetricsSource` so the full pipeline builds/runs before real wiring; one config flip to go live |
| History | Raw samples 30 days, then permanent **hourly rollups**; History page with date range + per source/model |
| Hot-swap | Real: DB-backed **model registry** + per-source **launch spec**; Docker Engine API over `tcp://` (`DOCKER_HOST`); stop/recreate backend container with substituted model arg, health-wait `/v1/models`, flip active model |
| Docker API | Enabled on all machines (user decision); expose `tcp://0.0.0.0:2375` (TLS 2376 preferred) |
| Ollama | Scaffold-only in MVP: `OllamaSource` (catalog `/api/tags`, status `/api/version`, loaded+VRAM `/api/ps`, probe `/api/generate`). serenity.home:11434 confirmed running (gemma4:latest) |
| llama-swap | Stays (permanent for now). It is **routing only** (no swapping). Dashboard does NOT talk to the router for metrics; metrics + quick-test go to the **direct backend** (`tes:9999`) |
| Quick-test target | Direct backend port (per-source `inference_url`), not the llama-swap router |
| Config | Everything configurable in-app (Settings page); stored in Postgres; applied at runtime without restart |

## How to resume a build session

1. Read `README.md`, then `01`–`09` in order (they are short).
2. Start at the first milestone in `07-milestones.md` that is not `done`.
3. Every decision above is final unless the user re-opens it in a new session; if a new
   session discovers a contradiction, log it in `08-open-issues-and-risks.md`.
4. Conventions: SOLID throughout; dependency direction
   `Web → Application → Core ← Infrastructure`; no comments in code unless asked;
   verify with `dotnet build` + tests at the end of every milestone.

## Repo layout (target, after build)

```
Dashboard.sln
src/
  Dashboard.Core/            # models + abstractions only, zero project deps
  Dashboard.Infrastructure/  # scrapers, EF Core (Npgsql), Docker API client
  Dashboard.Application/     # hosted services, SignalR hub, config store
  Dashboard.Web/             # Blazor Server app (wwwroot, components, hub client)
tests/
  Dashboard.Core.Tests/
  Dashboard.Web.Tests/
deploy/
  Dockerfile
  docker-compose.yml
  .env.example
```
