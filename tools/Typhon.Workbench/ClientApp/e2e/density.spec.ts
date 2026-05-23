import { test, expect } from '@playwright/test';
import { closeAllSessions, gotoWelcome, openDemoFile, openPalette } from './_session';

// AC1.8 — runtime density switch (DS-1). Toggling density must flip `<html data-density>` AND actually
// re-measure virtualized rows (the value is read in JS by useDensityRowHeight, not just CSS), so the tree
// rows change height live. Unit suite H covers the store; this guards the wired DOM + re-measure end-to-end.
test.describe('AC1.8 — runtime density switch', () => {
  test('Toggle Density flips data-density and re-measures tree rows', async ({ page, request }) => {
    await closeAllSessions(request);
    await gotoWelcome(page);
    await openDemoFile(page, request);

    const density = () => page.evaluate(() => document.documentElement.dataset.density ?? 'compact');
    const rowHeight = () =>
      page.evaluate(() => {
        const row = document.querySelector('[role="treeitem"]');
        return row ? Math.round(row.getBoundingClientRect().height) : 0;
      });

    // Baseline: compact (22px rows).
    expect(await density()).toBe('compact');
    expect(await rowHeight()).toBe(22);

    // Toggle → comfortable (28px rows).
    await openPalette(page, 'density');
    await expect(page.locator('[cmdk-item]')).toHaveText(/Toggle Density/i);
    await page.keyboard.press('Enter');
    await expect.poll(density, { message: 'toggle should switch to comfortable' }).toBe('comfortable');
    await expect.poll(rowHeight, { message: 'tree rows re-measure to the comfortable height' }).toBe(28);

    // Toggle back → compact (22px rows). Proves it is a real switch, not a one-way flip.
    await openPalette(page, 'density');
    await page.keyboard.press('Enter');
    await expect.poll(density).toBe('compact');
    await expect.poll(rowHeight).toBe(22);
  });
});
