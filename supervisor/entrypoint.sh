#!/bin/sh
# Thin by design (doc 5): replicate whatever env-setup the stock vLLM
# entrypoint does, then exec the supervisor. The stock-image delivery check
# (what the real entrypoint actually does, the Python path, whether tini is
# already present) is P00's inventory item deferred to P16a prep -- there is
# no load-bearing env-setup to replicate yet, so this stays a one-liner until
# that check comes back with something to add.
exec python3 /app/supervisor.py "$@"
