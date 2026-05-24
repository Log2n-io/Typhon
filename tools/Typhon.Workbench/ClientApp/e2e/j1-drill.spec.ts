import { test, expect } from '@playwright/test';
import { closeAllSessions, gotoWelcome, openDevFixture } from './_session';

// AC2.12 — the J1 exit gate. The full schema → data → disk drill in one flow (E2E-J1.1 + the J1.4 storage
// hop), proving the unified bus carries selection across every hop and no view dead-ends.
test.describe('J1 — schema → data → disk drill (AC2.12)', () => {
  test('orient → archetype → component → layout → entity values → File Map', async ({ page, request }) => {
    await closeAllSessions(request);
    await gotoWelcome(page);
    await openDevFixture(page);

    // 1. Orient — the Schema Explorer is the Open default center.
    await expect(page.getByTestId('schema-explorer')).toBeVisible();

    // 2. Archetype → Archetype Inspector.
    const archRows = page.locator('[data-testid="schema-explorer-archetype"]');
    await expect.poll(() => archRows.count()).toBeGreaterThan(0);
    await archRows.first().dblclick();
    const archInspector = page.getByTestId('archetype-inspector');
    await expect(archInspector).toBeVisible();

    // 3. Component → Component Inspector.
    await archInspector.locator('[data-testid="archetype-component-row"]').first().dblclick();
    const compInspector = page.getByTestId('component-inspector');
    await expect(compInspector).toBeVisible();

    // 4. Layout (byte grid) — the field/byte view is the default tab.
    await expect(compInspector.getByTestId('schema-layout-canvas')).toBeVisible();

    // 5. Data drill — type-first Open in Data Browser → entity rows → select → decoded component cards.
    await compInspector.getByTestId('component-open-data-browser').click();
    const rows = page.getByTestId('entity-row');
    await expect(rows.first()).toBeVisible({ timeout: 10_000 });
    await rows.first().click();
    await expect(page.getByTestId('component-card').first()).toBeVisible({ timeout: 5_000 });

    // 6. Storage drill — re-activate the Component Inspector tab and Reveal in File Map to close the loop to disk.
    await page.locator('.dv-tab').filter({ hasText: 'Component' }).first().click();
    await compInspector.getByTestId('component-reveal-file-map').click();
    await expect(page.getByTestId('dbmap-canvas')).toBeVisible({ timeout: 10_000 });
  });
});
