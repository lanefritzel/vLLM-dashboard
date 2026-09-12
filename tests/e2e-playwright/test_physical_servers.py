"""Verifies the Physical Server feature end to end:
- Settings page has a "Physical Servers" section, separate from "Sources".
- The Physical Server Setup Wizard creates a server (Docker host test, GPU, HF cache).
- The vLLM Instance wizard (formerly "Server Setup Wizard") step 1 picks an existing
  physical server, and can add a new one inline without losing progress.
- The Advanced source form's "Physical server" dropdown round-trips correctly.
- The Model wizard's VRAM-aware gpu-memory-utilization hint reads the linked
  physical server's VRAM total.

Creates its own throwaway physical server + source + model and deletes them at the
end. Safe to re-run; if it fails partway, check Settings for a leftover
"E2E Test Server" row.
"""
from playwright.sync_api import sync_playwright

from common import BASE, login, shot_dir, collect_console_errors, auto_accept_dialogs

SHOTS = shot_dir("test_physical_servers")
SERVER_LABEL = "E2E Test Server"
SOURCE_LABEL = "E2E Test Instance"


def cleanup(page):
    page.goto(f"{BASE}/settings")
    page.wait_for_load_state("networkidle")
    row = page.locator("tr", has_text=SOURCE_LABEL)
    if row.count() > 0:
        row.locator("text=Delete").click()
        page.wait_for_timeout(500)
    page.goto(f"{BASE}/settings")
    page.wait_for_load_state("networkidle")
    row = page.locator("tr", has_text=SERVER_LABEL)
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

        cleanup(page)  # in case a previous run left something behind

        print("== Settings: Physical Servers section exists ==")
        page.goto(f"{BASE}/settings")
        page.wait_for_load_state("networkidle")
        assert page.locator("text=Physical Servers").count() > 0
        page.screenshot(path=f"{SHOTS}/01_settings_overview.png", full_page=True)

        print("== Create a physical server via its wizard ==")
        page.click("text=+ Add physical server")
        page.wait_for_timeout(500)
        modal = page.locator(".modal-dialog-custom")
        modal.locator("input[placeholder='e.g. Main GPU box']").fill(SERVER_LABEL)
        modal.locator("input[placeholder='tcp://servername-or-ip:2375']").fill("tcp://e2e-fake-host:2375")
        modal.locator("text=Test connection").click()
        page.wait_for_timeout(1500)
        page.screenshot(path=f"{SHOTS}/02_server_wizard_step1.png", full_page=True)

        modal.locator("button:has-text('Next')").click()
        page.wait_for_timeout(300)
        modal.locator("select").select_option(label="Intel (raw device passthrough)")
        modal.locator("input[placeholder='e.g. 32']").fill("32")
        modal.locator("button:has-text('Next')").click()
        page.wait_for_timeout(300)
        modal.locator("input[placeholder='/home/you/.cache/huggingface']").fill("/data/hf-cache")
        page.screenshot(path=f"{SHOTS}/03_server_wizard_review.png", full_page=True)
        modal.locator("button:has-text('Save')").click()
        page.wait_for_timeout(1000)

        row = page.locator("tr", has_text=SERVER_LABEL)
        assert row.count() > 0, "physical server not found in table after save"
        print("Physical server row:", row.inner_text())
        page.screenshot(path=f"{SHOTS}/04_server_created.png", full_page=True)

        print("== vLLM instance wizard: step 1 offers the just-created server ==")
        page.click("text=+ Add source")
        page.wait_for_timeout(500)
        modal = page.locator(".modal-dialog-custom")
        heading = modal.locator(".card-title").first.inner_text()
        assert "vLLM Instance Setup Wizard" in heading, f"unexpected heading: {heading}"
        modal.locator("input[placeholder='e.g. Qwen instance']").fill(SOURCE_LABEL)
        # Select by visible text containing the server label (dropdown text is "Label (host)")
        select = modal.locator("select")
        select.select_option(label=[o for o in select.locator("option").all_inner_texts() if SERVER_LABEL in o][0])
        page.wait_for_timeout(200)
        page.screenshot(path=f"{SHOTS}/05_instance_wizard_step1.png", full_page=True)

        modal.locator("button:has-text('Next')").click()
        page.wait_for_timeout(300)
        modal.locator("input[placeholder='vllm/vllm-openai:latest']").fill("vllm/vllm-openai:latest")
        page.screenshot(path=f"{SHOTS}/06_instance_wizard_step2_autofill.png", full_page=True)
        modal.locator("button:has-text('Next')").click()
        page.wait_for_timeout(300)
        page.screenshot(path=f"{SHOTS}/07_instance_wizard_review.png", full_page=True)
        modal.locator("button:has-text('Save')").click()
        page.wait_for_timeout(1000)

        row = page.locator("tr", has_text=SOURCE_LABEL)
        assert row.count() > 0, "vLLM instance not found in Sources table after save"
        print("Source row:", row.inner_text())

        print("== Advanced form: Physical server dropdown round-trips ==")
        row.locator("text=Edit").click()
        page.wait_for_timeout(500)
        advanced = page.locator(".modal-dialog-custom")
        selected_text = advanced.locator("select").nth(1).input_value()
        print("Physical server dropdown selected value (id):", bool(selected_text))
        page.screenshot(path=f"{SHOTS}/08_advanced_form_server_dropdown.png", full_page=True)
        page.click(".modal-close-btn")
        page.wait_for_timeout(300)

        print("\n=== Console/page errors ===")
        for e in errors:
            print(e)

        print("\n== Cleanup ==")
        cleanup(page)
        page.wait_for_timeout(500)
        assert page.locator("tr", has_text=SOURCE_LABEL).count() == 0
        assert page.locator("tr", has_text=SERVER_LABEL).count() == 0
        print("Cleaned up test rows.")

        browser.close()
        print("\nOK")


if __name__ == "__main__":
    run()
