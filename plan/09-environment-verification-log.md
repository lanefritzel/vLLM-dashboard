# 09 — Environment Verification Log

Raw results of read-only probes run from the build machine
(`192.168.0.x` LAN, mDNS `.home` resolution) on **2026-09-08**. These are the
ground truth the plan was written against. Re-verify if behavior seems off.

## 1. DNS

```
tes.home      -> 192.168.0.170
serenity.home -> 192.168.0.34
lf.home       -> 192.168.0.117
```

## 2. Port reachability (TCP connect)

| host:port | result |
|---|---|
| tes.home:8000 | OPEN |
| tes.home:5000 | OPEN |
| tes.home:8888 | OPEN |
| tes.home:9999 | OPEN |
| tes.home:2375 | **closed/filtered** (Docker API not yet exposed — see 08/A4) |
| tes.home: 8001/8080/8002/3000/80 | closed |
| serenity.home:11434 | OPEN |
| lf.home:8000 | OPEN |
| lf.home:3000 | OPEN |
| lf.home: 8001/8080/8002 | closed |

## 3. tes.home:9999 — THE REAL VLLM BACKEND (primary source)

```
GET /health        -> 200 OK (empty body)
GET /version       -> 200 {"version":"0.21.1.dev0+gad7125a43.d20260810"}
GET /v1/models     -> 200 {"object":"list","data":[{"id":"qwen3.8-vllm","object":"model",
                          "created":1788911386,"owned_by":"vllm","root":"/models/qw...
GET /metrics        -> 200 (Prometheus text, full vllm_* surface)
```
- Server: **intel/vllm** (Intel fork of vLLM), source build tag `0.0.0-src` on the
  router side but `/version` on 9999 reports the real vLLM version string.
- Model: `qwen3.8-vllm` (root `/models/qw...`).
- All metric series carry labels `engine="0"`, `model_name="qwen3.8-vllm"`.

### Verified vllm_* metric types (from `# TYPE vllm:*` lines)

```
vllm:estimated_flops_per_gpu_total        counter
vllm:estimated_read_bytes_per_gpu_total   counter
vllm:estimated_write_bytes_per_gpu_total  counter
vllm:num_requests_running                 gauge
vllm:num_requests_waiting                 gauge
vllm:num_requests_waiting_by_reason       gauge
vllm:engine_sleep_state                   gauge
vllm:kv_cache_usage_perc                  gauge
vllm:prefix_cache_queries_total           counter
vllm:prefix_cache_hits_total              counter
vllm:external_prefix_cache_queries_total  counter
vllm:external_prefix_cache_hits_total     counter
vllm:mm_cache_queries_total               counter
vllm:mm_cache_hits_total                  counter
vllm:num_preemptions_total                counter
vllm:prompt_tokens_total                  counter
vllm:prompt_tokens_by_source_total        counter
vllm:prompt_tokens_cached_total           counter
vllm:generation_tokens_total              counter
vllm:request_success_total                counter
vllm:request_prompt_tokens                histogram
vllm:request_generation_tokens            histogram
vllm:iteration_tokens_total               histogram
vllm:request_max_num_generation_tokens    histogram
vllm:request_params_n                     histogram
vllm:request_params_max_tokens            histogram
vllm:time_to_first_token_seconds          histogram
vllm:inter_token_latency_seconds          histogram
vllm:request_time_per_output_token_seconds histogram
vllm:e2e_request_latency_seconds          histogram
vllm:request_queue_time_seconds           histogram
vllm:request_inference_time_seconds       histogram
vllm:request_prefill_time_seconds         histogram
vllm:request_decode_time_seconds          histogram
vllm:request_prefill_kv_computed_tokens   histogram
vllm:cache_config_info                    gauge
```

### Sample series (verbatim)

```
vllm:num_requests_running{engine="0",model_name="qwen3.8-vllm"} 0.0
vllm:num_requests_waiting{engine="0",model_name="qwen3.8-vllm"} 0.0
vllm:prompt_tokens_total{engine="0",model_name="qwen3.8-vllm"} 4.763604e+06
vllm:generation_tokens_total{engine="0",model_name="qwen3.8-vllm"} 446362.0
vllm:time_to_first_token_seconds_count{engine="0",model_name="qwen3.8-vllm"} 206.0
vllm:e2e_request_latency_seconds_count{engine="0",model_name="qwen3.8-vllm"} 202.0
vllm:request_queue_time_seconds_count{engine="0",model_name="qwen3.8-vllm"} 202.0
```

NOTE: at probe time there was **no** `vllm:gpu_cache_usage_avg` on this server —
`vllm:kv_cache_usage_perc` is the live KV metric. The aggregator must prefer
`kv_cache_usage_perc` (0–100) and only fall back to `gpu_cache_usage_avg` (0–1)
for older vLLM. (The Flask draft assumed the older name — superseded.)

## 4. tes.home:8000 — llama-swap ROUTER (not a source)

```
GET /health     -> 200 OK
GET /version    -> 200 0.0.0-src
GET /v1/models  -> 404 "Not found"
GET /metrics    -> 404 "Not found"
GET /docs       -> 404
```
Behavior: llama.cpp-style endpoints; routes inference; **no metrics, no model list**.
Dashboard bypasses it (metrics + probe go to 9999). Stays in user's stack (permanent
for now). If the user later wants the dashboard to replace its routing role →
separate feature (NG1).

## 5. tes.home:5000 — separate app (out of scope)

```
GET /        -> 200 (Vite HTML page)
GET /api...  -> 400 {"error":{"code":-32000,"message":"Bad Request: Mcp-Session-Id
                     header is required..."}}   (JSON-RPC/MCP server)
GET /health  -> 200 "Healthy"
```
A Vite SPA + MCP API. Unrelated to vLLM. Not a dashboard source.

## 6. tes.home:8888 — Go server (out of scope)

Every path probed returned the Go default `404 page not found` (e.g. `/`, `/api`,
`/health`). Likely llama-swap's control/UI plane or another Go service. No
dashboard role at MVP. (Re-investigate only if the user says it matters.)

## 7. serenity.home:11434 — OLLAMA (confirmed)

```
GET /api/tags -> 200 {"models":[{"name":"gemma4:latest","model":"gemma4:latest",
          "modified_at":"2026-08-21T13:09:13.171868571Z","size":9608350718,
          "digest":"c6eb3a7d6d91d5826685c8905c3b268e3f11e2d643c075b0b0f5b45d0f2a1c3e",
          "details":{"parent_model":"","format":"gguf","family":"gemma4",
          "families":["gemma4"],"parameter_size":...
```
Standard Ollama REST API. Will be migrated to vLLM (see 08/A6). Scaffold source for
the MVP.

## 8. lf.home — future option (out of scope at MVP)

```
GET :8000/v1/models -> 404 (some server, not a standard vLLM surface)
GET :3000/          -> 200 (web app; CSP references kb.home — a "knowledge base" app)
```
Small machine; may receive vLLM later. Multi-instance design makes this additive
(add a source when it exists).

## 9. Build machine (this box)

- OS: Linux, Python 3.12.3 (legacy only — Flask draft being deleted).
- **.NET SDK 10.0.300** at `/home/lanef/.dotnet10/sdk` (confirmed `dotnet --list-sdks`).
- **Docker 29.8.0**, **Compose v5.5.1** (confirmed).
- No vLLM installed locally (not required — dashboard is a client/scraper).
- Pre-existing files to delete in M1: `app.py`, `static/` (Flask draft).
- `plan/` folder: this documentation set (do not delete).

## 10. What a fresh session should NOT re-probe

- vLLM port on tes.home = **9999** (confirmed by user + verified).
- Router = 8000 (bypass). Ollama = serenity:11434.
- Docker API NOT yet open on tes.home (2375 closed) — enabling it is a user action
  on the other hosts (08/A4); do not assume it is open.
- The user is currently talking to the LLM **through the llama-swap router** —
  the backend on 9999 must never be destabilized by a real hot-swap during dev (R2).
