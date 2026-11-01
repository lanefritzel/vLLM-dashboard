#!/bin/sh
# Installed as /usr/local/bin/vllm in the test image (Dockerfile.test), so
# the supervisor's normalized "vllm serve <path> --flags..." actually runs
# something -- there's no real vllm CLI in a plain python:slim image. Drops
# the "serve <model-path>" positional prefix the real vLLM CLI expects (the
# fake vLLM test double only understands flags) and execs the fake.
if [ "$1" = "serve" ]; then
  shift
  case "$1" in
    --*) ;;  # no positional model path present -- nothing to drop
    *) shift ;;
  esac
fi
exec python3 /app/fake_vllm.py "$@"
