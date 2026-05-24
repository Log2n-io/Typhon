import { test, expect } from '@playwright/test';
import { closeAllSessions, gotoWelcome, openDevFixture } from './_session';

// AC1.2 / conformance B.2 — view-granular nav history. A double-click navigates INTO a view; one Back
// returns to the view you came from. The drill Schema → Archetype → Component must be exactly ONE Back per
// view (no redundant stops, and the Schema origin is never lost). Unit suite proves the store model
// (B.1c/B.1d/B.7/B.8); this proves the real dockview wiring.
test.describe('Nav focus restore (AC1.2 — one Back per view)', () => {
  test('Schema → Archetype → Component drills; Back walks back one view at a time', async ({ page, request }) => {
    await closeAllSessions(request);
    await gotoWelcome(page);
    await openDevFixture(page);

    await expect(page.getByTestId('schema-explorer')).toBeVisible();

    // Drill 1: double-click an archetype → Archetype Inspector.
    await page.locator('[data-archetype-id="802"]').dblclick();
    await expect(page.getByTestId('archetype-inspector')).toBeVisible();

    // Drill 2: double-click a component inside it → Component Inspector.
    await page.locator('[data-testid="archetype-component-row"][data-type-name="Typhon.Workbench.Fixture.CompA"]').dblclick();
    await expect(page.getByTestId('component-inspector')).toBeVisible();

    // Back #1 → Archetype Inspector (the view before drill 2).
    await page.keyboard.press('Alt+ArrowLeft');
    await expect(page.getByTestId('archetype-inspector')).toBeVisible();

    // Back #2 → Schema Explorer (the view before drill 1) — the origin is NOT lost.
    await page.keyboard.press('Alt+ArrowLeft');
    await expect(page.getByTestId('schema-explorer')).toBeVisible();
    await expect
      .poll(() =>
        page.evaluate(() => {
          const sx = document.querySelector('[data-testid="schema-explorer"]');
          const ae = document.activeElement;
          if (!sx || !ae || ae === document.body) return false;
          return ae.contains(sx) || sx.contains(ae) || ae === sx;
        }),
      )
      .toBe(true);

    // Forward re-enters the Archetype Inspector.
    await page.keyboard.press('Alt+ArrowRight');
    await expect(page.getByTestId('archetype-inspector')).toBeVisible();
  });
});
