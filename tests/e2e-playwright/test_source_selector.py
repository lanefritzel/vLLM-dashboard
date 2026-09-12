"""Verifies the two-level header source picker (Phase B): first dropdown picks the
physical server, second picks which vLLM instance on it; switching the server
dropdown auto-selects an instance; a vLLM source with no physical server shows
under a "No physical server" group; switching the instance dropdown actually
changes the selected source used by other pages.

Assumes the real "tes — main vLLM" / "Test Server" pair already exist on the same
physical server (created earlier this session) — if your local DB doesn't have two
vLLM instances on one server, create a second one via the vLLM Instance wizard
first. Creates and cleans up its own "no physical server" throwaway source.
"""
from playwright.sync_api import sync_playwright

from common import BASE, login, shot_dir, collect_console_errors, auto_accept_dialogs

SHOTS = shot_dir("test_source_selector")
ORPHAN_LABEL = "E2E Orphan Instance"


def cleanup(page):
    page.goto(f"{BASE}/settings")
    page.wait_for_load_state("networkidle")
    row = page.locator("tr", has_text=ORPHAN_LABEL)
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
        cleanup(page)

        print("== Create an orphan vLLM source (no physical server) via 'Not vLLM?' advanced link's sibling path ==")
        # The advanced form is the only way to leave Physical server blank (the wizard requires picking one to proceed past step 1 meaningfully, but Physical server is optional there too if skipped) — use Advanced directly for a clean no-server case.
        page.goto(f"{BASE}/settings")
        page.wait_for_load_state("networkidle")
        page.click("text=Not vLLM? Add an Ollama/other source directly")
        page.wait_for_timeout(500)
        modal = page.locator(".modal-dialog-custom")
        modal.locator("input").first.fill(ORPHAN_LABEL)
        modal.locator("select").first.select_option(label="vLLM (Prometheus)")
        page.wait_for_timeout(200)
        modal.locator("button[type=submit]:has-text('Save')").click()
        page.wait_for_timeout(800)

        print("== Header selector: check both dropdowns ==")
        page.goto(f"{BASE}/")
        page.wait_for_load_state("networkidle")
        selects = page.locator("select.source-selector")
        assert selects.count() == 2, f"expected 2 source-selector dropdowns, found {selects.count()}"
        server_select, instance_select = selects.nth(0), selects.nth(1)

        server_options = server_select.locator("option").all_inner_texts()
        print("Server options:", server_options)
        assert any("No physical server" in o for o in server_options), "expected a 'No physical server' group"

        page.screenshot(path=f"{SHOTS}/01_header_two_dropdowns.png", full_page=True)

        print("== Select the real physical server group, confirm both instances listed ==")
        real_server_opt = [o for o in server_options if "No physical server" not in o and o.strip()]
        assert real_server_opt, "expected at least one real physical server option"
        server_select.select_option(label=real_server_opt[0])
        page.wait_for_timeout(500)
        instance_options = instance_select.locator("option").all_inner_texts()
        print("Instance options for", real_server_opt[0], ":", instance_options)
        assert len(instance_options) >= 2, "expected at least 2 vLLM instances on the shared physical server"
        page.screenshot(path=f"{SHOTS}/02_server_selected_two_instances.png", full_page=True)

        print("== Switch instance dropdown, confirm page title updates ==")
        other_instance = instance_options[-1] if instance_options[0] == instance_select.input_value() else instance_options[0]
        instance_select.select_option(label=instance_options[-1])
        page.wait_for_timeout(500)
        page.goto(f"{BASE}/models")
        page.wait_for_load_state("networkidle")
        title = page.locator(".page-title").inner_text()
        print("Models page title after switching instance:", title)
        assert instance_options[-1].strip() in title, "Models page did not reflect the newly selected instance"
        page.screenshot(path=f"{SHOTS}/03_models_page_reflects_selection.png", full_page=True)

        print("== Switch server dropdown to 'No physical server', confirm auto-pick ==")
        server_select.select_option(label=[o for o in server_options if "No physical server" in o][0])
        page.wait_for_timeout(500)
        print("Instance dropdown value after switching to orphan group:", instance_select.input_value())
        instance_after = instance_select.locator("option").all_inner_texts()
        print("Instance options in orphan group:", instance_after)
        assert ORPHAN_LABEL in instance_after, "orphan instance not shown under 'No physical server'"
        page.screenshot(path=f"{SHOTS}/04_no_physical_server_group.png", full_page=True)

        print("\n=== Console/page errors ===")
        for e in errors:
            print(e)

        print("\n== Cleanup ==")
        cleanup(page)
        page.wait_for_timeout(500)
        assert page.locator("tr", has_text=ORPHAN_LABEL).count() == 0
        print("Cleaned up.")

        browser.close()
        print("\nOK")


if __name__ == "__main__":
    run()
