import { test, expect } from '@playwright/test';
import { closeAllSessions, gotoWelcome, makeTraceFixture, openPalette, openTrace } from './_session';

// #911 O3 canary — the Spatial Maintenance panel is reachable from the palette in a profiler session, and renders its
// explained cold state rather than an empty box.
//
// A TRACE session, not an open `.typhon` one: the view is profiler-scoped, so the palette entry is deliberately absent
// outside a trace/attach session (IA §7 principle 4 — no broken affordances). And the cold state is what a canary can
// assert without a ticking engine: the counters are produced by a tick fence, so a replayed trace has none by
// construction. Asserting the populated panel needs a live attach and belongs with the attach specs.
test.describe('#911 — Spatial Maintenance panel', () => {
  test('opens from the palette in a trace session and explains why it is empty', async ({ page, request }) => {
    await closeAllSessions(request);
    await gotoWelcome(page);
    const tracePath = await makeTraceFixture(request);
    await openTrace(page, tracePath);

    await openPalette(page, 'spatial maintenance');
    await expect(page.locator('[cmdk-item]').first()).toHaveText(/Spatial Maintenance/i);
    await page.keyboard.press('Enter');

    const cold = page.getByTestId('spatial-maintenance-cold');
    await expect(cold).toBeVisible();
    // Not a blank panel and not a wall of zeros: it names the session kind it needs, and why.
    await expect(cold).toContainText(/Attach/);
  });
});
