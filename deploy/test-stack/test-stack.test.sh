#!/usr/bin/env bash
# Tests for docker-compose.yml and vis-stack.sh. No Docker daemon or real ports needed: `ss` and
# `docker` are replaced by stubs, and the compose file is checked as text.
set -uo pipefail
cd "$(dirname "$0")"
FAILS=0
ok()   { echo "ok   - $1"; }
fail() { echo "FAIL - $1"; FAILS=$((FAILS+1)); }
check() { if eval "$2"; then ok "$1"; else fail "$1"; fi; }

# ---- the compose file itself ------------------------------------------------------------------
C=docker-compose.yml
check "project is named vis-test"                         "grep -qx 'name: vis-test' $C"
check "no service uses the host network"                  "! grep -q 'network_mode: *\"\\?host' $C"
check "exactly one host port is published"                "[ \$(grep -cE '^\\s+- \"[^\"]*:[0-9]+\"\\s*(#.*)?\$' $C) -eq 1 ]"
check "the published port is the dashboard UI variable"   "grep -qE '\"\\\$\\{VIS_DASHBOARD_PORT:-18080\\}:8080\"' $C"
check "every container is named vis-*"                    "[ \$(grep -c 'container_name:' $C) -eq \$(grep -cE 'container_name: vis-' $C) ]"
check "no service restarts by itself (would grab the GPU after a reboot)" "[ \$(grep -c 'restart:' $C) -eq \$(grep -c 'restart: \"no\"' $C) ] && [ \$(grep -c 'restart:' $C) -eq 3 ]"
check "dashboard and postgres join the supervisor's namespace" "[ \$(grep -c 'network_mode: \"service:supervisor\"' $C) -eq 2 ]"
check "volumes are all prefixed vis-"                     "! sed -n '/^volumes:/,\$p' $C | grep -E '^  [a-z]' | grep -vqE '^  vis-' || ! sed -n '/^volumes:/,\$p' $C | grep -E '^  [a-z]' | grep -vE '^  vis-' | grep -q ."
check "default published port is not one the live stack uses" "! echo 18080 | grep -qxE '8080|18001|9999|9998|15432|5432'"
check "models mount is read-only"                         "[ \$(grep -c ':/models:ro' $C) -eq 2 ]"

# ---- vis-stack.sh against stubbed ss / docker -------------------------------------------------
STUB=$(mktemp -d); trap 'rm -rf "$STUB"' EXIT
CALLS="$STUB/docker-calls"; : > "$CALLS"
cat > "$STUB/ss" <<'S'
#!/usr/bin/env bash
cat "$STUB_SS" 2>/dev/null
S
cat > "$STUB/docker" <<'S'
#!/usr/bin/env bash
echo "docker $*" >> "$STUB_CALLS"
case "$1 $2" in
  "ps --format")    case "$3" in *Ports*) cat "$STUB_PORTS" 2>/dev/null;; esac ;;
  "ps -a")          cat "$STUB_NAMES" 2>/dev/null ;;
esac
exit 0
S
chmod +x "$STUB/ss" "$STUB/docker"
run() { # run <ss-lines> <ps-ports-lines> <names> <args...>; sets OUT and RC
  printf '%s' "$1" > "$STUB/ss.txt"; printf '%s' "$2" > "$STUB/ports.txt"; printf '%s' "$3" > "$STUB/names.txt"; shift 3
  : > "$CALLS"
  OUT=$(PATH="$STUB:$PATH" STUB_SS="$STUB/ss.txt" STUB_PORTS="$STUB/ports.txt" STUB_NAMES="$STUB/names.txt" STUB_CALLS="$CALLS" ./vis-stack.sh "$@" 2>&1); RC=$?
}
LIVE_SS=$'LISTEN 0 4096 0.0.0.0:8080 0.0.0.0:*\nLISTEN 0 4096 0.0.0.0:18001 0.0.0.0:*\nLISTEN 0 4096 0.0.0.0:9999 0.0.0.0:*\nLISTEN 0 4096 0.0.0.0:9998 0.0.0.0:*\nLISTEN 0 4096 127.0.0.1:15432 0.0.0.0:*\n'

run "$LIVE_SS" "" "" check
check "check passes next to the live llm-stack's host ports (8080/18001/9999/9998/15432)" "[ $RC -eq 0 ] && echo '$OUT' | grep -q 'free'"

run "$LIVE_SS"$'LISTEN 0 4096 0.0.0.0:18080 0.0.0.0:*\n' "" "" check
check "check fails when something already listens on 18080"          "[ $RC -eq 1 ] && echo '$OUT' | grep -q COLLISION"

run "$LIVE_SS" "0.0.0.0:18080->8080/tcp"$'\n' "" check
check "check fails when a container already publishes 18080"         "[ $RC -eq 1 ] && echo '$OUT' | grep -q 'publishes host port 18080'"

OUT=$(PATH="$STUB:$PATH" STUB_SS="$STUB/ss.txt" STUB_PORTS="$STUB/ports.txt" STUB_NAMES="$STUB/names.txt" STUB_CALLS="$CALLS" VIS_DASHBOARD_PORT=18090 ./vis-stack.sh check 2>&1); RC=$?
check "VIS_DASHBOARD_PORT moves the published port (18090 is checked, not 18080)" "[ $RC -eq 0 ] && echo '$OUT' | grep -q 'port to publish: 18090'"

run "$LIVE_SS" "" "" up
check "up refuses without VIS_GPU_FREE=1 and starts nothing"          "[ $RC -eq 1 ] && echo '$OUT' | grep -q 'needs the Intel GPU' && ! grep -q ' up ' '$CALLS'"

run $'LISTEN 0 4096 0.0.0.0:18080 0.0.0.0:*\n' "" "" up
check "up refuses on a port collision, even with the GPU confirmed"   "[ $RC -eq 1 ] && ! grep -q ' up ' '$CALLS'"

printf '%s' "$LIVE_SS" > "$STUB/ss.txt"; : > "$STUB/ports.txt"; : > "$STUB/names.txt"; : > "$CALLS"
OUT=$(PATH="$STUB:$PATH" STUB_SS="$STUB/ss.txt" STUB_PORTS="$STUB/ports.txt" STUB_NAMES="$STUB/names.txt" STUB_CALLS="$CALLS" VIS_GPU_FREE=1 ./vis-stack.sh up 2>&1); RC=$?
check "up with GPU confirmed starts only project vis-test"            "[ $RC -eq 0 ] && grep -q 'docker compose -p vis-test -f docker-compose.yml up -d --build' '$CALLS'"

run "$LIVE_SS" "" "" down
check "down only ever targets project vis-test"                        "[ $RC -eq 0 ] && grep -q 'compose -p vis-test .* down\$' '$CALLS' && ! grep -qE 'docker (stop|rm|kill)' '$CALLS'"

run "$LIVE_SS" "" "" down purge
check "down purge removes only that project's volumes (down -v)"       "grep -q 'compose -p vis-test .* down -v' '$CALLS'"

check "the script never stops, removes or kills containers directly"  "! grep -nE 'docker (stop|rm|kill|restart)' vis-stack.sh"

echo; [ $FAILS -eq 0 ] && echo "all passed" || echo "$FAILS failed"; exit $FAILS
