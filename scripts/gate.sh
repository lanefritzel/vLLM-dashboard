#!/usr/bin/env bash
# The honest gate: build -> unit tests -> smoke. One command, non-zero exit on
# any failure. Every later phase's Definition of done builds on this passing.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

section() { printf '\n==== %s ====\n' "$1"; }

section "build"
dotnet build

section "unit tests"
dotnet test

venv_dir="$repo_root/.venv"
if [ ! -d "$venv_dir" ]; then
  python3 -m venv "$venv_dir"
fi

section "smoke: fake-vllm scripted conversation"
"$venv_dir/bin/pip" install --quiet -r "$repo_root/tests/fake-vllm/requirements-test.txt"
"$venv_dir/bin/python" -m pytest "$repo_root/tests/fake-vllm" -v

section "supervisor unit tests"
"$venv_dir/bin/pip" install --quiet -r "$repo_root/supervisor/tests/requirements-test.txt"
"$venv_dir/bin/python" -m pytest "$repo_root/supervisor/tests" -v

printf '\ngate.sh: all sections passed.\n'
