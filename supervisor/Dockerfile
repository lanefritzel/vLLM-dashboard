# One build per hardware stack -- the compose file owns image/devices/memory/
# port (doc 13); this just bakes the supervisor's code onto that base.
ARG BASE_IMAGE=intel/llm-scaler-vllm:0.21.0-b3
FROM ${BASE_IMAGE}

COPY entrypoint.sh /entrypoint.sh
COPY supervisor.py /app/supervisor.py
COPY supervisor/   /app/supervisor/

RUN chmod +x /entrypoint.sh

# tini as PID 1: baked in here if the base image doesn't already ship it
# (P00's stock-image check, deferred to P16a prep, decides which) -- until
# then, rely on the compose service's `init: true`, which gives the same
# reaping guarantee without assuming anything about this base image's distro.
ENTRYPOINT ["/entrypoint.sh"]
