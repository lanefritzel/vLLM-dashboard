# Live compose reference (as of P00, 2026-10-31)

Verbatim reference for the cutover, as given by the Build Plan's P00 page.
Note: this describes the **actual running setup on the live host**, which is
not the same as `deploy/docker-compose.yml` in this repo -- that file only
runs postgres + dashboard; the live vLLM container is created/recreated
dynamically by the dashboard's `DockerOrchestrator` (today's hot-swap
mechanism), not from a checked-in compose service. This file exists so P16a/
P16b have the real target to reproduce and then retire.

## vLLM service

- Image: `intel/llm-scaler-vllm:0.26.0-b2`
- `network_mode: host`
- `devices: /dev/dri`
- Extra volume: `/dev/dri/by-path`
- `ipc: host`
- `privileged: true`
- `entrypoint: ["vllm"]`
- Model mounted: `/home/lanef/hf-models/Qwen3.8-27B-INT4:/models/qwen3.8-int4`

`command`:

```
serve /models/qwen3.8-int4 --served-model-name qwen3.8-vllm --port 9999 --dtype float16 --enforce-eager --enable-mamba-cache-stochastic-rounding --mamba-ssm-cache-dtype float16 --gpu-memory-utilization 0.90 --max-model-len 131072 --kv-cache-dtype fp8_e4m3 --block-size 64 --max-num-batched-tokens 8192 --enable-auto-tool-choice --tool-call-parser qwen3_coder --reasoning-parser qwen3 --speculative-config '{"method":"mtp","num_speculative_tokens":4}'
```

env:
- `VLLM_ALLOW_LONG_MAX_MODEL_LEN=1`
- `VLLM_WORKER_MULTIPROC_METHOD=spawn`

## Postgres

- Postgres 16, host network, `listen_addresses=127.0.0.1`, volume on the
  external disk.
- **Rationale for host networking:** Postgres reached through Docker's
  published-port/NAT path once reset an idle pooled connection and corrupted
  the next request. (This is exactly what P01c's soak test is meant to
  re-verify before the new stack can rely on the bridge network instead.)

## Dashboard

- Ports: 8080 + HTTPS 8445 (cert mounted read-only).
- `DATAPROTECTION_KEYS_DIR=/data/dataprotection` on a named volume.
