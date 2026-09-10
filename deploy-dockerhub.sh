#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION_FILE="$SCRIPT_DIR/VERSION"
DOCKER_IMAGE="lanedfritz/vllm-dashboard"

# Read version from VERSION (source of truth) — bump this file before each release.
VERSION=$(tr -d '[:space:]' < "$VERSION_FILE")
if [[ -z "$VERSION" ]]; then
  echo "ERROR: $VERSION_FILE is empty"
  exit 1
fi
echo "Version: $VERSION"

echo "Building Docker image $DOCKER_IMAGE:$VERSION and $DOCKER_IMAGE:latest"
docker build \
  -f "$SCRIPT_DIR/deploy/Dockerfile" \
  -t "$DOCKER_IMAGE:latest" \
  -t "$DOCKER_IMAGE:$VERSION" \
  "$SCRIPT_DIR"

echo "Pushing $DOCKER_IMAGE:latest"
docker push "$DOCKER_IMAGE:latest"

echo "Pushing $DOCKER_IMAGE:$VERSION"
docker push "$DOCKER_IMAGE:$VERSION"

echo "Done. Deployed $DOCKER_IMAGE:$VERSION"
