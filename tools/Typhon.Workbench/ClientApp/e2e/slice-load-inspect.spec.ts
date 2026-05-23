import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

const DEMO_DIR = path.resolve('../bin/Debug/net10.0/DemoData');

/** Opens a clean demo (open-kind) session — see schema-inspector.spec.ts. Leaves the shell mounted. */
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

// Stage 1 load-a-file slice (Open kind): load → navigator (zone C) populates → select a navigator item →
// the unified bus re-targets the right-rail Inspector (zone E). The Trace/Attach kinds complete in Phase 3.
test.describe('Stage 1 slice — load → navigate → inspect (Open)', () => {
  test('selecting a resource in the navigator drives the Inspector via the bus', async ({ page, request }) => {
    await openDemo(page, request);

    // Inspector starts at its empty prompt (nothing on the bus yet).
    await expect(page.getByText(/select anything/i)).toBeVisible({ timeout: 5_000 });
    // The resource card's always-present "Children" row is our canary that it is NOT yet shown.
    await expect(page.getByText(/^Children$/)).toHaveCount(0);

    // Pick a navigator (Resource Tree) row.
    const firstRow = page.locator('[role="treeitem"] > div').first();
    await expect(firstRow).toBeVisible({ timeout: 10_000 });
    await firstRow.click();

    // The bus leaf flips to that resource → the Inspector renders its Resource card.
    await expect(page.getByText(/select anything/i)).toHaveCount(0);
    await expect(page.getByText(/^Children$/)).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText(/^Path$/)).toBeVisible();
  });
});
