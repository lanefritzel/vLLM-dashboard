"""vLLM neuron-map visualizer plugin (prototype, v0.1).

Loaded through the `vllm.general_plugins` entry point in every vLLM process.
It does nothing unless VIS_ENABLED=1, and even then it only does work while a
viewer is connected to its small HTTP endpoint (see tap.py).
"""
import os


def register() -> None:
    if os.environ.get("VIS_ENABLED") != "1":
        return
    from . import tap

    tap.install()
