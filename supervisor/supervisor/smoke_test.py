"""Smoke test (P06): after a deploy becomes healthy, prove the model
actually answers -- a canned prompt sent straight to vLLM's own
chat/completions endpoint. Never through any proxy: the smoke prompt must
not show up in Live Traffic (doc 5), and the supervisor has no proxy of
its own to route through anyway.
"""
from __future__ import annotations

import json
import time
import urllib.error
import urllib.request
from dataclasses import dataclass

DEFAULT_PROMPT = "Reply with OK."
DEFAULT_EXPECT_SUBSTRING = "OK"
DEFAULT_TIMEOUT_SECONDS = 30.0
DEFAULT_MAX_TOKENS = 50


@dataclass
class SmokeResult:
    ok: bool
    excerpt: str
    ms: float

    def to_dict(self) -> dict:
        return {"ok": self.ok, "excerpt": self.excerpt, "ms": self.ms}


def run_smoke_test(
    port: int,
    served_model_name: str,
    prompt: str = DEFAULT_PROMPT,
    expect_substring: str = DEFAULT_EXPECT_SUBSTRING,
    timeout: float = DEFAULT_TIMEOUT_SECONDS,
    max_tokens: int = DEFAULT_MAX_TOKENS,
) -> SmokeResult:
    """Pass iff `expect_substring` appears in the model's reply (the DoD's
    "first 50 tokens" is approximated by `max_tokens`, which bounds how
    much the model is even asked to generate). Never raises -- a request
    failure or timeout is just a `fail` result with the error as the
    excerpt, same as a bad answer."""
    url = f"http://127.0.0.1:{port}/v1/chat/completions"
    body = json.dumps({
        "model": served_model_name,
        "messages": [{"role": "user", "content": prompt}],
        "max_tokens": max_tokens,
        "stream": False,
    }).encode("utf-8")
    start = time.monotonic()
    try:
        req = urllib.request.Request(url, data=body, method="POST", headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read()
    except (urllib.error.URLError, OSError, TimeoutError) as exc:
        return SmokeResult(ok=False, excerpt=f"request failed: {exc}", ms=(time.monotonic() - start) * 1000)

    text = _extract_text(raw)
    elapsed_ms = (time.monotonic() - start) * 1000
    return SmokeResult(ok=expect_substring in text, excerpt=text[:200], ms=elapsed_ms)


def _extract_text(raw: bytes) -> str:
    """Tolerant of a streaming-shaped chunk (delta.content) or a plain
    completions response (choices[0].text), even though this module's own
    request always sends stream=false -- defensive against whatever shape
    an actual vLLM (or a differently-configured fake) sends back."""
    try:
        data = json.loads(raw)
    except json.JSONDecodeError:
        return ""
    choices = data.get("choices") or []
    if not choices:
        return ""
    choice = choices[0]
    message = choice.get("message") or {}
    if isinstance(message.get("content"), str):
        return message["content"]
    delta = choice.get("delta") or {}
    if isinstance(delta.get("content"), str):
        return delta["content"]
    if isinstance(choice.get("text"), str):
        return choice["text"]
    return ""


def resolve_smoke_config(smoke: dict | None) -> tuple[str, str] | None:
    """Returns (prompt, expect_substring) to run with, or None to skip.
    Smoke testing is automatic by default (doc 5) -- `smoke` omitted or
    explicitly `null` still runs it with the canned defaults; only an
    explicit `smoke: {skip: true}` turns it off (owner-confirmed
    2026-11-01, resolving an ambiguity in the page's own wording: a JSON
    field that's omitted and one explicitly null are indistinguishable in
    the request model, so "automatic by default" wins over the literal
    "smoke: null ... skips" phrasing)."""
    if smoke is not None and smoke.get("skip"):
        return None
    prompt = (smoke or {}).get("prompt", DEFAULT_PROMPT)
    expect_substring = (smoke or {}).get("expect_substring", DEFAULT_EXPECT_SUBSTRING)
    return prompt, expect_substring
