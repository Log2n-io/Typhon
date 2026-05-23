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
      await request.delete(`http://localhost:5200/api/sessions/${s.sessionId}`, { headers: { 'X-Session-Token': s.sessionId } });
    }
  }
  const seed = await request.post('http://localhost:5200/api/sessions/file', { data: { filePath: 'demo.typhon' } });
  const j = await seed.json();
  await request.delete(`http://localhost:5200/api/sessions/${j.sessionId}`, { headers: { 'X-Session-Token': j.sessionId } });
  await page.addInitScript(() => { try { localStorage.clear(); } catch { /* ignore */ } });
  await page.goto('/');
  await page.getByRole('button', { name: /^open \.typhon file$/i }).click();
  await page.getByPlaceholder(/path/i).first().fill(DEMO_DIR);
  const row = page.getByText(/^demo\.typhon$/).first();
  await expect(row).toBeVisible({ timeout: 10_000 });
  await row.click();
  await page.getByRole('button', { name: /^open$/i }).click();
  await expect(page.locator('body')).toContainText(/Storage|DataEngine/i, { timeout: 10_000 });
}

// F6 must cycle the active panel across edge groups (dockview's own moveToNext can't — they aren't in the
// grid). Regression guard for the keyboard model (AC1.4 / suite F).
//
// These assertions are deliberately stronger than "the class moved": the first cut of this fix passed a
// green spec that only checked `.dv-active-group` changed, while live F6 left DOM focus on <body> (so it
// looked dead). We assert the two things that were actually broken: (1) DOM focus lands inside the active
// pane, and (2) the DS-4 active-panel cue — a discreet tint on the active pane's own header toolbar
// (`.wb-pane-header`) — is present on exactly the active pane and moves with it.
test.describe('Stage 1 — keyboard panel focus (F6)', () => {
  // Reads the active group's edge orientation, whether DOM focus is inside the active pane, and how the
  // pane-header tint is distributed (the active pane's header is tinted; no other pane's is). One
  // round-trip so they stay consistent. 'rgba(0, 0, 0, 0)' is the untinted (transparent) header bg.
  const probe = (page: import('@playwright/test').Page) =>
    page.evaluate(() => {
      const TRANSPARENT = 'rgba(0, 0, 0, 0)';
      const groups = [...document.querySelectorAll('.dv-groupview')];
      const ag = groups.find((g) => g.classList.contains('dv-active-group'));
      const headerBg = (g: Element): string | null => {
        const h = g.querySelector('.wb-pane-header');
        return h ? getComputedStyle(h).backgroundColor : null;
      };
      const activeBg = ag ? headerBg(ag) : null;
      const otherBgs = groups.filter((g) => g !== ag).map(headerBg);
      return {
        edge: ag?.className.match(/header-(left|right|bottom|top)/)?.[0] ?? null,
        focusInActivePane: !!ag && ag.contains(document.activeElement), // the bug: focus used to sit on <body>
        activeHeaderTinted: activeBg != null && activeBg !== TRANSPARENT,
        tintExclusive: activeBg != null && otherBgs.every((bg) => bg !== activeBg),
      };
    });

  test('F6 moves focus into the active pane and the header tint follows', async ({ page, request }) => {
    await openDemo(page, request);
    // Populate the Inspector so the right pane also has a header toolbar to tint when F6 reaches it.
    await page.locator('[role="treeitem"]').first().click();

    const first = await probe(page);
    expect(first.edge).not.toBeNull();
    expect(first.focusInActivePane).toBe(true);
    expect(first.activeHeaderTinted).toBe(true); // active pane's own header is tinted
    expect(first.tintExclusive).toBe(true); // and no other pane's header shares that tint

    await page.keyboard.press('F6');
    await page.waitForTimeout(80);
    const second = await probe(page);
    expect(second.edge).not.toBe(first.edge); // the active group moved
    expect(second.focusInActivePane).toBe(true); // focus moved with it (used to stay orphaned on <body>)
    expect(second.activeHeaderTinted).toBe(true);
    expect(second.tintExclusive).toBe(true);

    await page.keyboard.press('F6');
    await page.waitForTimeout(80);
    const third = await probe(page);
    expect(third.edge).not.toBe(second.edge); // still cycling
    expect(third.focusInActivePane).toBe(true);
    expect(third.activeHeaderTinted).toBe(true);
  });
});
