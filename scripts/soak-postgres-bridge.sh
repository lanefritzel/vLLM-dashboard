#!/usr/bin/env bash
# P01c: one-command, parameterised Postgres-over-bridge soak test.
#
# Usage: scripts/soak-postgres-bridge.sh [idle-minutes] [cycles]
#   Defaults match the phase page's spec: 3 cycles x 10 minute idle gaps.
#   For a fast sanity check during development: scripts/soak-postgres-bridge.sh 0.2 2
set -euo pipefail

idle_minutes="${1:-10}"
cycles="${2:-3}"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
compose_file="deploy/docker-compose.soak.yml"

cleanup() {
  docker compose -f "$compose_file" down -v >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker compose -f "$compose_file" build postgres-soak
docker compose -f "$compose_file" up -d postgres

echo "soak-postgres-bridge.sh: running ${cycles} cycle(s) x ${idle_minutes} minute idle (postgres reachable only over the bridge, no published port)..."
docker compose -f "$compose_file" run --rm postgres-soak --idle-minutes "$idle_minutes" --cycles "$cycles"
