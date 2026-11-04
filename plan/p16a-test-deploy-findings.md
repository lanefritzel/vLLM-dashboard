# P16a test deploy -- findings log

Running notes from the first real-hardware rehearsal on `tes.home`, using
`deploy/docker-compose.p16a-test.yml` (separate network/ports/DB from the
live `llm-stack` Portainer project -- see that file's own header comment for
the full topology). Owner-run per the Build Plan's own instruction for this
phase; I (Claude) gave commands one at a time and the owner ran them.

Each entry: what we hit, root cause, whether it blocked anything, and the
fix/status.

## 1. rsync target directory owned by root

`/media/external6tb/docker/vllm-dashboard/codebase` was `root:root` --
rsync failed with `Permission denied` on the first copy attempt. Owner
fixed ownership (`chown -R lanef:lanef`) themselves; not something I did
automatically (would touch a live host path without being asked).
**Blocked**: copy step, until fixed. **Status**: resolved, one-time.

## 2. Supervisor image build: wrong Docker build context

`deploy/docker-compose.p16a-test.yml` originally had the `supervisor`
service's build `context: ..` (repo root) with `dockerfile: supervisor/
Dockerfile`. But `supervisor/Dockerfile`'s `COPY entrypoint.sh`, `COPY
supervisor.py`, etc. are relative to the `supervisor/` directory itself,
not the repo root -- build failed with `"/requirements.txt": not found`.
**Fix**: changed to `context: ../supervisor`, `dockerfile: Dockerfile`.
**Status**: fixed in the compose file, committed to this repo (not yet --
see note at bottom).

## 3. Main browser profile silently dropped the Identity auth cookie

Registered the first user fine (became admin), but logging in bounced
straight back to the login page with no visible error. Traced via Network
tab: the login POST *did* get a 302 with a valid `Set-Cookie:
.AspNetCore.Identity.Application=...` (912 bytes, well under the 4096
limit, no blocked-cookie warning in Chrome's Cookies panel) -- but the
cookie never actually landed in the browser's cookie jar (confirmed via
Application > Cookies), so the very next request to `/` came back
unauthenticated and bounced to Login again.

Root cause: **not our app** -- confirmed by testing the same login in an
Incognito window, which worked immediately. The main profile has a large
number of other local services' cookies already on the bare `tes.home`
domain (cookies are host-scoped, not port-scoped, so they're shared across
every service on this box) plus several extensions (ProtonPass, etc.) --
one of those is almost certainly interfering with new cookie storage for
this origin. Not a bug to fix in the dashboard.
**Blocked**: manual verification, until worked around. **Status**: resolved
by using Incognito (or the owner can clean up/audit the main profile's
`tes.home` cookies/extensions later -- optional, not required for P16a).

## 4. GPU VRAM detection reports "generic (0 MB)" -- sysfs layout mismatch

`supervisor/supervisor/gpu_backend.py`'s `parse_xe_sysfs` reads
`/sys/class/drm/card0/device/mem_info_vram_used` (and `_total`), with a
PCI-BAR-size fallback. On this host, that path doesn't exist at all --
`/sys/class/drm/card0/device/` has no `mem_info_vram_*` files, and the
`tile0/gt0`, `tile0/gt1` subdirectories (this GPU exposes a multi-tile
sysfs layout) only have `freq0`, `gtidle`, `engines` -- no memory counters
anywhere in the tree. Confirmed from *inside* the privileged supervisor
container (sysfs is visible fine, `/dev/dri` + `privileged: true` isn't the
issue) via `find /sys/class/drm/card0/device/tile0 -iname '*vram*'` --
no results.

The code comment claims this was "confirmed against the owner's real Intel
host via... intel-gpu-monitor" -- either that was a different driver/kernel
version, or intel-gpu-monitor gets its numbers a different way (worth
checking `intel-gpu-monitor-web`'s own source, already running on this
host, next time this is picked up).

**Blocked**: nothing -- `gpu_backend.py`'s own docstring guarantees "a
missing/unavailable metric never fails a swap; only hard blocks (checked
dashboard-side) do." GPU vendor/VRAM just displays as generic/0 MB in the
UI for now. **Status**: deferred by owner's choice -- revisit after the
main swap rehearsal. Worth checking whether this also affects the
bfloat16-on-Intel block (P16a's own DoD bullet) -- that gate may key off
`vendor == "intel"` rather than the VRAM numbers specifically, in which
case it's unaffected; needs a read of wherever that block is implemented
before assuming either way.

---

**Note on `docker-compose.p16a-test.yml`**: this file lives in the repo
(not committed as of this entry -- it's new, untracked, part of ongoing
P16a work) and was copied to tes.home via `scp` after the fix in item 2.
If anyone picks this file up fresh from git, pull the fixed version, not
whatever may still be sitting in an old rsync on the host.

## 5. "Add model" (local path) Save button does nothing right after typing

`AddModelDialog.razor`'s "Weights path" input used plain `@bind="_localPath"`
(fires on blur/`onchange`), unlike `_name`/`_repoId` in the same dialog
which use `@bind:event="oninput"`. Clicking **Save** immediately after
typing the path (without first blurring the field, e.g. by clicking
elsewhere) left the button's `disabled="@(!CanSaveLocal || ...)"` evaluating
against a stale/empty `_localPath` server-side -- so the click did nothing,
no error, no network call. Same root-cause pattern already seen (and
worked around in test code) during P13's `@bind` blur-timing issue --
this is the first time it surfaced as a *real end-user-facing* bug rather
than only a Playwright test-timing wrinkle.

**Fix**: changed to `@bind:event="oninput"`, matching the other two fields
in this dialog. **Status**: fixed in source; not yet rebuilt/redeployed to
the tes.home test stack as of this entry -- workaround in the meantime is
clicking elsewhere in the dialog (blurring the field) before clicking Save.

## 6. "hf-cache only" model-path policy was too strict for real-world layouts

Hit immediately when trying the first real swap: saving a "local path"
model, then swapping, failed with "This model's path (/models/
Qwen3.8-27B-INT4) is not under this server's models_dir/hf-cache/
(/models/hf-cache/)." `CompatibilityPass.Check` required every model
(downloaded or hand-registered) to live specifically under
`<models_dir>/hf-cache/`, even though that convention only ever made sense
for the dashboard's own HF-download feature. The real production host's
actual model (and ours, mirroring it) sits directly under the models root,
no `hf-cache/` subfolder -- matching the shape a P01b-converted row has too
(a pre-existing test already documented this exact collision).

Discussed with the owner directly (not a quick unilateral fix -- this is a
real policy decision, not a path-math bug): symlinking to force everything
into `hf-cache/` was rejected as unacceptable end-user friction ("we can't
have end users/admin creating symlinks"). Decision: **a model just needs to
live somewhere under the server's mapped models directory, in whatever
structure the user already has** -- no required subfolder name. The
`hf-cache/` convention still exists, but only as where the dashboard's own
HF downloads default to; it's no longer enforced on everything else.

**Fix** (both pieces, since they're the same policy applied in two places):
- `CompatibilityPass.cs`: the model-path check now only requires the path
  be under `models_dir`, not `models_dir/hf-cache/` specifically. Renamed
  the issue code `model_path_outside_hf_cache` -> `model_path_outside_models_dir`.
- `supervisor/downloads.py`'s `dest_dir()`: new HF downloads now land at
  `<models_dir>/<slug>` directly, not `<models_dir>/hf-cache/<slug>`.
- Updated all tests asserting the old behavior (4 in C#, `test_admin_api.py`
  + `test_downloads.py` in Python) and doc comments referencing the old
  convention.

**Status**: fixed in source (both repos' tests green: 180/72 .NET, 34
Python); not yet rebuilt/redeployed to the tes.home test stack as of this
entry.

## 7. "New recipe" silently skipped model-path validation right after adding a model

Discovered while investigating #6: after registering the model and
clicking "+ New recipe" immediately, no warning showed about the hf-cache
issue at all -- it only appeared later when reopening the recipe to edit
it. Root cause: `ModelsRecipes.razor`'s per-model-aware validation is keyed
off a page-level "Serving model" dropdown (`_servingModelId`), not the
model the "New recipe" button was actually clicked on. That dropdown's
value is computed once, from the page's own model list, at initial page
load (`RefreshActiveDeploymentAsync`) -- on a brand-new database with zero
models at that moment, it's left at `0`. Adding a model afterward
(`OnModelSavedAsync`) refreshes the model list but never re-picks a valid
`_servingModelId`, so it stays `0` -- a nonexistent model id. The recipe
editor's validation call (`ValidateDraftAsync`) looks that id up, gets a
`KeyNotFoundException`, and that's silently swallowed (deliberately, so a
transient validation failure never blocks typing) -- so the user sees
nothing at all, not even a vague error.

**Fix**: `OnModelSavedAsync` now fixes up `_servingModelId` to the
just-saved model's id whenever the current value doesn't match any model
in the refreshed list.

**Status**: fixed in source; not yet rebuilt/redeployed to the tes.home
test stack as of this entry. Not covered by a new automated test (UI/
Razor page-state bug, no existing test harness exercises this exact
path -- worth a P16-follow-up E2E test: add a model then immediately open
"New recipe" on a fresh DB, assert the hf-cache-style warning shows without
a reload).

## 8. Pinned huggingface_hub version broke the base image's own transformers

First real swap attempt after fix #6/#7: `CompatibilityPass`'s model-path
check passed (confirming that fix), but the vLLM process itself crashed on
startup with `ImportError: cannot import name 'is_offline_mode' from
'huggingface_hub'` deep inside `transformers`' own import chain.

Root cause: `supervisor/requirements.txt` pinned `huggingface_hub>=0.26,
<0.27`, installed via `pip install -r requirements.txt` in
`supervisor/Dockerfile`. The base image (`intel/llm-scaler-vllm:
0.26.0-b2`) ships its own `transformers` + `huggingface_hub` versions that
are already matched to each other (Intel's own tested pairing) -- forcing
a narrower huggingface_hub range on top of that broke the pairing
`transformers` expected. The Dockerfile's own comment had flagged this
exact pin as "unverified against the real image" -- P16a's real-hardware
rehearsal is what finally verified it, and it failed.

The supervisor's own code only uses long-stable huggingface_hub APIs
(`HfApi`, `hf_hub_download`, `HfHubHTTPError`, `RepositoryNotFoundError`)
-- nothing needs a narrow version range.

**Fix**: dropped the version constraint in `requirements.txt` (now just
`huggingface_hub`, no range) -- `pip install` leaves an already-present,
already-compatible package alone when no version is specified, so this
install becomes a true no-op against whatever the base image already
ships. Updated the Dockerfile's doc comment to record this as confirmed,
not just suspected.

**Status**: fixed in source; not yet rebuilt/redeployed to the tes.home
test stack as of this entry.

## 9. Default health timeout (5 min) is razor-thin for a real 27B model's cold load

First real deploy attempt after fixes #6-#8 actually ran the real vLLM
process -- weights loaded, engine initialized, KV cache allocated, and the
API server printed "Supported tasks: ['generate']" (i.e. genuinely ready)
at roughly T+297s after spawn. But `SwapService.cs`'s hardcoded
`DefaultHealthTimeout = TimeSpan.FromMinutes(5)` (300s) had the supervisor
declare `health_timeout` failure and kill the child right around that same
moment -- confirmed the process was gone afterward (`curl .../health` on
the real inference port got a flat connection refused, not even a TCP
connect). Not a real crash; a timing race at the edge of an only-just-long-
enough deadline; cause: a 27B quantized model with a drafter/MTP model,
mamba cache, and two full safetensors-loading passes (one for the main
model, one for the drafter) genuinely takes a few minutes on a cold
filesystem cache -- this is the first time this path has been exercised
against a real model that size; every existing E2E test uses the fast
fake-vllm double, so this never showed up before.

**Not yet fixed** -- retrying the deploy first, since the model's weights
should now be in the OS page cache from this attempt (likely a much faster
second load). If it still times out close to the edge on a warm cache,
`DefaultHealthTimeout` needs to either go up (simple) or become
configurable per recipe/model (more correct -- a tiny model and a 27B MTP
model shouldn't share one hardcoded ceiling). Revisit after the retry
result.

**Resolution for #9**: bumped `DefaultHealthTimeout` from 5 to 15 minutes
as an immediate stopgap for this rehearsal. Owner's own observation during
testing: storage speed varies a lot in practice (this test's model loads
from an internal SSD; a removable/external/network drive could be far
slower), and model size/quantization varies too -- a single hardcoded
ceiling, however generous, is the wrong shape of fix long-term. **Tracked
as its own follow-up, not done now**: make health timeout a per-recipe
field (alongside max model length, GPU memory utilization, etc. in the
recipe editor), threaded through `ArgBuilder`/`SwapService`/the supervisor's
deploy request, with a sensible default for anyone who doesn't set it
explicitly.

## 10. Overview page: "When" column printed raw code instead of a formatted date

`Overview.razor`'s Recent swaps table: `@(d.StoppedAt ?? d.CreatedAt).ToLocalTime().ToString("MMM d, HH:mm")`.
Razor's explicit `@(...)` only evaluates what's inside the parens --
everything chained after the closing `)` is literal text, not code. The
page rendered the literal string `11/03/2026 16:58:05.ToLocalTime()
.ToString("MMM d, HH:mm")` instead of a formatted date. Confirmed this is
a pre-existing bug (not something touched in this phase) that's gone
unnoticed until someone actually looked at this table with real swap
history in it.

**Fix**: wrapped the whole chained expression in one `@(...)`:
`@((d.StoppedAt ?? d.CreatedAt).ToLocalTime().ToString("MMM d, HH:mm"))`.

## 11. Live metrics charts never rendered -- a one-shot chart-creation race

Discovered during manual verification: the real swap succeeded, the top
banner's live stats (serving model, uptime, req/s) updated correctly in
real time confirming the whole scrape -> SignalR -> Blazor pipeline
worked end to end -- but the Throughput (tok/s) and KV cache usage (%)
charts stayed permanently blank, even across a hard page reload, even
while sending real traffic through the proxy.

Root cause, confirmed via direct browser console testing (manually
calling `chart_create`/`chart_push` worked perfectly once the canvas
existed -- Chart.js, the canvas, and the push logic were never the
problem): `Overview.razor`'s `EnsureChartsAsync()` set
`_chartKey = sourceId` *before* confirming `chart_create` actually
succeeded for either canvas. `chart_create` returns early (and used to
return nothing at all, now returns `false`) if its target canvas isn't in
the DOM yet -- which can genuinely happen on the very first
`OnAfterRenderAsync` call right after the "Live metrics" card's own `@if`
block flips from hidden to visible, since JS interop can run before the
browser has finished reflecting that DOM change. Once `_chartKey` latched
to the "already done" value on that failed first attempt, the `if
(_chartKey == sourceId) return;` guard at the top of `EnsureChartsAsync`
permanently blocked ever retrying -- even though `OnAfterRenderAsync`
(which already re-invokes `EnsureChartsAsync` on every render, not just
the first) kept calling it on every subsequent render for the rest of the
page's life.

**Fix**: `chart_create` (in `charts.js`) now returns `true`/`false`
reporting whether it actually created the chart. `EnsureChartsAsync` only
latches `_chartKey` once *both* charts report success; otherwise it's left
unset so the next render (which happens continuously anyway, e.g. every
metrics-hub broadcast cycle, not just on a timer) retries automatically --
no new polling loop needed.

**Status**: fixed and confirmed on tes.home -- rebuilt the dashboard image,
redeployed, and the Throughput/KV cache charts now populate live. No
existing test caught this -- worth a follow-up E2E assertion that the live
metrics charts actually populate after a real swap (every existing
chart-related test likely only checks the cards/canvases exist, not that
data lands in them).

## 12. Overview page's "Ready" banner lied about system health after a failed swap

Discovered while deliberately testing DoD item "bad recipe fails cleanly
without looping" (see the end-user workflow doc's own "bad recipe" test):
duplicated the known-good recipe, added an invalid flag, swapped to it.
The supervisor correctly failed fast and cleanly (`state: failed`,
`exit_code: 2`, `reason: bad_args`, no retry loop -- confirms that DoD
item). But swapping back to the original (good) recipe found its own
"Swap" button disabled, and the Overview banner still said "Ready --
Serving: qwen3.8-vllm ... Uptime 1h 56m" -- even though the real
supervisor status (checked directly) showed the SAME "failed" state from
the bad-flag attempt, nothing running at all.

Root cause, two layered facts, both by design individually:
1. `SwapService`/the supervisor deliberately never flips the DB's "active
   deployment" row off the OLD (good) deployment unless a LATER swap
   actually *succeeds* -- a failed attempt leaves that bookkeeping alone,
   since it's still "what's supposed to be running."
2. The supervisor deliberately never auto-restarts anything after a failed
   deploy either (`deploy_runner.py`'s own comment: "no auto-restart, no
   auto-rollback -- fix the recipe and deploy is one model load, not
   two").

Combined, these mean a DB-active deployment row can point at a process
that's actually dead, with nothing wrong by either component's own
contract -- but `Overview.razor`'s main status banner trusted that DB row
at face value for display, with no live cross-check, so it confidently
reported a dead model as healthy and serving. The page already had
correct recovery UI for this exact situation ("Last run failed" + "Re-
deploy last working: ...") -- it was just unreachable, gated behind
`_activeDeployment is null`, which was never true here.

**Fix**: `Overview.razor` now calls `IBootReconciler.ReconcileInstanceAsync`
(the same live supervisor-vs-DB check the Instances page already uses)
alongside loading `_activeDeployment`, and only shows the confident "Ready"
banner when that reconcile agrees (`ReconcileStatus.Ok`). When the
supervisor is actually idle despite the DB saying active
(`ReconcileStatus.IdleVsActive`), the page now falls through to the
existing "no active model" UI, correctly surfacing the real failure and
offering "Re-deploy last working" to recover -- all pre-existing UI, now
actually reachable.

**Status**: fixed in source; not yet rebuilt/redeployed to the tes.home
test stack as of this entry.

## 13. "Re-deploy last working" was a silent no-op in exactly the scenario it exists for

Found immediately after fixing #12 and testing the recovery button it
surfaces: clicking "Re-deploy last working: qwen3.8-vllm ·
qwen3.8-vllm-recipe" appeared to do nothing at all -- no error, no
progress, nothing. Checked the real supervisor status directly: still the
same old `failed`/`bad_args` state from the earlier bad-recipe test,
completely untouched.

Root cause: `RedeployLastWorkingAsync` called `Swap.SwapAsync(...)` with
the "last working" model+recipe pair -- but in the IdleVsActive scenario
(finding #12's own case), that pair IS still the DB's current active
deployment (nothing ever flipped it off after the later bad swap failed).
`SwapAsync` has its own "same model+recipe already active -> no-op,
already_active" short-circuit, by design, for the ordinary case of
clicking Swap on something that's already running -- but here it fired
for exactly the wrong reason, silently discarding the recovery attempt
instead of actually relaunching anything. The interface already has the
right tool for this: `ISwapService.ForceRedeployAsync`, built specifically
as P12's "Fix" reconcile action for this exact DB-says-active-but-
supervisor-disagrees mismatch -- `RedeployLastWorkingAsync` just wasn't
using it.

**Fix**: `RedeployLastWorkingAsync` now calls `ForceRedeployAsync` when the
"last working" deployment is the same row as the DB's current active
deployment (the mismatch-recovery case); otherwise it still goes through
the normal `SwapAsync` path (the genuinely-idle, no-active-row-at-all
case, where the short-circuit was never the problem). Also added an
immediate busy/disabled state to the button itself -- separately raised
directly by the owner ("I can't tell if it's doing anything... we really
need better messaging to the user") while this bug made it look stuck;
even once fixed, the button gave no feedback during the request/response
round trip before the swap-ceremony dialog took over.

**Status**: fixed in source; not yet rebuilt/redeployed to the tes.home
test stack as of this entry.

## 14. Swap progress checklist never actually highlighted the current step

Owner's own observation while watching a real swap: the "Swapping..."
modal lists all 6 steps (Stop old process, Release GPU, Start new process,
Load weights, Health check, Smoke test) but gives no visual indication of
which one is actually happening.

Root cause: `SwapCeremonyDialog.razor`'s own `StepState()`/`StepCssClass()`
already correctly track real progress from live deployment events and
apply `step-done`/`step-current`/`step-failed`/`step-pending` CSS classes
per step -- the tracking logic was never the problem. There was simply no
CSS anywhere in the project defining what any of those classes look like,
so every `<li>` rendered identically regardless of its real, correctly-
computed state.

**Fix**: added `SwapCeremonyDialog.razor.css` (scoped component CSS, same
pattern already used for `NavMenu`/`MainLayout`/`ReconnectModal`) styling
each state: a filled dot + highlighted text for done/current, a pulsing
accent dot for the current step, red for failed, muted for pending --
using the same CSS custom properties (`--text`, `--muted`, `--green`,
`--accent`, `--red`, `--border`) the rest of the app (e.g. `charts.js`)
already draws from, so it matches dark/light theme automatically.

**Status**: fixed in source; not yet rebuilt/redeployed to the tes.home
test stack as of this entry.

## 15. Health-wait had no heartbeat, and log delivery had no pull-based fallback

Two related gaps surfaced from the owner directly watching a real swap and
asking "where did we go wrong, and how does a real supervisor report up":

**No heartbeat during the health wait.** `_wait_for_health`'s single
blocking call gave the UI nothing to show for however long it took (up to
~2m38s for the real 27B model) -- just a static pulsing dot. Fixed by
restructuring it into a loop of short (10s) sub-waits on the async side,
emitting a `waiting_health` heartbeat (with `elapsed_seconds`) between
sub-calls -- `_wait_for_health` itself still runs in a worker thread and
can't safely call `_record` directly (not thread-safe), so the heartbeat
is emitted back on the event loop between awaits instead. (First version
of this fix had a regression caught by the Python suite: a short
`health_timeout` could spuriously emit one extra heartbeat right before
the terminal "failed" event; fixed by only heartbeating when time actually
remains.)

**No pull-based fallback for logs.** Confirmed via live browser DevTools
(WebSocket Messages tab) during a real swap: only the 3 real `receiveState`
messages ever arrived plus SignalR keepalive pings -- zero `receiveLog`
messages the entire ~2.5-minute wait, despite the supervisor correctly
capturing and archiving every line the whole time. Root cause: log pushes
are "droppable" and never retried by design (so one slow network blip
never blocks the real state-machine events queued behind it) -- but unlike
state events (which have `GET /admin/deployments/{id}/events` as a
catch-up path), logs never had an equivalent "ask directly" fallback, even
though `GET /admin/deployments/{id}/logs` already existed for exactly this
use case.

Fix: added `ISupervisorClient.GetDeploymentLogsAsync` (wraps that existing
endpoint) and a new `PollLogsAsync` loop in `SwapCeremonyDialog.razor` that
asks the supervisor directly every 2s for this deployment's own archived
log tail, independent of push delivery -- same admin HTTP API the dialog
already calls for everything else, no new SignalR plumbing (SignalR's own
dashboard-to-browser leg was never the broken one; this fixes the
supervisor-to-dashboard leg, which was never SignalR in the first place).
The old push-based `OnLog` handler is now a no-op (kept, not removed, since
the JS side still calls it) rather than fighting the poll's full-tail
replace every 2s.

**Status**: fixed in source (Python: 127/127 tests pass; .NET: solution
builds clean, Core.Tests 180/180); not yet rebuilt/redeployed to the
tes.home test stack as of this entry.

## 16. ROOT CAUSE of #11: boot-replay's self-generated UUID deadlocked the entire event queue

Finding #11 was initially (wrongly) attributed to network flakiness under
GPU load. Added temporary diagnostic logging (supervisor-side: log the
actual exception/response on every failed push; dashboard-side: log every
received event) and reproduced it live -- the real cause is structural, not
environmental, and 100% reproducible:

`DeployRunner._begin` self-generates a UUID
(`deployment_id = deployment_id or str(uuid.uuid4())`) whenever no
`deployment_id` is given -- which is exactly what `supervisor.py`'s own
`run_boot_sequence` does when it auto-replays the last-known-good model on
container start (`deploy_runner.deploy_and_wait(argv, health_timeout=...)`,
no id passed, since a boot-replay isn't something the dashboard asked for
-- there's no `deployments.id` row for it to correlate with). That UUID
then gets pushed to the dashboard's `/internal/supervisor-events` endpoint,
whose receiver only accepts an integer id -- confirmed via the diagnostic's
captured response: `400 Bad Request` for
`deployment_id: "2d7a2834-e7f5-4cc8-abf8-8203ab94035d"`.

The fatal part: that "state" push is marked non-droppable, and
`EventPusher._run` is a strictly sequential, one-item-at-a-time queue that
never advances past a failing item -- a non-droppable item that keeps
failing is retried forever (exponential backoff capped at 30s, but
unlimited retries), permanently blocking every event queued behind it.
Since the queue is created once per supervisor process and this stuck item
sits at its head, **every subsequent real deploy's state and log events
for the rest of that process's lifetime silently never arrive** -- fully
explaining both finding #11 (logs never streaming) and a later symptom
(a real swap attempt appeared to succeed in the DB -- a deployment row
flipped to "active" -- while the owner correctly observed zero GPU/VRAM
usage, i.e. finding #12's DB-vs-reality mismatch recurring, caused by the
same root issue rather than being a new one).

This only started showing up today specifically because today involved
far more supervisor container restarts in a short window than any normal
usage pattern would -- and critically, by the time of these restarts,
`state.last_good_argv` was already set from earlier successful real
deploys, so AUTO_START="last_good" (the default) correctly triggered a
boot-replay on every subsequent restart, hitting this previously-latent
bug every time.

**Fix** (owner's explicit choice, after being offered "skip push for
boot-replay" / "harden the queue to never deadlock on any bad item" /
"both" -- chose the first, narrowest, most correct fix for this specific
cause): added `DeployRunner._dashboard_initiated: dict[str, bool]`,
recorded in `_begin` (true only when a real `deployment_id` was passed
in), and gated all three push call sites (`_record`, `_push_report`, the
log-batch push in `_run_log_batch`) behind a new `_should_push()` check.
A boot-replay still runs its full internal state machine normally
(`/admin/status`, `/admin/logs`, the archive -- all still work for it),
it simply never attempts to report up to a dashboard that never asked for
it and has nothing to correlate it with.

**Diagnostic logging disposition** (owner's explicit choice): kept a
short, permanent, loud log line on any push failure in `event_push.py`
(kind/seq/status or exception type -- cheap, genuinely useful if this
class of bug ever recurs) but removed the verbose body-dumping that was
only needed to root-cause this specific case. Removed the dashboard-side
per-event `Console.WriteLine` entirely (too noisy for permanent use, added
no information the fix above doesn't already make unnecessary).

**Status**: fixed in source, Python 127/127 passing, .NET builds clean;
not yet rebuilt/redeployed to the tes.home test stack as of this entry.
This is the single most significant finding of the P16a rehearsal --
without it, every future supervisor restart with a known-good model to
replay would silently and permanently break live progress/log reporting
for every subsequent real deploy, with no error ever surfaced anywhere.

## 17. THE ACTUAL ROOT CAUSE of #11: SupervisorPushEvent's naming-policy claim was never wired up

Finding #16's boot-replay UUID fix was real and worth keeping, but digging
further (owner's own question: "where did we go wrong, how does a real
supervisor report up correctly") surfaced a second, deeper, pre-existing
bug underneath it -- one that explains why finding #11 happened at all,
and strongly suggests live push has likely never worked correctly for any
deploy, not just under load or after a boot-replay.

`SupervisorPushEvent`'s own doc comment claimed it was "Deserialized with
JsonNamingPolicy.SnakeCaseLower" -- but nothing in `Program.cs` actually
configures that. The endpoint (`MapPost("/internal/supervisor-events",
...)`) binds its JSON body using ASP.NET's default minimal-API options
(camelCase-ish), which is a completely different code path from
`SupervisorClient`'s own explicit `SnakeCaseLower` options (which only
ever applied to that class's own *outbound* HTTP client calls reading
responses *from* the supervisor -- an entirely different direction).
Every field in the push payload (`seq`, `ts`, `kind`, `state`, `detail`)
happens to be a single lowercase word, so it coincidentally bound
correctly under any naming convention -- except `deployment_id`, the one
field with an underscore, which never matched the required `DeploymentId`
property under default camelCase binding. ASP.NET's minimal API
auto-validates required properties on JSON-bound parameters and returns
400 automatically when one is missing, **before the handler method is
ever invoked** -- confirmed by the complete absence of this endpoint's own
log output for every failed push, even before diagnostics were added.

This reframes finding #11 entirely: state events never actually arrived
live either -- they only ever *looked* like they worked because
`DeploymentCatchUpService`'s independent polling (`GET /admin/deployments/
{id}/events`) was silently backfilling them the whole time. Logs have no
equivalent catch-up path, so they were always completely and silently
missing -- not "under load", always, on every single deploy, since this
endpoint was first built.

**Fix**: added explicit `[JsonPropertyName("...")]` attributes to every
property of `SupervisorPushEvent`, pinning the wire contract directly to
this one model rather than relying on an app-wide naming policy that was
never actually configured for this binding path.

**Test gap, closed**: the existing `SupervisorEventReceiverTests.cs`
(Dashboard.Infrastructure.Tests) explicitly "exercises SupervisorEventReceiver
directly... rather than a full web host" -- it hands a hand-constructed
`SupervisorPushEvent` object straight to the handler, which thoroughly
tests the handler's own logic but can never exercise the real JSON
model-binding boundary where this bug actually lived. Added
`tests/Dashboard.E2E.Tests/SupervisorEventReceiverE2ETests.cs`: two tests
that POST the supervisor's real raw snake_case JSON text (a literal string,
never a C#-object-then-serialized payload) through the real minimal-API
route on a real running host. Verified these tests are genuinely
meaningful, not just trivially green: reverted the `[JsonPropertyName]`
fix, reran -- both failed with the exact real symptom
(`Assert.Equal() Failure: Expected: OK, Actual: BadRequest`) -- then
restored the fix and confirmed both pass again. Same class of gap as the
P15b `JsonContent.Create` naming-policy bug: a serialization boundary that
unit tests bypass by constructing the object directly instead of sending
real bytes over the wire.

**Status**: fixed in source and covered by a verified-meaningful
regression test. Python 127/127, .NET Core.Tests 180/180, the two new
E2E tests pass (2/2); full E2E suite not yet rerun. Not yet rebuilt/
redeployed to the tes.home test stack as of this entry.

**Finding #17 -- confirmed live on tes.home**: redeployed both fixes
together (boot-replay push-skip + the real `[JsonPropertyName]` fix) and
ran a full real swap end to end. Zero push failures logged. The
`deployment_events` table shows a complete, genuine real-time sequence --
`starting` -> `loading` -> `waiting_health` (heartbeats firing every ~10s
as designed, seq 3 through 48) -> `ready` (seq 50) -> `smoke` (seq 72) --
all delivered live via push, not backfilled by catch-up polling. The swap
itself completed successfully (owner-confirmed: "deployment of new model
complete"). This is the first time in the entire P16a rehearsal that the
full swap-ceremony dialog, live step highlighting, live log streaming, and
the underlying model swap have all worked correctly together, live, on
real hardware.

## 18. Swap progress steps: off-by-one marked the in-progress step "Done" immediately

Owner's own observation after finding #17's real fix was confirmed working
underneath (heartbeats genuinely firing every ~10s in the DB): the dialog
still visually looked like everything finished instantly, then just sat
waiting on "Smoke test" until the real model finished loading. A separate
bug from #14 (which only added the missing CSS) and from #17 (the push
delivery itself) -- this one is a pure logic error in `StepState()`.

`_maxSeenStepIndex` tracks "the step whose event we most recently saw" --
but the old comparison (`idx <= _maxSeenStepIndex` -> Done) marked that
step Done on its OWN first event, before it had actually finished, and
showed the NEXT step as "Current" instead -- so a long step (Health check,
re-heartbeating the same index for minutes) displayed as "already done,
waiting on Smoke test" for its entire real duration, even though the
heartbeats were correctly arriving the whole time underneath. The step
whose event just arrived is still the one IN PROGRESS; it should only
flip to Done once a LATER step's event arrives.

**Fix**: changed `StepState()` to treat `idx == _maxSeenStepIndex` as
Current (not Done) and `idx < _maxSeenStepIndex` as Done (not
`idx <= _maxSeenStepIndex`); failure detection shifted to match (the
CURRENT step is the one that failed, not the one after it). No existing
test needed updating -- `SwapCeremonyE2ETests`'s own assertion already
just waits for `.step-done, .step-current` generically.

**Status**: fixed in source, builds clean; not yet rebuilt/redeployed to
the tes.home test stack as of this entry.

## 19. Real weight-loading progress, derived from vLLM's own log output

Owner's follow-up to finding #18: once "Health check" correctly showed as
in-progress (with live elapsed-seconds heartbeats), the "Load weights"
step right before it was *still* flipping to Done the instant the process
spawned -- same root shape as #18, but for a step the supervisor genuinely
had no signal for at all, not just an off-by-one in existing data.

Investigated whether the supervisor could actually see weight-loading
progress rather than just relabeling/merging steps to hide the gap: yes --
it already captures 100% of the child's stdout/stderr (same buffer
`/admin/logs` reads from), it just never looks at the text for meaning.
Confirmed from two real deploys' own captured `log_tail` today that vLLM
reliably prints tqdm progress (`"Loading safetensors checkpoint shards:
50% Completed | 1/2 ..."`) and a completion line
(`"Model loading took 18.35 GiB memory and 127.981414 seconds"`).

**Fix**: new `DeployRunner._watch_weight_loading`, run between the
"loading" and "waiting_health" state transitions. Watches
`ProcessManager.logs()` (the same non-destructive ring-buffer read
`/admin/logs` already uses -- never a second pipe reader, never
interferes with `_run_log_batch`'s own destructive drain for push)
for the percentage pattern, emitting a real "loading" heartbeat with
`weights_pct` each time it changes (the dialog already renders arbitrary
heartbeat detail as text, so no dashboard-side change was needed for this
part). Exits on whichever comes first: the completion-line pattern, a
quick direct `/health` probe succeeding (so an already-healthy/fast-
loading model never waits around), the process dying, cancellation, or
(if genuinely nothing has matched at all within ~a couple seconds) a fast
bail-out.

**A real regression found and fixed before deploying this**: the first
version gave this phase a budget *shared with* the health-wait loop that
follows (subtracting elapsed time from `health_timeout`). Running the full
Python suite caught two real breaks: `test_slow_start_beyond_health_
timeout_...` started reporting the wrong failure reason
(`process_exited` instead of `health_timeout`) because the real
health-wait loop was left with zero budget to make its own determination,
and `test_cancel_is_responsive_even_with_a_long_health_timeout` failed
outright because `self._state` stayed stuck on "loading" past the test's
own polling window whenever nothing matched. Fixed by giving each phase
its own independent ceiling (capped at `health_timeout` each, not split
between them) plus a fast bail-out when there's no real signal at all --
re-ran the full suite, all 127 pass.

**Status**: fixed in source, Python 127/127 passing; not yet rebuilt/
redeployed to the tes.home test stack as of this entry. No dashboard-side
(.NET) changes needed -- the existing `_lastDetail` rendering already
displays whatever heartbeat detail arrives, including `weights_pct`.

## 20. Boot-replay used its own separate 30s health timeout, never bumped

Tested the boot/restart DoD item: restarted the supervisor container with
a real last-good model set. VRAM briefly loaded, then dropped back to 0%
-- the replay failed. `/admin/status` showed `reason: health_timeout`.
Root cause: `supervisor.py`'s own `BOOT_HEALTH_TIMEOUT_SECONDS` defaults
to 30s, a completely separate constant from `SwapService`'s
`DefaultHealthTimeout` (finding #9, already bumped to 15 min) -- the real
model needs minutes to load regardless of which code path started it, but
only the user-initiated path got fixed.

**Fix**: bumped `BOOT_HEALTH_TIMEOUT_SECONDS` default to 900s (15 min),
matching the dashboard-side stopgap. Same caveat as #9: a real fix is a
configurable per-model/recipe timeout, not a shared hardcoded constant --
tracked, not done here.

**Status**: fixed in source; not yet rebuilt/redeployed or retested live.

## 21. No avg tok/s rollup history for V2 instances (legacy had this)

`RollupService` only aggregates `MetricsSamples` rows with a non-null
`SourceId` (a GUID FK to the legacy `Sources` table). V2 instances are fed
through the same metrics pipeline using a synthetic non-GUID id
(`"instance-N"`), so `SampleStore.ToEntity` always stores `SourceId =
null` for them (`Guid.TryParse` fails) -- every V2 instance is silently
excluded from rollups/retention history, not just this one. Confirmed live:
no avg-tok/s history shows for the real instance, unlike the legacy page.
Real architecture gap (rollups are keyed to the legacy Source table),
not a quick fix. Not fixed in this phase.

## 22. Live metrics charts don't scroll off like legacy -- possibly a window-cap miscalculation

Owner's observation: the Throughput/KV cache charts scrunch all data into
the same area instead of scrolling the x-axis window like the legacy page
did. `Overview.razor`'s cap computation
(`(_windowMinutes * 60) / Math.Max(1, _instance.PollIntervalMs / 1000)`)
uses integer division on `PollIntervalMs` -- if the real poll interval is
sub-1000ms, this floors to 0 then clamps to 1, undercounting the real
scrape rate and capping the rolling window at far fewer real seconds than
the selected window label implies, which could look like this. Not
confirmed against the real instance's actual `PollIntervalMs` value yet --
flagged, not root-caused or fixed in this phase.
