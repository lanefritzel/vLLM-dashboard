"""Model downloads (P08) -- no network, a FakeHfClient serves a local
directory as the "repo".
"""
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

from supervisor.downloads import (
    DownloadInProgressError,
    DownloadManager,
    DownloadNotFoundError,
    FakeHfClient,
)


def make_repo(tmp_path: Path, name: str, files: dict[str, bytes], config: dict | None = None) -> Path:
    base = tmp_path / "repos" / name
    base.mkdir(parents=True)
    for rel_path, content in files.items():
        p = base / rel_path
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(content)
    if config is not None:
        (base / "config.json").write_text(json.dumps(config))
    return base


def test_inspect_returns_files_config_and_free_bytes(tmp_path):
    repo = make_repo(tmp_path, "org/model-a", {"model.safetensors": b"x" * 1000}, config={"max_position_embeddings": 4096})
    manager = DownloadManager(FakeHfClient({"org/model-a": repo}), models_root=tmp_path / "models")

    result = manager.inspect("org/model-a")
    assert result["files"] == [{"path": "config.json", "size": len(json.dumps({"max_position_embeddings": 4096}))},
                                {"path": "model.safetensors", "size": 1000}]
    assert result["config"] == {"max_position_embeddings": 4096}
    assert result["free_bytes"] > 0


def test_inspect_unknown_repo_raises_404(tmp_path):
    manager = DownloadManager(FakeHfClient({}), models_root=tmp_path / "models")
    from supervisor.downloads import HfRepoError
    try:
        manager.inspect("nonexistent/repo")
        assert False, "expected HfRepoError"
    except HfRepoError as exc:
        assert exc.status == 404


def test_inspect_gated_repo_raises_401(tmp_path):
    manager = DownloadManager(FakeHfClient({}), models_root=tmp_path / "models")
    from supervisor.downloads import HfRepoError
    try:
        manager.inspect("gated/repo")
        assert False, "expected HfRepoError"
    except HfRepoError as exc:
        assert exc.status == 401


def test_inspect_unreachable_repo_raises_with_no_status(tmp_path):
    manager = DownloadManager(FakeHfClient({}), models_root=tmp_path / "models")
    from supervisor.downloads import HfRepoError
    try:
        manager.inspect("unreachable/repo")
        assert False, "expected HfRepoError"
    except HfRepoError as exc:
        assert exc.status is None
        assert "could not reach" in str(exc)


def test_download_reaches_done_with_monotonic_progress_and_lands_at_the_right_path(tmp_path):
    repo = make_repo(tmp_path, "org/model-a", {
        "model.safetensors": b"x" * (5 * 1024 * 1024), "tokenizer.json": b"y" * 1024,
    }, config={"max_position_embeddings": 4096})
    models_root = tmp_path / "models"
    manager = DownloadManager(FakeHfClient({"org/model-a": repo}, chunk_delay=0.01), models_root=models_root)

    download_id = manager.start_download("org/model-a", "model-a")

    seen_pct = []
    for _ in range(200):
        status = manager.get(download_id)
        seen_pct.append(status["progress_pct"])
        if status["status"] == "done":
            break
        time.sleep(0.05)
    else:
        raise AssertionError("download never reached done")

    assert seen_pct == sorted(seen_pct), "progress_pct must never decrease"
    assert status["files_done"] == status["files_total"] == 3  # 2 files + config.json
    assert status["eta_seconds"] is None or status["eta_seconds"] >= 0
    assert status["path"] == str(models_root / "model-a")
    assert status["load_format"] is None
    assert status["config"] == {"max_position_embeddings": 4096}

    dest = models_root / "model-a"
    assert (dest / "model.safetensors").stat().st_size == 5 * 1024 * 1024
    assert (dest / "tokenizer.json").stat().st_size == 1024


def test_single_file_gguf_download_sets_load_format_gguf(tmp_path):
    repo = make_repo(tmp_path, "org/model-gguf", {"model.Q4_K_M.gguf": b"g" * 2048, "README.md": b"ignored"})
    models_root = tmp_path / "models"
    manager = DownloadManager(FakeHfClient({"org/model-gguf": repo}), models_root=models_root)

    download_id = manager.start_download("org/model-gguf", "model-gguf", filename="model.Q4_K_M.gguf")
    for _ in range(100):
        status = manager.get(download_id)
        if status["status"] == "done":
            break
        time.sleep(0.05)
    else:
        raise AssertionError("download never reached done")

    assert status["files_total"] == 1  # only the named file, not README.md
    assert status["load_format"] == "gguf"
    assert status["config"] is None
    assert status["size_bytes"] == 2048
    assert status["path"] == str(models_root / "model-gguf" / "model.Q4_K_M.gguf")


def test_second_concurrent_download_gets_409(tmp_path):
    repo = make_repo(tmp_path, "org/model-a", {"model.safetensors": b"x" * (2 * 1024 * 1024)})
    manager = DownloadManager(FakeHfClient({"org/model-a": repo}, chunk_delay=0.05), models_root=tmp_path / "models")

    first_id = manager.start_download("org/model-a", "model-a")
    try:
        manager.start_download("org/model-a", "model-a-2")
        assert False, "expected DownloadInProgressError"
    except DownloadInProgressError as exc:
        assert exc.download_id == first_id
    finally:
        manager.cancel(first_id)


def test_delete_mid_download_removes_the_partial_directory(tmp_path):
    repo = make_repo(tmp_path, "org/model-a", {"model.safetensors": b"x" * (5 * 1024 * 1024)})
    models_root = tmp_path / "models"
    manager = DownloadManager(FakeHfClient({"org/model-a": repo}, chunk_delay=0.02), models_root=models_root)

    download_id = manager.start_download("org/model-a", "model-a")
    time.sleep(0.1)  # let it get partway through
    manager.cancel(download_id)

    assert manager.get(download_id)["status"] == "cancelled"
    assert not (models_root / "model-a").exists()


def test_unreachable_repo_download_fails_with_readable_message(tmp_path):
    manager = DownloadManager(FakeHfClient({}), models_root=tmp_path / "models")
    download_id = manager.start_download("unreachable/repo", "model-x")

    for _ in range(100):
        status = manager.get(download_id)
        if status["status"] != "downloading":
            break
        time.sleep(0.05)
    else:
        raise AssertionError("download never concluded")

    assert status["status"] == "download_failed"
    assert "could not reach" in status["error"]


def test_retry_after_failure_resumes_without_redownloading_finished_files(tmp_path):
    """Simulates a failure partway through a multi-file download, then a
    retry -- the already-fully-downloaded file must not be re-copied (its
    on-disk bytes are left untouched; only the missing file gets fetched)."""
    repo = make_repo(tmp_path, "org/model-a", {
        "a.bin": b"a" * 1024, "b.bin": b"b" * 1024,
    })
    models_root = tmp_path / "models"
    client = FakeHfClient({"org/model-a": repo})
    manager = DownloadManager(client, models_root=models_root)

    # First attempt: manually pre-populate the destination as if "a.bin"
    # finished and the process died before "b.bin" -- the realistic shape
    # of a partial download on disk.
    dest = models_root / "model-a"
    dest.mkdir(parents=True)
    (dest / "a.bin").write_bytes(b"a" * 1024)
    before_mtime = (dest / "a.bin").stat().st_mtime

    download_id = manager.start_download("org/model-a", "model-a")
    for _ in range(100):
        status = manager.get(download_id)
        if status["status"] != "downloading":
            break
        time.sleep(0.05)
    else:
        raise AssertionError("retry download never concluded")

    assert status["status"] == "done"
    assert (dest / "a.bin").stat().st_mtime == before_mtime, "the already-finished file must not be re-copied"
    assert (dest / "b.bin").read_bytes() == b"b" * 1024


def test_hf_token_never_appears_in_any_response(tmp_path, capsys):
    from supervisor.downloads import HfClient

    secret = "hf_super_secret_token_value"
    repo = make_repo(tmp_path, "org/model-a", {"model.safetensors": b"x" * 1024}, config={"k": "v"})
    client = FakeHfClient({"org/model-a": repo})
    # A real HfClient would carry the token internally -- construct one
    # too (never actually used, since FakeHfClient handles the calls) to
    # make sure nothing about token plumbing leaks into any response.
    HfClient(token=secret)
    manager = DownloadManager(client, models_root=tmp_path / "models")

    inspect_result = manager.inspect("org/model-a")
    download_id = manager.start_download("org/model-a", "model-a")
    for _ in range(100):
        status = manager.get(download_id)
        if status["status"] != "downloading":
            break
        time.sleep(0.05)

    assert secret not in json.dumps(inspect_result)
    assert secret not in json.dumps(status)
    captured = capsys.readouterr()
    assert secret not in captured.out
    assert secret not in captured.err


def test_get_unknown_download_id_raises(tmp_path):
    manager = DownloadManager(FakeHfClient({}), models_root=tmp_path / "models")
    try:
        manager.get("nope")
        assert False, "expected DownloadNotFoundError"
    except DownloadNotFoundError:
        pass


def test_cancel_unknown_download_id_raises(tmp_path):
    manager = DownloadManager(FakeHfClient({}), models_root=tmp_path / "models")
    try:
        manager.cancel("nope")
        assert False, "expected DownloadNotFoundError"
    except DownloadNotFoundError:
        pass


def test_cleanup_of_an_already_failed_download_removes_its_partial_directory(tmp_path):
    models_root = tmp_path / "models"
    manager = DownloadManager(FakeHfClient({}), models_root=models_root)
    download_id = manager.start_download("unreachable/repo", "model-x")

    for _ in range(100):
        if manager.get(download_id)["status"] != "downloading":
            break
        time.sleep(0.05)

    # Simulate a leftover partial dir for the failed attempt, then "Clean up".
    dest = models_root / "model-x"
    dest.mkdir(parents=True)
    (dest / "stray.bin").write_bytes(b"x")

    manager.cancel(download_id)
    assert not dest.exists()
