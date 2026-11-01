"""Hardware-agnostic GPU VRAM query (P04). Auto-detected once at supervisor
start: nvidia -> nvidia-smi; intel -> the "xe" driver's sysfs VRAM counters
(confirmed against the owner's real Intel host via a sibling project,
intel-gpu-monitor -- not guessed, no xpu-smi involved); else generic (no
query possible). Only the query MECHANISM is vendor-specific -- VRAM being
held by the vLLM process and needing to drop before the next model loads
is universal (doc 5). A missing/unavailable metric never fails a swap;
only hard blocks (checked dashboard-side) do.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

DEFAULT_DRM_CARD_PATH = Path("/sys/class/drm/card0/device")
DEFAULT_FAKE_BACKEND_PATH = Path("/tmp/fake_gpu_backend.json")


@dataclass
class GpuInfo:
    vendor: str  # "nvidia" | "intel" | "generic"
    count: int
    total_mb: Optional[int]
    used_mb: Optional[int]

    def to_dict(self) -> dict:
        return {"vendor": self.vendor, "count": self.count, "total_mb": self.total_mb, "used_mb": self.used_mb}


GENERIC_INFO = GpuInfo(vendor="generic", count=0, total_mb=None, used_mb=None)


def parse_nvidia_smi(output: str) -> Optional[GpuInfo]:
    """`nvidia-smi --query-gpu=memory.used,memory.total --format=csv,noheader,nounits`
    -- one "used, total" line per GPU, values already in MiB. Garbage in ->
    None out (never raises)."""
    rows: list[tuple[int, int]] = []
    for line in output.strip().splitlines():
        parts = [p.strip() for p in line.split(",")]
        if len(parts) != 2:
            continue
        try:
            rows.append((int(float(parts[0])), int(float(parts[1]))))
        except ValueError:
            continue
    if not rows:
        return None
    return GpuInfo(vendor="nvidia", count=len(rows), total_mb=sum(r[1] for r in rows), used_mb=sum(r[0] for r in rows))


def parse_xe_sysfs(drm_base: Path = DEFAULT_DRM_CARD_PATH) -> Optional[GpuInfo]:
    """Intel "xe" driver sysfs VRAM counters, in bytes. Falls back to the
    PCI BAR size (the `resource` file) for the total when
    mem_info_vram_total reads 0 -- the same fallback intel-gpu-monitor's
    XeSysfsCollector uses on this exact hardware."""
    used_path = drm_base / "mem_info_vram_used"
    if not used_path.exists():
        return None
    used_bytes = _read_sysfs_int(used_path)
    if used_bytes is None:
        return None
    total_bytes = _read_sysfs_int(drm_base / "mem_info_vram_total") or 0
    if total_bytes == 0:
        total_bytes = _read_pci_bar_size_bytes(drm_base / "resource")
    return GpuInfo(
        vendor="intel",
        count=1,
        total_mb=round(total_bytes / 1024 / 1024) if total_bytes else None,
        used_mb=round(used_bytes / 1024 / 1024),
    )


def _read_sysfs_int(path: Path) -> Optional[int]:
    try:
        return int(path.read_text().strip())
    except (OSError, ValueError):
        return None


def _read_pci_bar_size_bytes(resource_path: Path) -> int:
    try:
        text = resource_path.read_text()
    except OSError:
        return 0
    max_bytes = 0
    for line in text.splitlines():
        parts = line.strip().split()
        if len(parts) != 3:
            continue
        try:
            start, end = int(parts[0], 16), int(parts[1], 16)
        except ValueError:
            continue
        if start == 0 and end == 0:
            continue
        max_bytes = max(max_bytes, end - start + 1)
    return max_bytes


class GpuBackend:
    vendor = "generic"

    def query(self) -> GpuInfo:
        return GENERIC_INFO


class NvidiaBackend(GpuBackend):
    vendor = "nvidia"

    def query(self) -> GpuInfo:
        try:
            result = subprocess.run(
                ["nvidia-smi", "--query-gpu=memory.used,memory.total", "--format=csv,noheader,nounits"],
                capture_output=True, text=True, timeout=5, check=True,
            )
        except (OSError, subprocess.SubprocessError):
            return GENERIC_INFO
        return parse_nvidia_smi(result.stdout) or GENERIC_INFO


class IntelSysfsBackend(GpuBackend):
    vendor = "intel"

    def __init__(self, drm_base: Path = DEFAULT_DRM_CARD_PATH) -> None:
        self.drm_base = drm_base

    def query(self) -> GpuInfo:
        return parse_xe_sysfs(self.drm_base) or GENERIC_INFO


class FakeBackend(GpuBackend):
    """Test-only (work item 2): GPU_BACKEND=fake, value read from a file so
    a test can change "used_mb" between polls by rewriting it."""

    vendor = "fake"

    def __init__(self, path: Path = DEFAULT_FAKE_BACKEND_PATH) -> None:
        self.path = path

    def query(self) -> GpuInfo:
        try:
            data = json.loads(self.path.read_text())
        except (OSError, json.JSONDecodeError):
            return GENERIC_INFO
        return GpuInfo(
            vendor=data.get("vendor", "fake"),
            count=data.get("count", 1),
            total_mb=data.get("total_mb"),
            used_mb=data.get("used_mb"),
        )


def detect_backend() -> GpuBackend:
    """Auto-detected once at supervisor start (doc 5)."""
    kind = os.environ.get("GPU_BACKEND")
    if kind == "fake":
        return FakeBackend(Path(os.environ.get("GPU_BACKEND_FAKE_PATH", str(DEFAULT_FAKE_BACKEND_PATH))))
    if shutil.which("nvidia-smi") is not None:
        return NvidiaBackend()
    if (DEFAULT_DRM_CARD_PATH / "mem_info_vram_used").exists():
        return IntelSysfsBackend()
    return GpuBackend()
