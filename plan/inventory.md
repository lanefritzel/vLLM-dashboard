# Owner-assisted inventories (P00)

These touch the live host, so none were run without asking first.

| Item | Status |
|---|---|
| Proxy URLs currently used by clients | **Done (2026-10-31), per owner.** This machine (where the refactor work happens) doesn't host or run any LLM -- it's a dev machine only. Nothing on it is connected to a proxy URL. No live clients identified as pointed at `/proxy/<slug>/v1` as part of this check; the owner didn't name any (OpenWebUI, a coding agent, etc.) as currently in use. If that changes before P11/P16a, re-check then. |
| Ollama sources and who uses them | **Done (2026-10-31), per owner.** No Ollama sources are currently registered in the dashboard. Nothing to convert or warn anyone about for Ollama specifically. |
| On-disk model layout (`ls` of the models root and `hf-cache/`, the running compose's `volumes:`) | deferred to P16a prep -- needs looking at the actual live host (`tes.home`). |
| Stock-image delivery check (stock entrypoint behavior, Python path, whether `tini` exists) | deferred to P16a prep -- needs inspecting the live `intel/llm-scaler-vllm` container. |
