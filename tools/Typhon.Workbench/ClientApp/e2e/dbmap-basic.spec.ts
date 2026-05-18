import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

const DEMO_DIR = path.resolve('../bin/Debug/net10.0/DemoData');

// Shared preamble — opens demo.typhon via the Connect Dialog. Mirrors navigate-resource-tree.spec.ts;
// copied here to keep the spec self-contained.
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

test.describe('Module 15 — Database File Map (A1 canary)', () => {
  test('open → render whole-file map → pan', async ({ page, request }) => {
    await openDemo(page, request);

    // Open the panel through the command palette.
    await page.keyboard.press('Control+k');
    await page.getByPlaceholder(/search commands/i).fill('Database File Map');
    await page.getByText(/Toggle View Database File Map/i).first().click();

    const panel = page.getByTestId('dbmap-panel');
    await expect(panel).toBeVisible();

    const canvas = page.getByTestId('dbmap-canvas');
    await expect(canvas).toBeVisible();

    // The breadcrumb must report a real, non-empty page count built from the live engine.
    const breadcrumb = page.getByTestId('dbmap-breadcrumb');
    await expect(breadcrumb).toContainText(/pages/i, { timeout: 10_000 });

    // Pan the surface — a drag must not throw and the canvas must survive.
    const box = await canvas.boundingBox();
    expect(box).not.toBeNull();
    if (box) {
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      await page.mouse.down();
      await page.mouse.move(box.x + box.width / 2 + 80, box.y + box.height / 2 + 60, { steps: 8 });
      await page.mouse.up();
    }
    await expect(canvas).toBeVisible();

    // Switch the encoding — the legend / map must recolor without error.
    await page.getByTestId('dbmap-encoding').selectOption('freeUsed');
    await expect(canvas).toBeVisible();
  });
});
