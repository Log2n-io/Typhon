import { test, expect } from '@playwright/test';
import { closeAllSessions, gotoWelcome, openDemoFile } from './_session';

// AC1.2 — navigation history + deep links (IA §5, command-palette.md). The selection bus writes the active
// leaf into the URL (`?resource=…`) and pushes a nav-history entry; the toolbar Go-back / Go-forward
// buttons replay that history. Unit suites cover `selectionUrlSync` / `useNavHistoryStore` / bootstrap in
// isolation; this e2e guards the wired-up integration in a real browser.
//
// Scope note: reloading the page drops the engine session (it isn't persisted), so a pasted deep link only
// re-targets selection *within* an open session — the URL-restore-on-open path is unit-covered by
// useSelectionBootstrap. Here we assert the live write side + in-app history replay, which is the part with
// no automated coverage.
test.describe('AC1.2 — nav history & deep links', () => {
  test('selection writes ?resource= and Go-back / Go-forward replay it', async ({ page, request }) => {
    await closeAllSessions(request);
    await gotoWelcome(page);
    await openDemoFile(page, request);

    const resourceParam = () => new URL(page.url()).searchParams.get('resource');
    const back = page.getByRole('button', { name: /go back/i });
    const fwd = page.getByRole('button', { name: /go forward/i });

    // Two distinct leaf resources.
    await page.locator('[role="treeitem"]').filter({ hasText: 'PageCache' }).first().click();
    await expect.poll(resourceParam, { message: 'selecting PageCache writes the URL' }).toContain('PageCache');
    const a = resourceParam();

    await page.locator('[role="treeitem"]').filter({ hasText: 'EpochManager' }).first().click();
    await expect.poll(resourceParam).toContain('EpochManager');
    const b = resourceParam();
    expect(b).not.toBe(a);

    // Back restores A.
    await expect(back).toBeEnabled();
    await back.click();
    await expect.poll(resourceParam, { message: 'Go-back restores the previous leaf' }).toBe(a);

    // Forward restores B.
    await expect(fwd).toBeEnabled();
    await fwd.click();
    await expect.poll(resourceParam, { message: 'Go-forward re-advances to the later leaf' }).toBe(b);
  });
});
