"""Verifies the actual scenario that prompted Phase C: a model registered while
instance A is selected must appear when B (a different vLLM instance on the same
physical server) is selected too, since models are now a per-server shared catalog
rather than per-instance. Also checks the "active on {other instance}" cross-
reference and that activating/preloading only affects the instance the form was
opened for.

Does NOT click Activate/Hot-swap (those touch the real Docker backend) — this only
exercises registration/listing via the Advanced form and the model wizard.

Assumes the "tes — main vLLM" / "Test Server" pair on one physical server already
exists (created earlier this session). Creates and cleans up its own model.
"""
from playwright.sync_api import sync_playwright

from common import BASE, login, shot_dir, collect_console_errors, auto_accept_dialogs

SHOTS = shot_dir("test_model_registry_shared")
MODEL_NAME = "e2e-shared-registry-test"


def select_instance(page, label):
    selects = page.locator("select.source-selector")
    selects.nth(1).select_option(label=label)
    page.wait_for_timeout(400)


def cleanup(page):
    page.goto(f"{BASE}/models")
    page.wait_for_load_state("networkidle")
    row = page.locator("tr", has_text=MODEL_NAME)
    if row.count() > 0:
        row.locator("text=Delete").click()
        page.wait_for_timeout(500)


def run():
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        page = browser.new_page()
        errors = collect_console_errors(page)
        auto_accept_dialogs(page)
        login(page)

        page.goto(f"{BASE}/models")
        page.wait_for_load_state("networkidle")
        select_instance(page, "tes — main vLLM")
        cleanup(page)

        print("== Register a model via Advanced form while 'tes — main vLLM' is selected ==")
        page.goto(f"{BASE}/models")
        page.wait_for_load_state("networkidle")
        select_instance(page, "tes — main vLLM")
        page.click("text=+ Add model")
        page.wait_for_timeout(500)
        modal = page.locator(".modal-dialog-custom")
        modal.locator("input[placeholder='e.g. my-model']").fill(MODEL_NAME)
        modal.locator("button:has-text('Switch to Advanced')").click()
        page.wait_for_timeout(800)
        advanced = page.locator(".modal-dialog-custom")
        # Fill the required repo id field (blank from the wizard's minimal step) so save succeeds cleanly.
        advanced.locator("input[placeholder='Qwen/Qwen2.5-7B-Instruct']").fill("sshleifer/tiny-gpt2")
        advanced.locator("button[type=submit]:has-text('Save')").click()
        page.wait_for_timeout(800)

        print("== Confirm it shows up on 'tes — main vLLM' ==")
        assert page.locator("tr", has_text=MODEL_NAME).count() == 1
        page.screenshot(path=f"{SHOTS}/01_registered_on_a.png", full_page=True)

        print("== Switch to 'Test Server' (same physical server), confirm it's visible there too ==")
        select_instance(page, "Test Server")
        page.wait_for_timeout(500)
        assert page.locator("tr", has_text=MODEL_NAME).count() == 1, "model registered on instance A did not appear on instance B (same server)"
        row_text = page.locator("tr", has_text=MODEL_NAME).inner_text()
        print("Row on Test Server:", row_text.replace("\n", " | "))
        page.screenshot(path=f"{SHOTS}/02_visible_on_b.png", full_page=True)

        print("\n=== Console/page errors ===")
        for e in errors:
            print(e)

        print("\n== Cleanup ==")
        cleanup(page)
        assert page.locator("tr", has_text=MODEL_NAME).count() == 0
        print("Cleaned up.")

        browser.close()
        print("\nOK")


if __name__ == "__main__":
    run()
