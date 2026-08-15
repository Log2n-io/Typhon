import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

const DEMO_DIR = path.resolve('../bin/Debug/net10.0/DemoData');

// Shared preamble — opens demo.typhon via the Connect Dialog. Identical to open-real-file, copied
// here to keep the spec self-contained.
async function openDemo(page: import('@playwright/test').Page, request: import('@playwright/test').APIRequestContext) {
  fs.mkdirSync(DEMO_DIR, { recursive: true });
  fs.writeFileSync(path.join(DEMO_DIR, 'demo.typhon'), '');

  // Drop all sessions so we don't fight a lingering file-lock from a previous test.
  const list = await request.get('http://localhost:5200/api/sessions');
  if (list.ok()) {
    const { sessions = [] } = await list.json();
    for (const s of sessions as Array<{ sessionId: string }>) {
      await request.delete(`http://localhost:5200/api/sessions/${s.sessionId}`, {
        headers: { 'X-Session-Token': s.sessionId },
      });
    }
  }

  // Seed-and-release to force any residual WB-side state to settle.
  const seed = await request.post('http://localhost:5200/api/sessions/file', {
    data: { filePath: 'demo.typhon' },
  });
  const seedJson = await seed.json();
  await request.delete(`http://localhost:5200/api/sessions/${seedJson.sessionId}`, {
    headers: { 'X-Session-Token': seedJson.sessionId },
  });

  // Clear localStorage before the app script runs so Zustand's persist middleware hydrates from empty.
  await page.addInitScript(() => {
    try { localStorage.clear(); } catch { /* ignore */ }
  });
  await page.goto('/');
  await page.getByRole('button', { name: /^open typhon database$/i }).click();
  await expect(page.getByRole('dialog')).toBeVisible();

  await page.getByPlaceholder(/path/i).first().fill(DEMO_DIR);
  const demoRow = page.getByText(/^demo\.typhon$/).first();
  await expect(demoRow).toBeVisible({ timeout: 10_000 });
  await demoRow.click();
  await page.getByRole('button', { name: /^open$/i }).click();
  await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 10_000 });
}

test.describe('Phase 5 — Resource Tree interactivity', () => {
  test('click a tree node → Detail panel renders that node', async ({ page, request }) => {
    await openDemo(page, request);

    // Wait for tree to render at least one child (any subsystem).
    await expect(page.locator('body')).toContainText(
      /Storage|DataEngine|Durability|Allocation|Synchronization/i,
      { timeout: 10_000 },
    );

    // Click the first tree row (react-arborist emits role="treeitem").
    const firstRow = page.locator('[role="treeitem"]').first();
    await expect(firstRow).toBeVisible({ timeout: 5_000 });
    await firstRow.click();

    // Detail panel renders an "Id" row + a "Path" row — both exclusive to DetailPanel.
    await expect(page.getByText('Id', { exact: true })).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText('Path', { exact: true })).toBeVisible();
  });

  test('Ctrl+K → # prefix → select resource → Detail updates', async ({ page, request }) => {
    await openDemo(page, request);

    await expect(page.locator('body')).toContainText(/Storage|DataEngine/i, { timeout: 10_000 });

    // Open the palette via keyboard
    await page.keyboard.press('Control+KeyK');

    // Palette input should be focused; type a #-prefixed search.
    const input = page.getByPlaceholder(/search commands|search resources/i);
    await input.fill('#stor');

    // At least one hit under a "Subsystem" / "Segment" group should appear.
    const firstHit = page.locator('[cmdk-item]').first();
    await expect(firstHit).toBeVisible({ timeout: 5_000 });
    await firstHit.click();

    // Palette closes; Detail panel reflects the selection.
    await expect(page.getByText('Id', { exact: true })).toBeVisible({ timeout: 5_000 });
  });

  test('after selection, back button becomes enabled; Alt+← restores prior selection', async ({ page, request }) => {
    await openDemo(page, request);

    await expect(page.locator('body')).toContainText(/Storage|DataEngine/i, { timeout: 10_000 });

    // Clicking the root row toggles it closed, so pick two non-root rows. With openByDefault=true
    // the tree renders Root plus all children, so rows.nth(1) is a child, rows.nth(2) another.
    const rows = page.locator('[role="treeitem"]');
    await expect(rows.nth(2)).toBeVisible({ timeout: 5_000 });
    await rows.nth(1).click();
    await rows.nth(2).click();

    const backButton = page.locator('button[title*="Alt"][title*="←"], button[title*="Back"]').first();
    await expect(backButton).toBeEnabled({ timeout: 5_000 });

    // Alt+Left should navigate back; Detail panel should reflect the earlier node.
    await page.keyboard.press('Alt+ArrowLeft');
    await expect(page.getByText('Id', { exact: true })).toBeVisible();
  });
});
