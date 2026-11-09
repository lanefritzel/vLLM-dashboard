#!/usr/bin/env bash
# Control script for the isolated vis-test stack (see docker-compose.yml). It only ever touches
# the compose project "vis-test" (containers named vis-*); it never stops, removes or recreates
# anything else on the machine.
#
#   ./vis-stack.sh check            preflight: is the published port free, are the vis-* names free?
#   ./vis-stack.sh build            build the two images (CPU only; does not touch the GPU)
#   VIS_GPU_FREE=1 ./vis-stack.sh up   start the stack (needs the Intel GPU free: stop the live model first)
#   ./vis-stack.sh status | logs [service] | down [purge]
set -euo pipefail
cd "$(dirname "$0")"

PROJECT=vis-test
PORT="${VIS_DASHBOARD_PORT:-18080}"
COMPOSE=(docker compose -p "$PROJECT" -f docker-compose.yml)

listening_ports() { ss -ltnH 2>/dev/null | awk '{print $4}' | sed 's/.*://' | sort -un; }
published_ports() { docker ps --format '{{.Ports}}' 2>/dev/null | grep -oE '[0-9]+->' | tr -d '>-' | sort -un; }
existing_vis_containers() { docker ps -a --format '{{.Names}}' 2>/dev/null | grep -E '^vis-' || true; }

check() {
  local ok=0
  echo "Host port to publish: $PORT (dashboard UI)"
  if listening_ports | grep -qx "$PORT"; then
    echo "  COLLISION: something is already listening on host port $PORT"; ok=1
  elif published_ports | grep -qx "$PORT"; then
    echo "  COLLISION: a running container already publishes host port $PORT"; ok=1
  else
    echo "  free"
  fi
  echo "Ports used INSIDE the private namespace (8080 5432 18001 9998 9999): isolated from the host, no check needed."
  local mine; mine=$(existing_vis_containers)
  if [ -n "$mine" ] && ! "${COMPOSE[@]}" ps -q 2>/dev/null | grep -q .; then
    echo "  NOTE: vis-* containers exist that are not in project $PROJECT:"; echo "$mine" | sed 's/^/    /'
  fi
  return "$ok"
}

case "${1:-}" in
  check) check ;;
  build) "${COMPOSE[@]}" build ;;
  up)
    check || { echo "Refusing to start: fix the collision above (or set VIS_DASHBOARD_PORT)."; exit 1; }
    if [ "${VIS_GPU_FREE:-}" != "1" ]; then
      echo "Refusing to start: the supervisor needs the Intel GPU and the live model is probably using it."
      echo "Stop the live model, then run:  VIS_GPU_FREE=1 $0 up"; exit 1
    fi
    "${COMPOSE[@]}" up -d --build
    echo "Dashboard: http://$(hostname):$PORT   (register the first account; it becomes admin)"
    ;;
  status) "${COMPOSE[@]}" ps ;;
  logs) shift; "${COMPOSE[@]}" logs --tail 100 "$@" ;;
  down)
    if [ "${2:-}" = "purge" ]; then "${COMPOSE[@]}" down -v; else "${COMPOSE[@]}" down; fi
    ;;
  *) sed -n '2,12p' "$0"; exit 2 ;;
esac
