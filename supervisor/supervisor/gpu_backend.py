"""Hardware-agnostic GPU VRAM query (P04). Auto-detected once at supervisor
start: nvidia -> nvidia-smi; intel -> the "xe" driver's sysfs VRAM counters,
with a per-process /proc/<pid>/fdinfo reader as a fallback for `used_mb`;
else generic (no query possible). Only the query MECHANISM is
vendor-specific -- VRAM being held by the vLLM process and needing to drop
before the next model loads is universal (doc 5). A missing/unavailable
metric never fails a swap; only hard blocks (checked dashboard-side) do.

Confirmed live on the owner's real Intel host (an Arc B-series/Battlemage
card) via a sibling project, intel-gpu-monitor -- not guessed, no xpu-smi
involved -- that this kernel/driver version does NOT expose
`mem_info_vram_used`/`mem_info_vram_total` under
/sys/class/drm/card0/device at all (neither file exists, on the host or
inside this supervisor's own container; both see the identical sysfs
tree). `total_mb` still resolves via the PCI BAR `resource` file fallback
below. `used_mb` instead comes from `/proc/<pid>/fdinfo/<fd>`'s
`drm-active-vram0`/`drm-resident-vram0` fields, scoped to the vLLM child's
own process group -- intel-gpu-monitor's own fdinfo collector, validated
against this exact hardware, confirmed this is the right per-process
source when the sysfs counters are absent."""
from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
from dataclasses import dataclass
from pathlib import Path
from typing import TYPE_CHECKING, Optional

if TYPE_CHECKING:
    from .process_manager import ProcessManager

DEFAULT_DRM_CARD_PATH = Path("/sys/class/drm/card0/device")
DEFAULT_FAKE_BACKEND_PATH = Path("/tmp/fake_gpu_backend.json")
# A module-level Path (same pattern as intel-gpu-monitor's own fdinfo
# collector) rather than a hardcoded "/proc" string, so tests can point
# this at a fake tree instead of the real one.
_PROC = Path("/proc")

# `drm-active-vram0: <N> <unit>` / `drm-resident-vram0: <N> <unit>` --
# always carries a unit (KiB in practice) per the kernel's
# drm-usage-stats.rst, same format intel-gpu-monitor's FdinfoCollector
# parses.
_FDINFO_VRAM_RE = re.compile(r"^drm-(?:active|resident)-vram0:\s+(\d+)\s+(\w+)$", re.MULTILINE)


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
    mem_info_vram_total reads 0 or is absent entirely -- the same fallback
    intel-gpu-monitor's XeSysfsCollector uses on this exact hardware.
    `used_mb` comes back None (not a bail-out) when mem_info_vram_used
    doesn't exist on this kernel/driver -- IntelSysfsBackend.query() then
    tries the fdinfo fallback instead of losing `total_mb` along with it."""
    if not drm_base.exists():
        return None
    used_bytes = _read_sysfs_int(drm_base / "mem_info_vram_used")
    total_bytes = _read_sysfs_int(drm_base / "mem_info_vram_total") or 0
    if total_bytes == 0:
        total_bytes = _read_pci_bar_size_bytes(drm_base / "resource")
    if used_bytes is None and total_bytes == 0:
        return None
    return GpuInfo(
        vendor="intel",
        count=1,
        total_mb=round(total_bytes / 1024 / 1024) if total_bytes else None,
        used_mb=round(used_bytes / 1024 / 1024) if used_bytes is not None else None,
    )


def _read_sysfs_int(path: Path) -> Optional[int]:
    try:
        return int(path.read_text().strip())
    except (OSError, ValueError):
        return None


def _read_pci_slot(drm_base: Path) -> Optional[str]:
    """`PCI_SLOT_NAME=` out of the device's own `uevent` file, e.g.
    "0000:03:00.0" -- the same string fdinfo embeds per-fd, used to pick
    out only this GPU's clients when more than one DRM device is present."""
    try:
        text = (drm_base / "uevent").read_text()
    except OSError:
        return None
    for line in text.splitlines():
        if line.startswith("PCI_SLOT_NAME="):
            return line.split("=", 1)[1].strip()
    return None


def _parse_fdinfo_vram_mb(text: str, pci_slot: str) -> Optional[float]:
    """One `/proc/<pid>/fdinfo/<fd>` file's content -> VRAM MB, or None if
    this fd isn't an xe client of the GPU at `pci_slot` at all (a process
    can hold fds for several devices/drivers)."""
    if "drm-driver:\txe" not in text and "drm-driver: xe" not in text:
        return None
    if pci_slot not in text:
        return None
    best: Optional[float] = None
    for match in _FDINFO_VRAM_RE.finditer(text):
        value = int(match.group(1))
        unit = match.group(2).lower()
        mb = value / 1024.0 if unit in ("kib", "kb") else value
        best = mb if best is None else max(best, mb)
    return best


def _max_fdinfo_vram_mb(pid: int, pci_slot: str) -> Optional[float]:
    """The largest single-fd VRAM reading for one process -- its fds for
    the same GPU buffer report the same size, so max (not sum) avoids
    double-counting within one process."""
    fdinfo_dir = _PROC / str(pid) / "fdinfo"
    try:
        fd_names = os.listdir(fdinfo_dir)
    except OSError:
        return None
    best: Optional[float] = None
    for fd_name in fd_names:
        try:
            text = (fdinfo_dir / fd_name).read_text()
        except OSError:
            continue
        mb = _parse_fdinfo_vram_mb(text, pci_slot)
        if mb is not None:
            best = mb if best is None else max(best, mb)
    return best


def _pids_in_group(pgid: int) -> list[int]:
    """Every live PID in process group `pgid`, visible via this
    supervisor's own /proc -- the vLLM child it spawned runs in its own
    session/process group but the SAME pid namespace (no `pid: host`
    needed), and a multi-worker vLLM (tensor-parallel, spawn method) puts
    its GPU-holding workers in that same group, not just the top pid."""
    pids = []
    try:
        entries = os.listdir(_PROC)
    except OSError:
        return pids
    for entry in entries:
        if not entry.isdigit():
            continue
        pid = int(entry)
        try:
            if os.getpgid(pid) == pgid:
                pids.append(pid)
        except (ProcessLookupError, PermissionError):
            continue
    return pids


def read_fdinfo_vram_used_mb(process_manager: "ProcessManager", pci_slot: str) -> Optional[float]:
    """Sum of each process's own max VRAM reading across the vLLM child's
    process group. None (not 0) when there's no running child or nothing
    matched -- lets the caller tell "no data" from "legitimately idle"."""
    info = process_manager.info
    if info is None or not process_manager.is_running():
        return None
    try:
        pgid = os.getpgid(info.pid)
    except ProcessLookupError:
        return None
    total = 0.0
    found = False
    for pid in _pids_in_group(pgid):
        pid_mb = _max_fdinfo_vram_mb(pid, pci_slot)
        if pid_mb is not None:
            found = True
            total += pid_mb
    return total if found else None


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

    def __init__(
        self,
        drm_base: Path = DEFAULT_DRM_CARD_PATH,
        process_manager: Optional["ProcessManager"] = None,
    ) -> None:
        self.drm_base = drm_base
        self._process_manager = process_manager
        self._pci_slot = _read_pci_slot(drm_base)

    def query(self) -> GpuInfo:
        info = parse_xe_sysfs(self.drm_base)
        if info is None:
            return GENERIC_INFO
        if info.used_mb is None and self._process_manager is not None and self._pci_slot is not None:
            fdinfo_used = read_fdinfo_vram_used_mb(self._process_manager, self._pci_slot)
            if fdinfo_used is not None:
                info = GpuInfo(vendor=info.vendor, count=info.count, total_mb=info.total_mb, used_mb=round(fdinfo_used))
        return info


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


def _xe_driver_present(drm_base: Path) -> bool:
    """Whether the "xe" kernel driver is bound to this DRM device --
    confirmed live (P16a follow-up) that `mem_info_vram_used` is NOT a
    reliable signal: this exact host's Arc B-series card runs xe but
    exposes no mem_info_* sysfs files at all. The driver symlink
    (device/driver -> .../drivers/xe) is present regardless."""
    try:
        return (drm_base / "driver").resolve().name == "xe"
    except OSError:
        return False


def detect_backend(process_manager: Optional["ProcessManager"] = None) -> GpuBackend:
    """Auto-detected once at supervisor start (doc 5)."""
    kind = os.environ.get("GPU_BACKEND")
    if kind == "fake":
        return FakeBackend(Path(os.environ.get("GPU_BACKEND_FAKE_PATH", str(DEFAULT_FAKE_BACKEND_PATH))))
    if shutil.which("nvidia-smi") is not None:
        return NvidiaBackend()
    if _xe_driver_present(DEFAULT_DRM_CARD_PATH):
        return IntelSysfsBackend(process_manager=process_manager)
    return GpuBackend()
