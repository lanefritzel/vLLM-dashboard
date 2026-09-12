"""Shared helpers for the ad hoc Playwright scripts in this folder. Not a pytest
suite — these are run directly (`python3 test_whatever.py`) against a local dev
instance while building/reviewing a feature. See README.md for setup.
"""
import os

BASE = os.environ.get("DASHBOARD_BASE_URL", "http://localhost:8080")
LOGIN_EMAIL = os.environ.get("DASHBOARD_TEST_EMAIL", "e2etemp@test.local")
LOGIN_PASSWORD = os.environ.get("DASHBOARD_TEST_PASSWORD", "TestPassw0rd!23")

SHOT_ROOT = os.path.join(os.path.dirname(__file__), ".shots")


def shot_dir(script_name):
    d = os.path.join(SHOT_ROOT, script_name)
    os.makedirs(d, exist_ok=True)
    return d


def login(page):
    page.goto(f"{BASE}/Account/Login")
    page.fill("#Input\\.Email", LOGIN_EMAIL)
    page.fill("#Input\\.Password", LOGIN_PASSWORD)
    page.click("button[type=submit]")
    page.wait_for_load_state("networkidle")


def collect_console_errors(page):
    """Attach console/page error listeners; returns the list they append to."""
    errors = []
    page.on("console", lambda m: errors.append(f"[console] {m.text}") if m.type == "error" else None)
    page.on("pageerror", lambda e: errors.append(f"[pageerror] {e}"))
    return errors


def auto_accept_dialogs(page):
    """Accept every JS confirm()/alert() automatically — most wizard flows use
    confirm() for the 'save incomplete setup anyway?' prompt."""
    page.on("dialog", lambda d: d.accept())
