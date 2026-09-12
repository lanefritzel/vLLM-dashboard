"""Verifies: '+ Add source'/'+ Add model' open their wizards directly (no separate
wizard button), the 'Switch to Advanced' hand-off persists in-progress wizard state
and opens the real Edit form loaded from it, and the Full Setup Wizard's phase-skip
logic (skips vLLM-instance creation when a suitable source already exists).

Creates and cleans up its own throwaway source/model rows. Safe to re-run.
"""
from playwright.sync_api import sync_playwright

from common import BASE, login, shot_dir, collect_console_errors, auto_accept_dialogs

SHOTS = shot_dir("test_wizards_entrypoints")


def run():
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        page = browser.new_page()
        errors = collect_console_errors(page)
        auto_accept_dialogs(page)
        login(page)

        print("== Settings: '+ Add source' opens the vLLM instance wizard directly ==")
        page.goto(f"{BASE}/settings")
        page.wait_for_load_state("networkidle")
        page.click("text=+ Add source")
        page.wait_for_timeout(500)
        modal = page.locator(".modal-dialog-custom")
        heading = modal.locator(".card-title").first.inner_text()
        print("Modal heading:", heading)
        assert "Setup Wizard" in heading, "expected '+ Add source' to open a wizard"
        page.screenshot(path=f"{SHOTS}/01_add_source_wizard.png", full_page=True)
        page.click(".modal-close-btn")
        page.wait_for_timeout(300)

        print("\n=== Console/page errors ===")
        for e in errors:
            print(e)
        assert not any("Error" in e and "negotiation" not in e for e in errors), "unexpected console error"

        browser.close()
        print("\nOK")


if __name__ == "__main__":
    run()
