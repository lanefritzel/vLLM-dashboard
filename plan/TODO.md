# TODO

Deferred ideas and follow-ups -- not scheduled, just tracked so they don't
get lost between sessions.

## Default the Instance admin port instead of requiring a full URL

Since the token-removal work (2026-11-04/05), the supervisor's admin API
only binds to `127.0.0.1`, which only works when the dashboard shares the
same network namespace (`network_mode: host`). That means the admin URL's
host part is now always `127.0.0.1` -- the only thing that actually varies
per deployment is the port, and that port is already set once, by hand, in
the compose file's `ADMIN_PORT` env var for the supervisor service.

Right now a human has to type that same port a second time into the
Add/Edit instance form's Supervisor URL field, with no link between the two
-- exactly the kind of "one fact living in two places" bug that caused the
`host.docker.internal` stale-URL issue during the 2026-11-05 p16a-test
rehearsal (the compose topology changed, nothing told the already-saved
instance row).

Proposed fix: pass the same port to the dashboard's own environment (e.g.
`DEFAULT_SUPERVISOR_PORT`, mirroring the supervisor's `ADMIN_PORT` in the
same compose file), and have the Add-instance form default its admin URL
field to `http://127.0.0.1:<that port>` instead of blank. Keep the field
editable for the rare multi-instance-different-ports case.

Explicitly not auto-discovered via the Docker socket -- that would require
mounting the Docker socket into the dashboard container, a much bigger
privilege grant than this is worth, and cuts against the whole point of
this session's hardening work.
