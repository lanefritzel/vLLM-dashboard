#!/usr/bin/env bash
# Runs the plugin tests in a throwaway local container (no GPU, nothing installed on the host).
# Needs a local image that has python + torch + vllm, e.g. your built supervisor image:
#   IMAGE=<that image> ./run-tests.sh
set -euo pipefail
: "${IMAGE:?set IMAGE to a local image with python, torch and vllm (for example your supervisor image)}"
SRC="$(cd "$(dirname "$0")" && pwd)"
# copy to local disk first: bind-mounting from sshfs breaks docker
TMP="$(mktemp -d "$HOME/vis-tests.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT
cp -r "$SRC"/. "$TMP"/
docker run --rm --entrypoint python3 -v "$TMP":/p:ro -w /p -e PYTHONPATH=/p "$IMAGE" \
  -m unittest discover -s tests -v
