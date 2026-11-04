"""Model downloads (P08): the supervisor is the single egress point to
Hugging Face. Models live on a host-mounted volume, never in the
container -- host `<models_root>/<slug>/` <-> container `/models/<slug>/`
(doc 6). One download at a time (a second POST while one is in flight is
rejected, not queued); `HF_TOKEN` comes from the environment only, and is
never put into a request/response body or logged anywhere in this module.
"""
from __future__ import annotations

import json
import shutil
import threading
import time
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Optional

DEFAULT_MODELS_ROOT = Path("/models")
CHUNK_SIZE = 1024 * 1024  # 1 MiB -- fine progress granularity without excessive overhead


@dataclass
class RemoteFile:
    path: str
    size: int

    def to_dict(self) -> dict:
        return {"path": self.path, "size": self.size}


class HfRepoError(Exception):
    """A readable, user-facing failure talking to Hugging Face (DoD:
    "download_failed with a readable message"). `status` is the HTTP-ish
    status when known (401 gated, 404 not found), None for e.g. a plain
    network failure."""

    def __init__(self, message: str, status: Optional[int] = None) -> None:
        super().__init__(message)
        self.status = status


class HfClient:
    """Real client -- wraps `huggingface_hub`, imported lazily (inside
    each method, not at module level) so this module, and anything using
    only FakeHfClient in tests, loads fine in an environment that doesn't
    have the package installed. The real vLLM image already carries it as
    vllm's own dependency -- needs real-image confirmation (not yet
    verified against the actual Intel host image; see plan/PROGRESS.md's
    "needs real-hardware confirmation" list)."""

    def __init__(self, token: Optional[str] = None) -> None:
        self._token = token

    def list_files(self, repo_id: str) -> list[RemoteFile]:
        from huggingface_hub import HfApi
        from huggingface_hub.utils import HfHubHTTPError, RepositoryNotFoundError

        try:
            info = HfApi().model_info(repo_id, token=self._token, files_metadata=True)
        except RepositoryNotFoundError as exc:
            raise HfRepoError(f"repository not found: {repo_id}", status=404) from exc
        except HfHubHTTPError as exc:
            status = getattr(getattr(exc, "response", None), "status_code", None)
            raise HfRepoError(str(exc), status=status) from exc
        except Exception as exc:  # network/DNS/etc -- unreachable
            raise HfRepoError(f"could not reach Hugging Face: {exc}") from exc
        return [RemoteFile(path=s.rfilename, size=s.size or 0) for s in (info.siblings or [])]

    def get_config(self, repo_id: str) -> Optional[dict]:
        from huggingface_hub import hf_hub_download

        try:
            path = hf_hub_download(repo_id, "config.json", token=self._token)
            return json.loads(Path(path).read_text())
        except Exception:
            return None  # "downloaded but config unreadable" -- not a download failure (doc 6)

    def download_file(
        self, repo_id: str, filename: str, dest_dir: str, progress_cb: Optional[Callable[[int], None]] = None,
    ) -> str:
        from huggingface_hub import hf_hub_download

        # huggingface_hub's own resume (.incomplete + etag metadata, doc
        # 6) handles a file that's only partially there on disk already.
        # It doesn't expose a byte-level progress callback, so progress_cb
        # only gets one lump-sum call here, after the file is fully on
        # disk -- DownloadManager's files_done/total_bytes counts still
        # end up correct, just without fine-grained mid-file progress for
        # THIS client (FakeHfClient, used in tests, reports byte-level
        # chunks instead, for the "monotonic progress_pct mid-download"
        # DoD bullet).
        try:
            path = hf_hub_download(repo_id, filename, local_dir=dest_dir, token=self._token)
        except Exception as exc:
            raise HfRepoError(f"download failed for {filename}: {exc}") from exc
        if progress_cb:
            progress_cb(Path(path).stat().st_size)
        return path


class FakeHfClient(HfClient):
    """Serves a local directory as a stand-in Hugging Face repo (P08
    tests) -- no network involved. `repos` maps repo_id -> source
    directory on disk. Three reserved repo ids simulate the failure modes
    doc 6 names without a real HTTP call: "gated/repo" (401),
    "nonexistent/repo" (404, also the default for anything not in
    `repos`), "unreachable/repo" (network failure, no status)."""

    def __init__(self, repos: dict[str, Path], chunk_delay: float = 0.0) -> None:
        super().__init__(token=None)
        self._repos = repos
        self._chunk_delay = chunk_delay

    def list_files(self, repo_id: str) -> list[RemoteFile]:
        if repo_id == "gated/repo":
            raise HfRepoError("access to this repo is gated", status=401)
        if repo_id == "unreachable/repo":
            raise HfRepoError("could not reach Hugging Face: simulated network failure")
        base = self._repos.get(repo_id)
        if base is None:
            raise HfRepoError(f"repository not found: {repo_id}", status=404)
        return [
            RemoteFile(path=str(p.relative_to(base)), size=p.stat().st_size)
            for p in sorted(base.rglob("*")) if p.is_file()
        ]

    def get_config(self, repo_id: str) -> Optional[dict]:
        base = self._repos.get(repo_id)
        if base is None:
            return None
        cfg_path = base / "config.json"
        if not cfg_path.exists():
            return None
        try:
            return json.loads(cfg_path.read_text())
        except json.JSONDecodeError:
            return None

    def download_file(
        self, repo_id: str, filename: str, dest_dir: str, progress_cb: Optional[Callable[[int], None]] = None,
    ) -> str:
        src = self._repos[repo_id] / filename
        dest = Path(dest_dir) / filename
        dest.parent.mkdir(parents=True, exist_ok=True)
        with src.open("rb") as f_in, dest.open("wb") as f_out:
            while True:
                chunk = f_in.read(CHUNK_SIZE)
                if not chunk:
                    break
                f_out.write(chunk)
                if progress_cb:
                    progress_cb(len(chunk))
                if self._chunk_delay:
                    time.sleep(self._chunk_delay)
        return str(dest)


class DownloadInProgressError(Exception):
    def __init__(self, download_id: str) -> None:
        super().__init__(f"a download is already in progress: {download_id}")
        self.download_id = download_id


class DownloadNotFoundError(Exception):
    pass


@dataclass
class DownloadState:
    download_id: str
    repo_id: str
    slug: str
    filename: Optional[str]
    dest_dir: str
    status: str = "downloading"  # "downloading" | "done" | "download_failed" | "cancelled"
    files_total: int = 0
    files_done: int = 0
    total_bytes: int = 0
    downloaded_bytes: int = 0
    error: Optional[str] = None
    path: Optional[str] = None
    size_bytes: Optional[int] = None
    config: Optional[dict] = None
    load_format: Optional[str] = None
    started_at: float = field(default_factory=time.monotonic)
    finished_at: Optional[float] = None

    def to_dict(self) -> dict:
        elapsed = max((self.finished_at or time.monotonic()) - self.started_at, 1e-6)
        speed_bps = self.downloaded_bytes / elapsed
        progress_pct = (self.downloaded_bytes / self.total_bytes * 100) if self.total_bytes else 0.0
        remaining = max(self.total_bytes - self.downloaded_bytes, 0)
        eta_seconds = (remaining / speed_bps) if speed_bps > 0 else None
        result = {
            "status": self.status,
            "files_total": self.files_total,
            "files_done": self.files_done,
            "total_bytes": self.total_bytes,
            "downloaded_bytes": self.downloaded_bytes,
            "progress_pct": round(progress_pct, 2),
            "speed_bps": round(speed_bps, 2),
            "eta_seconds": round(eta_seconds, 2) if eta_seconds is not None else None,
        }
        if self.status == "done":
            result.update({
                "path": self.path, "size_bytes": self.size_bytes,
                "config": self.config, "load_format": self.load_format,
            })
        elif self.status == "download_failed":
            result["error"] = self.error
        return result


class DownloadManager:
    def __init__(self, hf_client: HfClient, models_root: Path | str = DEFAULT_MODELS_ROOT) -> None:
        self._hf_client = hf_client
        self._models_root = Path(models_root)
        self._lock = threading.Lock()
        self._current: Optional[DownloadState] = None
        self._downloads: dict[str, DownloadState] = {}
        self._cancel_event: Optional[threading.Event] = None
        self._thread: Optional[threading.Thread] = None

    def dest_dir(self, slug: str) -> Path:
        return self._models_root / slug

    def inspect(self, repo_id: str) -> dict:
        files = self._hf_client.list_files(repo_id)
        config = self._hf_client.get_config(repo_id)
        self._models_root.mkdir(parents=True, exist_ok=True)  # disk_usage needs it to exist
        free_bytes = shutil.disk_usage(self._models_root).free
        return {"files": [f.to_dict() for f in files], "config": config, "free_bytes": free_bytes}

    def start_download(self, repo_id: str, slug: str, filename: Optional[str] = None) -> str:
        with self._lock:
            if self._current is not None and self._current.status == "downloading":
                raise DownloadInProgressError(self._current.download_id)
            download_id = str(uuid.uuid4())
            state = DownloadState(
                download_id=download_id, repo_id=repo_id, slug=slug, filename=filename,
                dest_dir=str(self.dest_dir(slug)),
            )
            self._current = state
            self._downloads[download_id] = state
            cancel_event = threading.Event()
            self._cancel_event = cancel_event
            self._thread = threading.Thread(target=self._run, args=(state, cancel_event), daemon=True)
            self._thread.start()
        return download_id

    def get(self, download_id: str) -> dict:
        state = self._downloads.get(download_id)
        if state is None:
            raise DownloadNotFoundError(f"no such download: {download_id}")
        return state.to_dict()

    def cancel(self, download_id: str) -> None:
        """DELETE /admin/downloads/{id}: cancel + remove the partial
        directory (doc 6). Also doubles as "Clean up" for an
        already-finished (failed/cancelled) download -- a failure leaves
        its partial files on disk on purpose, for Retry/resume, so this is
        the one place that actually removes them."""
        state = self._downloads.get(download_id)
        if state is None:
            raise DownloadNotFoundError(f"no such download: {download_id}")
        if state.status == "downloading":
            if self._cancel_event is not None:
                self._cancel_event.set()
            if self._thread is not None:
                self._thread.join(timeout=30)
            if state.status == "downloading":  # thread didn't notice in time -- mark it anyway
                state.status = "cancelled"
                state.finished_at = time.monotonic()
        shutil.rmtree(state.dest_dir, ignore_errors=True)

    def _run(self, state: DownloadState, cancel_event: threading.Event) -> None:
        try:
            files = self._hf_client.list_files(state.repo_id)
            if state.filename is not None:
                files = [f for f in files if f.path == state.filename]
                if not files:
                    raise HfRepoError(f"{state.filename} not found in {state.repo_id}", status=404)
            state.files_total = len(files)
            state.total_bytes = sum(f.size for f in files)

            dest = Path(state.dest_dir)
            dest.mkdir(parents=True, exist_ok=True)

            for f in files:
                if cancel_event.is_set():
                    state.status = "cancelled"
                    state.finished_at = time.monotonic()
                    return
                target = dest / f.path
                if target.exists() and target.stat().st_size == f.size:
                    # Retry/resume (doc 6): a file this size is already
                    # fully there from a previous attempt -- skip
                    # re-downloading it, just count it toward progress.
                    state.downloaded_bytes += f.size
                    state.files_done += 1
                    continue

                def progress_cb(nbytes: int, _state=state) -> None:
                    _state.downloaded_bytes += nbytes

                self._hf_client.download_file(state.repo_id, f.path, state.dest_dir, progress_cb=progress_cb)
                state.files_done += 1

            if cancel_event.is_set():
                state.status = "cancelled"
                state.finished_at = time.monotonic()
                return

            is_gguf = state.filename is not None and state.filename.lower().endswith(".gguf")
            if is_gguf:
                gguf_path = dest / state.filename
                state.path = str(gguf_path)
                state.size_bytes = gguf_path.stat().st_size
                state.config = None
                state.load_format = "gguf"
            else:
                state.path = str(dest)
                state.size_bytes = sum(f.size for f in files)
                state.config = self._hf_client.get_config(state.repo_id)
                state.load_format = None

            state.status = "done"
            state.finished_at = time.monotonic()
        except HfRepoError as exc:
            state.status = "download_failed"
            state.error = str(exc)
            state.finished_at = time.monotonic()
        except Exception as exc:  # disk full, permissions, etc -- never crash the thread silently
            state.status = "download_failed"
            state.error = str(exc)
            state.finished_at = time.monotonic()
