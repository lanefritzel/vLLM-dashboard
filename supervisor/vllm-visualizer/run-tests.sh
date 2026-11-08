#!/usr/bin/env bash
# Runs the plugin tests in a throwaway local container (no GPU, nothing installed on the host).
# Needs a local image that has python + torch + vllm, e.g. the CPU POC image:
#   IMAGE=vllm-cpu-poc:v0.32.0 ./run-tests.sh
set -euo pipefail
IMAGE="${IMAGE:-vllm-cpu-poc:v0.32.0}"
SRC="$(cd "$(dirname "$0")" && pwd)"
# copy to local disk first: bind-mounting from sshfs breaks docker
TMP="$(mktemp -d "$HOME/vis-tests.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT
cp -r "$SRC"/. "$TMP"/
docker run --rm --entrypoint python3 -v "$TMP":/p:ro -w /p -e PYTHONPATH=/p "$IMAGE" \
  -m unittest discover -s tests -v
