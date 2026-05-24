import { test, expect } from '@playwright/test';
import { closeAllSessions, gotoWelcome, openDemoFile, openPalette } from './_session';

// AC1.3 — command-palette prefix routing (command-palette.md / IA §4). The unit suite (paletteRouting.test)
// proves `parsePaletteMode` in isolation, but that left the cmdk *integration* unguarded — and that gap hid
// a real bug: cmdk filters on the raw input value, which still carries the mode prefix, so ">open" scored
// every command to 0 and the explicit action mode returned nothing. These e2e assertions exercise the live
// palette end-to-end so that regression (and the other prefixes) can't silently return.

const itemCount = (page: import('@playwright/test').Page) => page.locator('[cmdk-item]').count();
const itemTexts = (page: import('@playwright/test').Page) =>
  page.locator('[cmdk-item]').allTextContents().then((t) => t.map((s) => s.trim()));

test.describe('AC1.3 — command palette prefix routing', () => {
  test.beforeEach(async ({ page, request }) => {
    await closeAllSessions(request);
    await gotoWelcome(page);
    await openDemoFile(page, request);
  });

  // "open" matches the five Open-* commands, incl. the Stage-2 "Open Data Browser" + "Open Storage Health".
  test('bare query filters commands (command mode)', async ({ page }) => {
    await openPalette(page, 'open');
    await expect.poll(() => itemCount(page)).toBe(5);
    expect(await itemTexts(page)).toEqual(
      expect.arrayContaining(['Open File…', 'Open Recent', 'Open Trace…', 'Open Data Browser', 'Open Storage Health']),
    );
  });

  test('">" runs the SAME command match as the bare query (regression: ">" must not zero out)', async ({ page }) => {
    await openPalette(page, '>open');
    // The bug this guards: ">open" used to show "No results". It must match exactly like "open".
    await expect.poll(() => itemCount(page)).toBe(5);
    expect(await itemTexts(page)).toEqual(expect.arrayContaining(['Open File…', 'Open Trace…', 'Open Storage Health']));
  });

  test('"@" resolves in-session objects (resource hits)', async ({ page }) => {
    await openPalette(page, '@Storage');
    await expect.poll(() => itemCount(page)).toBeGreaterThan(0);
    expect((await itemTexts(page)).some((t) => /Storage/i.test(t))).toBeTruthy();
  });

  test('":" jump offers a single jump target', async ({ page }) => {
    await openPalette(page, ':page 1024');
    await expect(page.locator('[cmdk-item]')).toHaveText(/Jump to page 1024/i);
  });

  test('"?" shows the prefix help (all routes documented)', async ({ page }) => {
    await openPalette(page, '?');
    const codes = await page.locator('[cmdk-list] code').allTextContents();
    expect(codes.map((c) => c.trim())).toEqual(expect.arrayContaining(['>', '@', '#', ':', '?']));
  });
});
