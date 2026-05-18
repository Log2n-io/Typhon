import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

const DEMO_DIR = path.resolve('../bin/Debug/net10.0/DemoData');

// Shared preamble — opens demo.typhon via the Connect Dialog. Mirrors dbmap-basic.spec.ts.
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
    try {
      localStorage.clear();
    } catch {
      /* ignore */
    }
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
}

test.describe('Module 15 — Database File Map (A2 drill-down canary)', () => {
  test('open → detail encoding fetches tiles → zoom drills into the page band', async ({ page, request }) => {
    await openDemo(page, request);

    await page.keyboard.press('Control+k');
    await page.getByPlaceholder(/search commands/i).fill('Database File Map');
    await page.getByText(/Toggle View Database File Map/i).first().click();

    const panel = page.getByTestId('dbmap-panel');
    await expect(panel).toBeVisible();
    const canvas = page.getByTestId('dbmap-canvas');
    await expect(canvas).toBeVisible();
    await expect(page.getByTestId('dbmap-breadcrumb')).toContainText(/pages/i, { timeout: 10_000 });

    // Switching to a detail encoding must trigger an on-demand detail-tile fetch (the A2 detail tier).
    const detailFetch = page.waitForResponse(
      (r) => r.url().includes('/dbmap/region/detail') && r.status() === 200,
      { timeout: 10_000 },
    );
    await page.getByTestId('dbmap-encoding').selectOption('fillDensity');
    await detailFetch;
    await expect(canvas).toBeVisible();

    // Zoom in hard toward the chunk band — the drill must not throw, and crossing into L3 must trigger an
    // on-demand per-page detail fetch (the L3 chunk-grid tier).
    const pageFetch = page.waitForResponse((r) => /\/dbmap\/page\/\d+/.test(r.url()), { timeout: 15_000 });
    const box = await canvas.boundingBox();
    expect(box).not.toBeNull();
    if (box) {
      // Zoom toward the populated upper-left corner — page 0 of the Hilbert curve sits there; the grid centre
      // is the inert tail. A moderate zoom lands in the L3 chunk band rather than blasting past every page.
      const cx = box.x + box.width * 0.18;
      const cy = box.y + box.height * 0.18;
      await page.mouse.move(cx, cy);
      for (let i = 0; i < 30; i++) {
        await page.mouse.wheel(0, -240);
      }
    }
    const pageResponse = await pageFetch;
    expect(pageResponse.status()).toBe(200);
    await expect(canvas).toBeVisible();

    // Back to a coarse encoding — recolor must still be error-free.
    await page.getByTestId('dbmap-encoding').selectOption('pageType');
    await expect(canvas).toBeVisible();
  });
});
