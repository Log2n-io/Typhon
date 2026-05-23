import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

const DEMO_DIR = path.resolve('../bin/Debug/net10.0/DemoData');

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

// Conformance suite E (PC-6 — no broken affordances), Stage-0 slice. Scans the live DOM for any disabled
// control whose label reads like an unbuilt handoff verb — exactly the F3/F5 stubs Stage 0 removed.
// Returns the offending labels so a failure names them.
async function disabledHandoffLabels(page: import('@playwright/test').Page): Promise<string[]> {
  return page.evaluate(() => {
    const verb = /\b(open in|reveal in|go to)\b/i;
    const candidates = Array.from(
      document.querySelectorAll('button, [role="button"], [role="menuitem"], [role="option"]'),
    );
    return candidates
      .filter((el) => {
        const disabled =
          (el as HTMLButtonElement).disabled === true ||
          el.getAttribute('aria-disabled') === 'true' ||
          el.hasAttribute('disabled') ||
          el.getAttribute('data-disabled') != null;
        return disabled && verb.test(el.textContent ?? '');
      })
      .map((el) => (el.textContent ?? '').trim());
  });
}

test.describe('Conformance suite E (slice) — no broken affordances', () => {
  test('the shell exposes no disabled Open in / Reveal in / Go to control (AC0.3)', async ({ page, request }) => {
    await openDemo(page, request);

    // 1. Resting shell (inspector / navigator / status bar).
    expect(await disabledHandoffLabels(page)).toEqual([]);

    // 2. The resource-tree (navigator) context menu — historically the worst offender.
    const firstRow = page.locator('[role="treeitem"] > div').first();
    await firstRow.click({ button: 'right' });
    await expect(page.getByRole('menuitem', { name: /copy path/i })).toBeVisible({ timeout: 5_000 });
    expect(await disabledHandoffLabels(page)).toEqual([]);
    await page.keyboard.press('Escape');

    // 3. The View menu.
    await page.getByRole('menuitem', { name: /^view$/i }).click();
    await expect(page.getByRole('menuitem', { name: /^options$/i })).toBeVisible({ timeout: 5_000 });
    expect(await disabledHandoffLabels(page)).toEqual([]);
    await page.keyboard.press('Escape');
  });
});
