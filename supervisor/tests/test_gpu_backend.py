"""P04's Definition of done: canned nvidia-smi / xe-sysfs output parses to
the expected {vendor, total_mb, used_mb}; garbage never raises.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

from supervisor.gpu_backend import (
    FakeBackend,
    GpuBackend,
    IntelSysfsBackend,
    parse_nvidia_smi,
    parse_xe_sysfs,
)


def test_parse_nvidia_smi_single_gpu():
    output = "1024, 12288\n"
    info = parse_nvidia_smi(output)
    assert info.vendor == "nvidia"
    assert info.count == 1
    assert info.used_mb == 1024
    assert info.total_mb == 12288


def test_parse_nvidia_smi_multiple_gpus_sums_values():
    output = "1024, 12288\n2048, 12288\n"
    info = parse_nvidia_smi(output)
    assert info.count == 2
    assert info.used_mb == 3072
    assert info.total_mb == 24576


def test_parse_nvidia_smi_garbage_returns_none_not_an_exception():
    assert parse_nvidia_smi("") is None
    assert parse_nvidia_smi("not,csv,at,all\ngarbage") is None
    assert parse_nvidia_smi("not-a-number, also-not-a-number") is None


def test_parse_xe_sysfs_reads_used_and_total(tmp_path):
    drm_base = tmp_path / "card0" / "device"
    drm_base.mkdir(parents=True)
    (drm_base / "mem_info_vram_used").write_text(str(1 * 1024 * 1024 * 1024))  # 1 GiB
    (drm_base / "mem_info_vram_total").write_text(str(12 * 1024 * 1024 * 1024))  # 12 GiB

    info = parse_xe_sysfs(drm_base)
    assert info.vendor == "intel"
    assert info.count == 1
    assert info.used_mb == 1024
    assert info.total_mb == 12288


def test_parse_xe_sysfs_falls_back_to_pci_bar_size_when_total_is_zero(tmp_path):
    drm_base = tmp_path / "card0" / "device"
    drm_base.mkdir(parents=True)
    (drm_base / "mem_info_vram_used").write_text("0")
    (drm_base / "mem_info_vram_total").write_text("0")
    # A PCI "resource" file: start/end/flags in hex, one BAR per line --
    # same format intel-gpu-monitor's XeSysfsCollector falls back to.
    bar_size = 16 * 1024 * 1024 * 1024  # 16 GiB BAR
    (drm_base / "resource").write_text(f"0x0000000000000000 0x0000000000000000 0x0\n0x00000000a0000000 {hex(0xa0000000 + bar_size - 1)} 0x204\n")

    info = parse_xe_sysfs(drm_base)
    assert info.total_mb == 16384


def test_parse_xe_sysfs_missing_sysfs_files_returns_none(tmp_path):
    assert parse_xe_sysfs(tmp_path / "does-not-exist") is None


def test_intel_sysfs_backend_degrades_to_generic_when_files_absent(tmp_path):
    backend = IntelSysfsBackend(drm_base=tmp_path / "nope")
    info = backend.query()
    assert info.vendor == "generic"
    assert info.used_mb is None


def test_generic_backend_reports_vendor_generic_with_no_metrics():
    info = GpuBackend().query()
    assert info.vendor == "generic"
    assert info.count == 0
    assert info.used_mb is None
    assert info.total_mb is None


def test_fake_backend_reads_from_its_file(tmp_path):
    path = tmp_path / "fake_gpu.json"
    path.write_text('{"vendor": "fake", "count": 1, "total_mb": 12288, "used_mb": 4096}')
    backend = FakeBackend(path)
    info = backend.query()
    assert info.used_mb == 4096
    assert info.total_mb == 12288


def test_fake_backend_missing_file_degrades_to_generic(tmp_path):
    backend = FakeBackend(tmp_path / "does-not-exist.json")
    info = backend.query()
    assert info.vendor == "generic"
