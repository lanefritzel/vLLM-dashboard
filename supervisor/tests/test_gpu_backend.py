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
    _max_fdinfo_vram_mb,
    _parse_fdinfo_vram_mb,
    _pids_in_group,
    _read_pci_slot,
    _xe_driver_present,
    detect_backend,
    parse_nvidia_smi,
    parse_xe_sysfs,
    read_fdinfo_vram_used_mb,
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


def test_parse_xe_sysfs_used_missing_falls_back_to_pci_bar_total_only(tmp_path):
    """Confirmed live on the owner's real host (an Arc B-series card):
    mem_info_vram_used/total don't exist under this kernel/driver's sysfs
    tree at all. `total_mb` should still resolve via the PCI BAR fallback;
    `used_mb` comes back None (for IntelSysfsBackend.query() to fill in via
    fdinfo), not a hard bail that loses the total too."""
    drm_base = tmp_path / "card0" / "device"
    drm_base.mkdir(parents=True)
    bar_size = 32 * 1024 * 1024 * 1024  # 32 GiB BAR
    (drm_base / "resource").write_text(f"0x0000000000000000 0x0000000000000000 0x0\n0x00000000a0000000 {hex(0xa0000000 + bar_size - 1)} 0x204\n")

    info = parse_xe_sysfs(drm_base)
    assert info is not None
    assert info.used_mb is None
    assert info.total_mb == 32768


def test_parse_xe_sysfs_returns_none_when_nothing_resolves(tmp_path):
    """drm_base itself exists (unlike the missing-dir case above) but has
    none of mem_info_vram_used/total nor a resource file -- there's
    nothing at all to report."""
    drm_base = tmp_path / "card0" / "device"
    drm_base.mkdir(parents=True)
    assert parse_xe_sysfs(drm_base) is None


def test_read_pci_slot_reads_uevent(tmp_path):
    drm_base = tmp_path / "card0" / "device"
    drm_base.mkdir(parents=True)
    (drm_base / "uevent").write_text("DRIVER=xe\nPCI_SLOT_NAME=0000:03:00.0\n")
    assert _read_pci_slot(drm_base) == "0000:03:00.0"


def test_read_pci_slot_missing_uevent_returns_none(tmp_path):
    drm_base = tmp_path / "card0" / "device"
    drm_base.mkdir(parents=True)
    assert _read_pci_slot(drm_base) is None


def test_xe_driver_present_true_when_driver_symlink_resolves_to_xe(tmp_path):
    drivers_xe = tmp_path / "drivers" / "xe"
    drivers_xe.mkdir(parents=True)
    drm_base = tmp_path / "card0" / "device"
    drm_base.mkdir(parents=True)
    (drm_base / "driver").symlink_to(drivers_xe, target_is_directory=True)

    assert _xe_driver_present(drm_base) is True


def test_xe_driver_present_false_for_a_different_driver(tmp_path):
    drivers_i915 = tmp_path / "drivers" / "i915"
    drivers_i915.mkdir(parents=True)
    drm_base = tmp_path / "card0" / "device"
    drm_base.mkdir(parents=True)
    (drm_base / "driver").symlink_to(drivers_i915, target_is_directory=True)

    assert _xe_driver_present(drm_base) is False


def test_xe_driver_present_false_when_no_driver_link(tmp_path):
    drm_base = tmp_path / "card0" / "device"
    drm_base.mkdir(parents=True)
    assert _xe_driver_present(drm_base) is False


_XE_FDINFO = (
    "pos:\t0\n"
    "flags:\t0100002\n"
    "mnt_id:\t23\n"
    "ino:\t5\n"
    "drm-driver:\txe\n"
    "drm-pdev:\t0000:03:00.0\n"
    "drm-client-id:\t12\n"
    "drm-active-vram0:\t1048576 KiB\n"
    "drm-resident-vram0:\t1048576 KiB\n"
)


def test_parse_fdinfo_vram_mb_matches_driver_and_slot():
    assert _parse_fdinfo_vram_mb(_XE_FDINFO, "0000:03:00.0") == 1024.0


def test_parse_fdinfo_vram_mb_none_for_a_different_pci_slot():
    assert _parse_fdinfo_vram_mb(_XE_FDINFO, "0000:99:00.0") is None


def test_parse_fdinfo_vram_mb_none_for_a_non_xe_driver():
    other = _XE_FDINFO.replace("drm-driver:\txe", "drm-driver:\ti915")
    assert _parse_fdinfo_vram_mb(other, "0000:03:00.0") is None


def test_parse_fdinfo_vram_mb_takes_the_larger_of_active_and_resident():
    text = _XE_FDINFO.replace("drm-resident-vram0:\t1048576 KiB", "drm-resident-vram0:\t2097152 KiB")
    assert _parse_fdinfo_vram_mb(text, "0000:03:00.0") == 2048.0


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


def _write_fdinfo(proc_dir: Path, pid: int, fd: str, text: str) -> None:
    fdinfo_dir = proc_dir / str(pid) / "fdinfo"
    fdinfo_dir.mkdir(parents=True)
    (fdinfo_dir / fd).write_text(text)


def test_max_fdinfo_vram_mb_reads_the_spawned_childs_own_proc_entry(tmp_path, monkeypatch):
    from supervisor import gpu_backend as gpu_backend_module
    from supervisor.process_manager import ProcessManager

    pm = ProcessManager()
    pm.spawn(["sleep", "5"])
    pid = pm.info.pid
    try:
        monkeypatch.setattr(gpu_backend_module, "_PROC", tmp_path)
        _write_fdinfo(tmp_path, pid, "4", _XE_FDINFO)
        assert _max_fdinfo_vram_mb(pid, "0000:03:00.0") == 1024.0
    finally:
        pm.stop()


def test_max_fdinfo_vram_mb_no_matching_fd_returns_none(tmp_path, monkeypatch):
    from supervisor import gpu_backend as gpu_backend_module

    monkeypatch.setattr(gpu_backend_module, "_PROC", tmp_path)
    assert _max_fdinfo_vram_mb(999999, "0000:03:00.0") is None


def test_pids_in_group_finds_a_real_spawned_child():
    from supervisor.process_manager import ProcessManager

    pm = ProcessManager()
    pm.spawn(["sleep", "5"])
    try:
        import os

        pgid = os.getpgid(pm.info.pid)
        assert pm.info.pid in _pids_in_group(pgid)
    finally:
        pm.stop()


def test_read_fdinfo_vram_used_mb_sums_across_the_childs_process_group(tmp_path, monkeypatch):
    from supervisor import gpu_backend as gpu_backend_module
    from supervisor.process_manager import ProcessManager

    pm = ProcessManager()
    pm.spawn(["sleep", "5"])
    pid = pm.info.pid
    try:
        monkeypatch.setattr(gpu_backend_module, "_PROC", tmp_path)
        _write_fdinfo(tmp_path, pid, "4", _XE_FDINFO)
        assert read_fdinfo_vram_used_mb(pm, "0000:03:00.0") == 1024.0
    finally:
        pm.stop()


def test_read_fdinfo_vram_used_mb_no_running_child_returns_none(tmp_path, monkeypatch):
    from supervisor import gpu_backend as gpu_backend_module
    from supervisor.process_manager import ProcessManager

    monkeypatch.setattr(gpu_backend_module, "_PROC", tmp_path)
    assert read_fdinfo_vram_used_mb(ProcessManager(), "0000:03:00.0") is None


def test_intel_sysfs_backend_falls_back_to_fdinfo_for_used_mb(tmp_path, monkeypatch):
    """End-to-end: mem_info_vram_used absent (today's reality on the real
    host), total comes from the PCI BAR, used comes from fdinfo."""
    from supervisor import gpu_backend as gpu_backend_module
    from supervisor.process_manager import ProcessManager

    drm_base = tmp_path / "card0" / "device"
    drm_base.mkdir(parents=True)
    bar_size = 32 * 1024 * 1024 * 1024
    (drm_base / "resource").write_text(f"0x0000000000000000 0x0000000000000000 0x0\n0x00000000a0000000 {hex(0xa0000000 + bar_size - 1)} 0x204\n")
    (drm_base / "uevent").write_text("DRIVER=xe\nPCI_SLOT_NAME=0000:03:00.0\n")

    pm = ProcessManager()
    pm.spawn(["sleep", "5"])
    pid = pm.info.pid
    try:
        proc_dir = tmp_path / "proc"
        monkeypatch.setattr(gpu_backend_module, "_PROC", proc_dir)
        _write_fdinfo(proc_dir, pid, "4", _XE_FDINFO)

        backend = IntelSysfsBackend(drm_base=drm_base, process_manager=pm)
        info = backend.query()
        assert info.vendor == "intel"
        assert info.total_mb == 32768
        assert info.used_mb == 1024
    finally:
        pm.stop()


def test_intel_sysfs_backend_used_stays_none_without_a_process_manager(tmp_path):
    drm_base = tmp_path / "card0" / "device"
    drm_base.mkdir(parents=True)
    bar_size = 32 * 1024 * 1024 * 1024
    (drm_base / "resource").write_text(f"0x0000000000000000 0x0000000000000000 0x0\n0x00000000a0000000 {hex(0xa0000000 + bar_size - 1)} 0x204\n")

    backend = IntelSysfsBackend(drm_base=drm_base)
    info = backend.query()
    assert info.total_mb == 32768
    assert info.used_mb is None


def test_detect_backend_picks_intel_sysfs_when_xe_driver_present(tmp_path, monkeypatch):
    from supervisor import gpu_backend as gpu_backend_module
    from supervisor.process_manager import ProcessManager

    drivers_xe = tmp_path / "drivers" / "xe"
    drivers_xe.mkdir(parents=True)
    drm_base = tmp_path / "card0" / "device"
    drm_base.mkdir(parents=True)
    (drm_base / "driver").symlink_to(drivers_xe, target_is_directory=True)

    monkeypatch.setattr(gpu_backend_module, "DEFAULT_DRM_CARD_PATH", drm_base)
    monkeypatch.delenv("GPU_BACKEND", raising=False)
    monkeypatch.setattr(gpu_backend_module.shutil, "which", lambda name: None)

    pm = ProcessManager()
    backend = detect_backend(pm)
    assert isinstance(backend, IntelSysfsBackend)
    assert backend._process_manager is pm


def test_detect_backend_falls_back_to_generic_without_nvidia_or_xe(tmp_path, monkeypatch):
    from supervisor import gpu_backend as gpu_backend_module

    monkeypatch.setattr(gpu_backend_module, "DEFAULT_DRM_CARD_PATH", tmp_path / "does-not-exist")
    monkeypatch.delenv("GPU_BACKEND", raising=False)
    monkeypatch.setattr(gpu_backend_module.shutil, "which", lambda name: None)

    assert type(detect_backend()) is GpuBackend
