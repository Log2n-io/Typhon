import { test, expect } from '@playwright/test';
import { seedDemoFile } from './_session';
import fs from 'node:fs';
import path from 'node:path';

// Resolve the server's working-dir DemoData so we can type it into the in-app FileBrowser.
// ClientApp is at tools/Typhon.Workbench/ClientApp; server writes to ../bin/Debug/net10.0/DemoData.
const DEMO_DIR = path.resolve('../bin/Debug/net10.0/DemoData');

test.describe('Phase 4 — Connect Dialog', () => {
  // #621 — two entry modes. "Open .typhon-trace" is gone: a capture is reached through the database it was recorded
  // against, so offering it as a peer entry point would advertise a third mode that no longer exists.
  test('Welcome shows the two entry modes (+ recents shortcut)', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('button', { name: /^open \.typhon file$/i })).toBeVisible();
    await expect(page.getByRole('button', { name: /^attach to engine$/i })).toBeVisible();
    await expect(page.getByRole('button', { name: /^recent files$/i })).toBeVisible();
    await expect(page.getByRole('button', { name: /^open \.typhon-trace$/i })).toHaveCount(0);
  });

  test('Recent Files button opens dialog on Recent tab with empty state', async ({ page }) => {
    await page.goto('/');
    await page.getByRole('button', { name: /^recent files$/i }).click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await expect(page.getByRole('tab', { name: /recent/i })).toHaveAttribute('data-state', 'active');
    await expect(page.getByText(/no recent files/i)).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).not.toBeVisible();
  });

  test('Attach button opens dialog on Attach tab with live endpoint form', async ({ page }) => {
    // Attach shipped in Phase 1b — the old "coming soon" stub is gone. This test now verifies
    // the real form chrome: endpoint input with default placeholder + an Attach submit button.
    // Real attach-to-mock end-to-end coverage lives in profiler-attach-connect.spec.ts.
    await page.goto('/');
    await page.getByRole('button', { name: /^attach to engine$/i }).click();
    await expect(page.getByRole('tab', { name: /attach/i })).toHaveAttribute('data-state', 'active');
    await expect(page.getByPlaceholder('localhost:9100')).toBeVisible();
    await expect(page.getByRole('button', { name: /^attach$/i })).toBeVisible();
  });

  test('Open File → browse to DemoData → pick demo.typhon → open → tree renders', async ({ page, request }) => {
    // A Typhon database is a bundle DIRECTORY. This used to hand-write a 0-byte `demo.typhon` marker file, which the
    // engine rejects outright ("a file exists at the bundle path") — a leftover from the pre-bundle layout where the
    // engine wrote `demo.bin` and the UI picked a separate marker. `seedDemoFile` creates and initialises the real
    // bundle through the API (and drops its session, releasing the handle), which is what this preamble wanted.
    await seedDemoFile(request);

    await page.goto('/');
    await page.getByRole('button', { name: /^open \.typhon file$/i }).click();
    await expect(page.getByRole('dialog')).toBeVisible();

    // Navigate the FileBrowser to the demo directory by typing into the breadcrumb input.
    const pathInput = page.getByPlaceholder(/path/i).first();
    await pathInput.fill(DEMO_DIR);

    // Wait for the listing to show the demo file; click it to select.
    const demoRow = page.getByText(/^demo\.typhon$/).first();
    await expect(demoRow).toBeVisible({ timeout: 10_000 });
    await demoRow.click();

    // Open button becomes enabled; click it.
    const openBtn = page.getByRole('button', { name: /^open$/i });
    await expect(openBtn).toBeEnabled();
    await openBtn.click();

    // Dialog closes; tree renders engine subsystems.
    await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 10_000 });
    await expect(page.locator('body')).toContainText(
      /Storage|DataEngine|Durability|Allocation|Synchronization/i,
      { timeout: 10_000 },
    );
  });
});
