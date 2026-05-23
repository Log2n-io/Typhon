import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

const DEMO_DIR = path.resolve('../bin/Debug/net10.0/DemoData');

async function openDemo(page: import('@playwright/test').Page, request: import('@playwright/test').APIRequestContext) {
  fs.mkdirSync(DEMO_DIR, { recursive: true });
  fs.writeFileSync(path.join(DEMO_DIR, 'demo.typhon'), '');

  const list = await request.get('http://localhost:5200/api/sessions');
  if (list.ok()) {
    const { sessions = [] } = await list.json();
    for (const s of sessions as Array<{ sessionId: string }>) {
      await request.delete(`http://localhost:5200/api/sessions/${s.sessionId}`, {
        headers: { 'X-Session-Token': s.sessionId },
      });
    }
  }

  const seed = await request.post('http://localhost:5200/api/sessions/file', {
    data: { filePath: 'demo.typhon' },
  });
  const seedJson = await seed.json();
  await request.delete(`http://localhost:5200/api/sessions/${seedJson.sessionId}`, {
    headers: { 'X-Session-Token': seedJson.sessionId },
  });

  await page.addInitScript(() => {
    try { localStorage.clear(); } catch { /* ignore */ }
  });
  await page.goto('/');
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
  await page.getByRole('button', { name: /^open \.typhon file$/i }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByPlaceholder(/path/i).first().fill(DEMO_DIR);
  const demoRow = page.getByText(/^demo\.typhon$/).first();
  await expect(demoRow).toBeVisible({ timeout: 10_000 });
  await demoRow.click();
  await page.getByRole('button', { name: /^open$/i }).click();
  await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 10_000 });
  await expect(page.locator('body')).toContainText(/Storage|DataEngine/i, { timeout: 10_000 });
}

test.describe('Phase 6 — Resource Tree polish', () => {
  test('right-click → context menu shows shell-local items only; no zone-D handoffs or stubs (Stage 0)', async ({ page, request }) => {
    await openDemo(page, request);

    // Right-click the inner div of the row (where our ContextMenuTrigger is mounted via asChild).
    // Targeting [role="treeitem"] directly can miss the trigger if arborist's wrapper captures the
    // event outside our child element.
    const firstRowInner = page.locator('[role="treeitem"] > div').first();
    await firstRowInner.click({ button: 'right' });

    // Shell-local items survive.
    await expect(page.getByRole('menuitem', { name: /^pin$/i })).toBeVisible();
    await expect(page.getByRole('menuitem', { name: /copy path/i })).toBeVisible();
    await expect(page.getByRole('menuitem', { name: /reveal in tree/i })).toBeVisible();
    await expect(page.getByRole('menuitem', { name: /refresh subtree/i })).toBeVisible();
    // Stage 0: handoffs to deactivated views are gated out entirely (Show Component Layout → SchemaLayout,
    // Show in File Map → DbMap), and the always-disabled "Open in …" stubs were removed (PC-6: no broken
    // affordances). They return with their views in later stages — this asserts the reduced shape.
    await expect(page.getByRole('menuitem', { name: /show component layout/i })).toHaveCount(0);
    await expect(page.getByRole('menuitem', { name: /show in file map/i })).toHaveCount(0);
    await expect(page.getByRole('menuitem', { name: /open in data browser/i })).toHaveCount(0);
    await expect(page.getByRole('menuitem', { name: /open in query console/i })).toHaveCount(0);
    await expect(page.getByRole('menuitem', { name: /open in profiler/i })).toHaveCount(0);

    await page.keyboard.press('Escape');
  });

  test('pin via context menu persists; unpin removes it', async ({ page, request }) => {
    await openDemo(page, request);

    // Right-click the inner div of the row (where our ContextMenuTrigger is mounted via asChild).
    // Targeting [role="treeitem"] directly can miss the trigger if arborist's wrapper captures the
    // event outside our child element.
    const firstRowInner = page.locator('[role="treeitem"] > div').first();
    await firstRowInner.click({ button: 'right' });
    await page.getByRole('menuitem', { name: /^pin$/i }).click();

    // Re-open menu → entry should now say "Unpin"
    await firstRowInner.click({ button: 'right' });
    await expect(page.getByRole('menuitem', { name: /^unpin$/i })).toBeVisible();
    await page.getByRole('menuitem', { name: /^unpin$/i }).click();

    await firstRowInner.click({ button: 'right' });
    await expect(page.getByRole('menuitem', { name: /^pin$/i })).toBeVisible();
    await page.keyboard.press('Escape');
  });

  test('Ctrl+/ hides the tree and status bar shows restore affordance', async ({ page, request }) => {
    await openDemo(page, request);

    // Tree filter input is the canary — visible before the toggle.
    await expect(page.getByPlaceholder(/filter resources/i)).toBeVisible();

    await page.keyboard.press('Control+/');

    // Status bar restore button shows up.
    const restoreBtn = page.getByRole('button', { name: /hidden/i });
    await expect(restoreBtn).toBeVisible({ timeout: 3_000 });

    await restoreBtn.click();
    await expect(page.getByPlaceholder(/filter resources/i)).toBeVisible();
  });
});
