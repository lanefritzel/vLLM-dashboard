# End-user setup workflow (captured live during P16a)

Step-by-step record of what an end user actually does, screen by screen, to
go from a brand-new dashboard install to a running model -- captured while
running the P16a test deploy on tes.home. Intent: once this is complete and
working, it becomes the source material for the real onboarding docs/
Help page copy (and flags anywhere the UX is confusing or needs more
explanation -- feeds the UX refactor planned after P16).

Each step: what the user did, what they saw, anything confusing or
worth calling out.

## 1. Register the first user

- Navigate to the app root -> redirected to `/Account/Login`.
- Click **Register**, fill in email + password.
- First user on a fresh database becomes admin automatically (no visible
  confirmation of this on the register page itself -- you just end up
  signed in as admin after).

## 2. Land on Overview -- onboarding checklist

- Fresh database shows a "Get started" checklist: Servers -> Models &
  Recipes -> Overview.

## 3. Servers page -- add a server

- `/servers` -> **+ Add server**.
- Fields: Label, Models directory (defaults to `/models`), GPU name, GPU
  count, GPU memory (GB).
- **Docker host** field is on the server, not asked about up front --
  found via a separate "Edit" pass. It's explicitly optional ("for Start/
  Hard-reset only" placeholder) -- only needed if you want the dashboard
  itself to be able to start/hard-reset the supervisor container via the
  Docker API. Skipped for this test (managing the container manually).

## 4. Servers page -- add an instance

- On the same server card, **+ Add instance**.
- Fields: Label, Proxy slug, vLLM port (default 9999), Container name
  (optional, same Start/Hard-reset-only caveat as Docker host), Poll
  interval (ms, default 1000), Supervisor URL, Token.
- **Test connection** button validates the URL+token live against the
  supervisor's `/admin/health` + `/admin/status` before you can trust it --
  shows a green summary ("Connected. GPU: ... - supervisor state: ...") or
  a red error (e.g. wrong token) inline, no page reload needed.
- Click Save once Test connection succeeds.

## 5. Models & Recipes -- add a model (local path)

- `/models` -> **+ Add model**.
- Source dropdown defaults to "Hugging Face" -- switch to **Local path** to
  register a model already downloaded on disk (not via the dashboard's own
  HF download flow). Fields then become: Name, Weights path (on the
  server's disk, e.g. `/models/<subdir>` under the server's configured
  models directory).
- Hit a real bug here: the "Weights path" field didn't register text typed
  into it fast enough for the Save button's enabled state on the first
  click -- had to click Save twice (first click "catches up" the field,
  second actually saves). Fixed in source (see
  `p16a-test-deploy-findings.md` #5); not yet redeployed to this test
  stack.

## 6. Models & Recipes -- add a recipe

- On the registered model, **+ New recipe**.
- Form is grouped: Identity (name, description, served model name --
  pre-filled from the instance's currently-active served name or "main"),
  Sizing & memory (max model length, GPU memory utilization, KV cache
  dtype, data type), Performance (enforce eager, max batched tokens, block
  size, speculative decoding + its num_speculative_tokens), Tools &
  reasoning (auto tool choice, tool call parser, reasoning parser),
  Advanced (free-form Extra args key/value rows + Environment vars
  key/value rows, "+ Row" to add more).
- The dtype field has a live inline warning: "On the Intel stack, bfloat16
  is blocked and auto warns -- the reason shows above if so." Confirms the
  bfloat16-on-Intel gate is a real, working UI-level check (not dependent
  on the GPU-vendor sysfs detection gap in finding #4 -- this reads the
  recipe's own dtype choice against the server's configured GPU, not live
  hardware query).
- Flags not covered by a named field (e.g. `--enable-mamba-cache-
  stochastic-rounding`, `--mamba-ssm-cache-dtype float16`) go in "Extra
  args" as key (no leading `--`) / value rows -- blank value means a
  bare flag.
- Reproduced the real production recipe
  (`plan/live-compose-reference.md`'s vLLM command) entirely through named
  fields plus two Extra args rows -- no raw argv editing needed.

## 7. Swap -- the first real attempt failed, and what it revealed

- Overview page -> "Pick a recipe" -> review-changes modal shows a clean
  Current -> New diff of every field the swap will change -- confirmed
  correct against the real production recipe before clicking through.
- First "Swap now" click failed immediately with a path error (see
  `p16a-test-deploy-findings.md` #6): the model wasn't registered under a
  required `hf-cache` subfolder. This led to a real design conversation
  (see #6/#7 in the findings doc) and two source fixes -- not yet
  redeployed as of this entry, so the actual successful swap hasn't
  happened yet in this session.
- **End-user takeaway for the Help rewrite**: registering a model via
  "Local path" should accept the model wherever it actually lives under
  the server's configured models directory -- no particular subfolder
  structure required. Downloads done through the dashboard's own HF
  download button land in a `hf-cache/` folder under that same directory
  by default, but that's just where new downloads go, not a requirement
  for anything registered by hand.

## 8. First real end-to-end success

- Retried swap succeeded (second attempt, after the model's weights were
  already in the OS page cache from the first attempt).
- Real inference confirmed through the proxy: a genuine chat completion
  came back with correct `model` field, reasoning trace, and real token
  usage counts.
- Live metrics charts (Throughput tok/s, KV cache usage %) initially
  stayed blank despite everything else working -- traced to a one-shot
  chart-creation race (see findings doc #11), fixed and confirmed working
  after rebuild.
- Also fixed in this pass: the Recent swaps table's "When" column was
  printing literal code instead of a formatted date (findings doc #10).
