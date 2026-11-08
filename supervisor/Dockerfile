# One build per hardware stack -- the compose file owns image/devices/memory/
# port (doc 13); this just bakes the supervisor's code onto that base.
ARG BASE_IMAGE=intel/llm-scaler-vllm:0.21.0-b3
FROM ${BASE_IMAGE}

COPY entrypoint.sh /entrypoint.sh
COPY supervisor.py /app/supervisor.py
COPY supervisor/   /app/supervisor/
COPY requirements.txt /app/requirements.txt

# fastapi/uvicorn already ship in the vLLM base image (doc 5's "zero
# extra deps") -- huggingface_hub does too (vllm/transformers depend on it
# themselves). Confirmed via the real P16a hardware rehearsal: pinning a
# specific huggingface_hub range here (as this once did) can force a
# version that conflicts with whatever this exact base image's transformers
# was actually built/tested against -- broke transformers' own import of
# huggingface_hub on intel/llm-scaler-vllm:0.26.0-b2. requirements.txt
# leaves huggingface_hub unpinned so this install is a true no-op when it's
# already present, whatever version that is.
RUN pip install --no-cache-dir -r /app/requirements.txt

RUN chmod +x /entrypoint.sh

# Neuron-map visualizer plugin for the dashboard's Overview section. vLLM finds it through
# its `vllm.general_plugins` entry point, so nothing in the supervisor or the recipes
# changes. Loaded but dormant: it only does work while a viewer has the neuron map open
# (measured on the Intel stack: no throughput change when idle, ~1.4% worst case when
# sampling every step). Only the package is copied, not its tests.
#   - Off for one recipe:          extra_env VIS_ENABLED=0 (needs that recipe's redeploy)
#   - Off right now, no restart:   POST /disable on the plugin port (loopback or VIS_TOKEN)
#   - Reachable from the dashboard: it listens on 127.0.0.1:9998 by default. If the dashboard
#     reaches vLLM over the LAN, set VIS_BIND=0.0.0.0 AND VIS_TOKEN (and Visualizer__Token on
#     the dashboard).
COPY vllm-visualizer/pyproject.toml /opt/vllm-visualizer/pyproject.toml
COPY vllm-visualizer/vllm_visualizer/ /opt/vllm-visualizer/vllm_visualizer/
RUN pip install --no-cache-dir --no-deps /opt/vllm-visualizer
ENV VIS_ENABLED=1

# tini as PID 1: baked in here if the base image doesn't already ship it
# (P00's stock-image check, deferred to P16a prep, decides which) -- until
# then, rely on the compose service's `init: true`, which gives the same
# reaping guarantee without assuming anything about this base image's distro.
ENTRYPOINT ["/entrypoint.sh"]
