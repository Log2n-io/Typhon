import { test, expect } from '@playwright/test';
import { closeAllSessions, gotoWelcome, openDevFixture } from './_session';

// Stage 2 Phase 3 — Storage Health dashboard (GAP-16 / AC2.9). Server-side `GET dbmap/health` rollup → a
// DB summary + sortable per-segment table; a row reveals its segment in the File Map (the spatial pivot).
test.describe('Storage Health (GAP-16)', () => {
  test.beforeEach(async ({ request }) => {
    await closeAllSessions(request);
  });

  test('View → Storage Health shows the summary + per-segment table', async ({ page }) => {
    await gotoWelcome(page);
    await openDevFixture(page);

    await page.getByRole('menuitem', { name: /^view$/i }).click();
    await page.getByRole('menuitem', { name: /^storage health$/i }).click();

    const panel = page.getByTestId('storage-health');
    await expect(panel).toBeVisible({ timeout: 5_000 });
    await expect(panel).toContainText(/pages/i); // DB summary line (e.g. "… · N pages · …")
    await expect(page.getByTestId('storage-health-segment-row').first()).toBeVisible({ timeout: 5_000 });
  });

  test('a segment row reveals in the File Map (the spatial pivot)', async ({ page }) => {
    await gotoWelcome(page);
    await openDevFixture(page);

    await page.getByRole('menuitem', { name: /^view$/i }).click();
    await page.getByRole('menuitem', { name: /^storage health$/i }).click();
    await expect(page.getByTestId('storage-health-segment-row').first()).toBeVisible({ timeout: 5_000 });

    await page.getByTestId('storage-health-reveal').first().click();
    await expect(page.getByTestId('dbmap-canvas')).toBeVisible({ timeout: 10_000 });
  });

  test('clicking a column header re-sorts the table', async ({ page }) => {
    await gotoWelcome(page);
    await openDevFixture(page);

    await page.getByRole('menuitem', { name: /^view$/i }).click();
    await page.getByRole('menuitem', { name: /^storage health$/i }).click();
    const rows = page.getByTestId('storage-health-segment-row');
    await expect(rows.first()).toBeVisible({ timeout: 5_000 });

    const idsBefore = await rows.evaluateAll((els) => els.map((e) => e.getAttribute('data-segment-id')));
    await page.getByRole('button', { name: /^Pages/ }).click(); // sort by pages (desc)
    const idsAfterDesc = await rows.evaluateAll((els) => els.map((e) => e.getAttribute('data-segment-id')));
    await page.getByRole('button', { name: /^Pages/ }).click(); // toggle to asc
    const idsAfterAsc = await rows.evaluateAll((els) => els.map((e) => e.getAttribute('data-segment-id')));
    // A toggle must flip the order (asc is the reverse-ish of desc), and at least one of the sorts changes it.
    expect(idsAfterAsc).not.toEqual(idsAfterDesc);
    expect(idsBefore.length).toBeGreaterThan(0);
  });
});
