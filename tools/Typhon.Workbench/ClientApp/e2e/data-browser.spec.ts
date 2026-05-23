import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

const DEMO_DIR = path.resolve('../bin/Debug/net10.0/DemoData');

// Same demo-open ritual as schema-inspector.spec.ts (Playwright specs don't share modules yet). Boots the Workbench against
// a freshly-opened engine with the dockview shell mounted.
async function openDemo(
  page: import('@playwright/test').Page,
  request: import('@playwright/test').APIRequestContext,
) {
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

  const seed = await request.post('http://localhost:5200/api/sessions/file', { data: { filePath: 'demo.typhon' } });
  const seedJson = await seed.json();
  await request.delete(`http://localhost:5200/api/sessions/${seedJson.sessionId}`, {
    headers: { 'X-Session-Token': seedJson.sessionId },
  });

  await page.addInitScript(() => {
    try { localStorage.clear(); } catch { /* ignore */ }
  });
  await page.goto('/');
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

test.describe('Data Browser — Phase 1', () => {
  test('View → Data Browser opens the panel with an archetype picker', async ({ page, request }) => {
    await openDemo(page, request);

    await page.getByRole('menuitem', { name: /^view$/i }).click();
    const item = page.getByRole('menuitem', { name: /^data browser$/i });
    await expect(item).toBeVisible({ timeout: 5_000 });
    await item.click();

    // Panel-mount canary: the archetype picker is unique to the Entity List panel. (The selected entity's component cards
    // render in the shared Detail pane, not a Data-Browser-owned panel.)
    await expect(page.getByTestId('archetype-picker')).toBeVisible({ timeout: 5_000 });
  });

  test('Palette command "Open Data Browser" opens the panel', async ({ page, request }) => {
    await openDemo(page, request);

    await page.getByRole('button', { name: /open command palette/i }).click();
    const paletteInput = page.getByPlaceholder(/search commands/i);
    await expect(paletteInput).toBeVisible();

    await paletteInput.fill('data browser');
    const command = page.getByRole('option', { name: /open data browser/i });
    await expect(command).toBeVisible();
    await command.click();

    await expect(page.getByTestId('archetype-picker')).toBeVisible({ timeout: 5_000 });
  });

  test('Component Browser context menu → "Open in Data Browser" is enabled', async ({ page, request }) => {
    await openDemo(page, request);

    await page.getByRole('menuitem', { name: /^view$/i }).click();
    await page.getByRole('menuitem', { name: /component browser/i }).click();
    await expect(page.getByPlaceholder(/search components/i)).toBeVisible({ timeout: 5_000 });

    // The demo session may or may not expose component rows (internal engine tables). Branch like the
    // schema-inspector relationships test: only assert the enabled menu item when a row exists.
    const firstRow = page.getByTestId('schema-row').first();
    if (await firstRow.isVisible().catch(() => false)) {
      await firstRow.click({ button: 'right' });
      const item = page.getByRole('menuitem', { name: /open in data browser/i });
      await expect(item).toBeVisible({ timeout: 5_000 });
      await expect(item).toBeEnabled();
      await page.keyboard.press('Escape');
    }
  });
});
