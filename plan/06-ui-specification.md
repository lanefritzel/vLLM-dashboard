# 06 — UI Specification

Blazor Server (`.razor` components, interactive SSR). No SPA; SignalR for live data.
Chart.js loaded from `wwwroot/lib` (vendored, no CDN — works on the LAN without
internet). All UI in `src/Dashboard.Web/Components`.

## Layout shell

```
┌──────────────────────────────────────────────────────────────┐
│ [logo] vLLM Dashboard   [Source ▾ selector]   [theme ▾] [user]│  <header, sticky>
├──────────┬───────────────────────────────────────────────────┤
│ Overview │                                                   │
│ History  │   <page content>                                  │
│ Models   │                                                   │
│ QuickTest│                                                   │
│ Settings │                                                   │
└──────────┴───────────────────────────────────────────────────┘
```

- Left nav (collapsible on narrow). Header right side: **theme switcher**
  (dark/light/system, inline, instant), user menu (sign out, my settings).
- **Source selector** (header): dropdown of active sources with a status dot
  (green=up, red=down, gray=disabled). Switching source → hub `JoinSource`, charts
  re-seed from the ring buffer, KPIs swap.
- Auth gate: unauthenticated users get `Login` + `Register` (register only when
  `HasUsers == false`, else shows "registration closed — contact admin");
  `pending` users get a "awaiting approval" screen.

## Pages

### 1. Overview (`/`)
- **KPI cards** (top row, 6): requests running, waiting, KV cache %,
  generated tok/s, prompt tok/s, requests/s. Each: big number + delta arrow.
- **Charts** (2×2 grid, Chart.js line, live):
  1. **Token throughput** — gen tok/s + prompt tok/s (2 series).
  2. **KV cache** — % (single line, y 0–100).
  3. **Scheduler** — running + waiting (2 series, integer).
  4. **Latency** — TTFT avg + E2E avg + queue avg (3 series, seconds).
- **Bonus strip** (small cards): prefix-cache hit %, preemptions (total + rate).
- For **Ollama sources**: KPI row = status, loaded models (names), VRAM used;
  charts area replaced by a "token metrics not exposed by Ollama" note. (Scaffold
  behavior; fine-grained Ollama UI is future work.)
- Live update: `samples` hub events → update charts (no full re-render; JS
  `chart.data.push/shift` via interop). Window selectable: 1 min / 5 min / 30 min
  (ring buffer holds 30 min; UI rate from config, default 1 s).

### 2. History (`/history`)
- Controls: date range (from/to), source (or "all"), model (or "all"),
  granularity (auto: raw <24 h, hourly beyond).
- Charts (static, from `IMetricsHistory`):
  1. **Throughput over time** — tokens/s (aggregated per bucket), stacked by model
     when "all".
  2. **Requests over time** — completed per bucket.
  3. **Latency percentiles** — P50/P95 (E2E) over time.
  4. **KV cache** — avg + max over time.
- **Summary table** (top): totals per model in range — requests, tokens generated,
  tokens prompt, avg TTFT, avg E2E, peak KV%.
- Data: recent window from `MetricsSamples` (raw); older from `MetricsRollups`
  (transparent to UI — `IMetricsHistory` decides).

### 3. Models (`/models`)
- Per selected source:
  - **Registry table**: name, display name, swap args, active badge; add/edit/delete
    (admin).
  - **Active model** card: current active + big **Hot-swap → [model]** control:
    pick a registry model → `POST /api/models/swap` → progress UI (steps: stop →
    pull (if needed) → start → health-check) → success/failure banner.
  - **Launch spec editor** (admin): image, model-arg template, ports, env, volumes,
    restart policy, GPU options, extra args. Save → `ILaunchSpecStore` + config
    event.
- Hot-swap is the only destructive action; confirm dialog required.

### 4. Quick test (`/quick-test`)
- Target = selected source's `inference_url` (direct backend) or, for Ollama,
  `inference_url/api/generate`.
- Fields: prompt (textarea), max_tokens (number), model (defaults to active,
  editable dropdown from catalog).
- **Send** → `IInferenceProbe` → shows returned text + latency ms + tokens used (if
  provided). Errors surfaced inline (e.g., "source unreachable", HTTP 4xx body).

### 5. Settings (`/settings`) — sections (tabs)
1. **Sources** — CRUD (label, type, metrics URL, inference URL, docker host,
   container name, interval, active toggle). "Test connection" button per source
   (calls `HealthAsync`).
2. **Preferences** — theme, default history window, UI update rate (applied to
   `Config` + broadcast).
3. **System** (admin) — retention days, rollups enabled, user list (approve/revoke
   pending, reset password, grant/revoke admin).

## Theming contract

- Single source of truth: CSS custom properties in `wwwroot/css/theme.css`.
  Two themes via `[data-theme="dark"]` / `[data-theme="light"]` on `<html>`;
  `system` resolves via `matchMedia` in JS.
- Tokens (minimum): `--bg`, `--card`, `--border`, `--text`, `--muted`, `--accent`,
  `--green`, `--red`, `--warn`, `--focus`, spacing/radius scales.
- `JS/theme.js`: `applyTheme(mode)` sets `data-theme`; `watchSystemTheme(cb)`.
- Per-user: `Users.ThemeMode` (DB); applied on login + on change; default dark.
- All components must use tokens only (no hardcoded colors) so themes swap cleanly.

## SignalR client contract (JS + C#)

- C#: `IHubConnection<MetricsHub>` established on app start (after auth);
  auto-reconnect with backoff; `JoinSource(sourceId)` on source switch/navigation.
- Events handled: `samples` (update charts/KPIs), `source-state` (status dots),
  `config-updated` (re-fetch `/api/config`, re-bind selectors), `model-changed`
  (Models page banner + KPI model label).
- JS interop surface (`wwwroot/js/charts.js`):
  - `chart_init(id, config)`, `chart_push(id, seriesName, point, maxPoints)`,
    `chart_setWindow(id, maxPoints)`, `chart_dispose(id)`.

## API endpoints (non-hub, authed JSON)

- `GET /api/config` → sources list + config keys (for selectors/settings hydration).
- `POST /api/sources`, `PUT /api/sources/{id}`, `DELETE /api/sources/{id}`,
  `POST /api/sources/{id}/test`.
- `POST /api/models`, `PUT /api/models/{id}`, `DELETE /api/models/{id}`,
  `POST /api/models/{id}/activate`, `POST /api/models/swap` (hot-swap, long-running).
- `PUT /api/launch-spec/{sourceId}`.
- `POST /api/probe` (quick-test).
- `GET /api/history?from=&to=&source=&model=` (server-rendered data for History).
- Identity endpoints: `POST /api/auth/register`, `POST /api/auth/approve/{userId}`,
  `POST /api/auth/reject/{userId}`, password reset (admin).

## Responsive & misc

- Breakpoint ~900 px: nav collapses to icons; KPI grid 4→2; charts stack 2×2→1.
- Number formatting: tabular-nums, `Intl.NumberFormat`; rates as `1,234 tok/s`;
  latency in ms when <1 s else s with 2 decimals.
- Empty states: no sources → onboarding card ("Add your first vLLM source in
  Settings"); source down → error card with last-known data faded + retry.
