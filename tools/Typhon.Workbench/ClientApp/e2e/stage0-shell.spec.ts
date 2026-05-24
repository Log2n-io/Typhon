import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

const DEMO_DIR = path.resolve('../bin/Debug/net10.0/DemoData');

/**
 * Opens a clean demo (open-kind) session — same ritual as schema-inspector.spec.ts — leaving the dockview
 * shell mounted and the resource tree populated. Stage 0 reduces the app to this shell frame, so the demo
 * session is exactly what we assert against.
 */
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

// The deep/workspace (zone-D) views STILL deactivated, by their View-menu label. (Data Browser was
// reintroduced in Stage 2 Phase 2, so it left this set and is asserted present below.)
const ZONE_D_MENU_ITEMS = [
  /component browser/i,
  /archetype browser/i,
  /component layout/i,
  /component archetypes/i,
  /component indexes/i,
  /component relationships/i,
  /^profiler$/i,
  /top spans/i,
  /system dag/i,
  /critical path/i,
  /call tree/i,
  /source preview/i,
  /data flow/i,
  /access matrix/i,
];

test.describe('Stage 0 — shell frame only', () => {
  test('opening a file shows no StartHere placeholder (AC0.4)', async ({ page, request }) => {
    await openDemo(page, request);
    await expect(page.getByText(/welcome to typhon workbench/i)).toHaveCount(0);
    // The shell frame is present: the resource tree (left navigator) mounted with engine content.
    await expect(page.locator('body')).toContainText(/Storage|DataEngine/i);
  });

  test('the View menu exposes no zone-D view (AC0.2)', async ({ page, request }) => {
    await openDemo(page, request);
    await page.getByRole('menuitem', { name: /^view$/i }).click();

    // Wait for the menu to open via a shell item that must remain.
    await expect(page.getByRole('menuitem', { name: /^options$/i })).toBeVisible({ timeout: 5_000 });

    for (const label of ZONE_D_MENU_ITEMS) {
      await expect(page.getByRole('menuitem', { name: label })).toHaveCount(0);
    }

    // The reintroduced deep views (Stage 2) ARE exposed.
    await expect(page.getByRole('menuitem', { name: /^data browser$/i })).toBeVisible();
    await expect(page.getByRole('menuitem', { name: /database file map/i })).toBeVisible();

    // Shell View items survive.
    await expect(page.getByRole('menuitem', { name: /^logs$/i })).toBeVisible();
    await expect(page.getByRole('menuitem', { name: /^detail$/i })).toBeVisible();
    await expect(page.getByRole('menuitem', { name: /reset layout to default/i })).toBeVisible();
    await page.keyboard.press('Escape');
  });

  test('the stub-only Edit and Help menus are gone (AC0.3)', async ({ page, request }) => {
    await openDemo(page, request);
    await expect(page.getByRole('menuitem', { name: /^edit$/i })).toHaveCount(0);
    await expect(page.getByRole('menuitem', { name: /^help$/i })).toHaveCount(0);
    // The shell menus remain.
    await expect(page.getByRole('menuitem', { name: /^file$/i })).toBeVisible();
    await expect(page.getByRole('menuitem', { name: /^view$/i })).toBeVisible();
  });

  test('the command palette surfaces no zone-D command (AC0.2)', async ({ page, request }) => {
    await openDemo(page, request);
    await page.getByRole('button', { name: /open command palette/i }).click();
    const paletteInput = page.getByPlaceholder(/search commands/i);
    await expect(paletteInput).toBeVisible();

    await paletteInput.fill('profiler');
    await expect(page.getByRole('option', { name: /toggle view profiler/i })).toHaveCount(0);

    await paletteInput.fill('component browser');
    await expect(page.getByRole('option', { name: /component browser/i })).toHaveCount(0);

    // A shell command is still reachable.
    await paletteInput.fill('reset layout');
    await expect(page.getByRole('option', { name: /reset layout to default/i })).toBeVisible();
    await page.keyboard.press('Escape');
  });

  test('the Open workspace hosts the Schema Explorer; no still-gated deep view leaks (AC0.2 / Stage 2)', async ({ page, request }) => {
    await openDemo(page, request);
    // Stage 2 reintroduced the Schema Explorer as the Open default center (no more empty/dead-end workspace).
    await expect(page.getByTestId('schema-explorer')).toBeVisible();
    // Views still gated off (Stages 3-4 / later) must not leak into the workspace.
    await expect(page.getByTestId('dbmap-canvas')).toHaveCount(0);
    await expect(page.getByTestId('entity-row')).toHaveCount(0);
  });
});
