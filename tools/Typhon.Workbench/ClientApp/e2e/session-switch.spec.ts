import { test, expect } from '@playwright/test';
import { closeAllSessions, gotoWelcome, openDevFixture, openDemoFile } from './_session';

// AC1.10 — switch session without close (GAP-12a). Opening a new session must wipe all session-scoped
// selection state (bus leaf, breadcrumb, nav history, Inspector) so nothing bleeds from the previous one.
// resetSessionScopedState runs on every sessionId change; this guards the wired-up behaviour end-to-end —
// including that the URL leaf is cleared *before* the bootstrap could re-restore it (the two interact:
// both files share resource paths, so a stale ?resource= would otherwise re-select on the new session).
test.describe('AC1.10 — switch session without close', () => {
  test('opening a second session resets selection (no cross-session bleed)', async ({ page, request }) => {
    await closeAllSessions(request);
    await gotoWelcome(page);

    // Session A: the Dev Fixture. Select a resource so the bus leaf, URL and Inspector are populated.
    await openDevFixture(page);
    await page.locator('[role="treeitem"]').filter({ hasText: 'ManagedPagedMMF' }).first().click();
    await expect.poll(() => new URL(page.url()).searchParams.get('resource')).toContain('ManagedPagedMMF');
    await expect(page.getByText(/select anything/i)).toHaveCount(0); // Inspector populated

    // Session B: switch to a different file WITHOUT closing A (File ▸ Open .typhon File…).
    await openDemoFile(page, request, 'demo.typhon');

    // Reset: the previous session's leaf must be gone — empty URL and empty Inspector on the new session.
    await expect.poll(
      () => new URL(page.url()).searchParams.get('resource'),
      { message: 'switching sessions must clear the previous leaf from the URL' },
    ).toBeNull();
    await expect(page.getByText(/select anything/i)).toBeVisible({ timeout: 5_000 });
    expect(await page.locator('[role="treeitem"][aria-selected="true"]').count()).toBe(0);
  });
});
