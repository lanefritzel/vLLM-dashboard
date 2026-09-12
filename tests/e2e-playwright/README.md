# End-to-end UI tests (Playwright)

Manual/agent-driven browser verification for flows that are impractical to unit test
(multi-step wizards, live Docker/Hugging Face calls, cross-page navigation). These are
**not** run in CI — they're run ad hoc against a local dev instance while building or
reviewing a feature.

## Setup

1. A local Postgres test database, e.g.:
   ```
   docker run -d --name vllm-dash-pgtest -p 5432:5432 \
     -e POSTGRES_DB=dashboard -e POSTGRES_USER=dashboard -e POSTGRES_PASSWORD=dashboard \
     postgres:16
   ```
2. Run the app against it:
   ```
   dotnet run --project src/Dashboard.Web/Dashboard.Web.csproj
   ```
   (uses `src/Dashboard.Web/Properties/launchSettings.json`'s `ConnectionStrings__DefaultConnection`,
   which already points at `localhost:5432/dashboard`.)
3. A test login user must exist with a known password. The scripts here expect
   `e2etemp@test.local` — set its password hash directly via `psql` if needed (see
   `common.py`'s `LOGIN_EMAIL`/`LOGIN_PASSWORD` for the expected credentials), or swap
   in your own via env vars.
4. Python Playwright: `pip install playwright && playwright install chromium` (already
   set up in this environment as of this writing).

## Running

```
python3 tests/e2e-playwright/test_physical_servers.py
```

Each script logs in, drives the browser headlessly, prints assertions/observations to
stdout, and saves screenshots to `tests/e2e-playwright/.shots/<script-name>/` (gitignored).
Scripts clean up any test data they create — if one fails partway, check the database
for leftover rows before re-running.

## Files

- `common.py` — shared login helper, base URL, screenshot helper. Import this from new
  scripts rather than duplicating the login flow.
- `test_wizards_entrypoints.py` — Server/Model/Full Setup Wizard default-entry-point
  behavior and the "Switch to Advanced" hand-off.
- `test_physical_servers.py` — Physical Server CRUD, the vLLM instance wizard's
  pick-or-add-server step, and VRAM-aware Model wizard suggestions.
