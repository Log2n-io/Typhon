import { test, expect } from '@playwright/test';
import { closeAllSessions, gotoWelcome, openDevFixture } from './_session';

// Stage 2 Phase 3 — bidirectional reveal (GAP-04 / AC2.8 / AC2.14). The schema spine and the Resource tree
// can "Reveal in → File Map" (the spatial pivot); the map opens and renders. (Outbound File Map → Schema /
// Resource is wired via the canvas context menu, exercised by dbmap-drilldown.spec.)
test.describe('File Map — reveal handoffs (GAP-04)', () => {
  test.beforeEach(async ({ request }) => {
    await closeAllSessions(request);
  });

  test('Component Inspector → Reveal in File Map opens the map', async ({ page }) => {
    await gotoWelcome(page);
    await openDevFixture(page);

    await page.getByRole('button', { name: 'Types', exact: true }).click();
    const typeRows = page.locator('[data-testid="schema-explorer-type-row"]');
    await expect.poll(() => typeRows.count()).toBeGreaterThan(0);
    await typeRows.first().dblclick();

    const inspector = page.getByTestId('component-inspector');
    await expect(inspector).toBeVisible();
    await inspector.getByTestId('component-reveal-file-map').click();

    await expect(page.getByTestId('dbmap-canvas')).toBeVisible({ timeout: 10_000 });
  });

  test('Archetype Inspector → Reveal in File Map opens the map', async ({ page }) => {
    await gotoWelcome(page);
    await openDevFixture(page);

    const archRows = page.locator('[data-testid="schema-explorer-archetype"]');
    await expect.poll(() => archRows.count()).toBeGreaterThan(0);
    await archRows.first().dblclick();

    const inspector = page.getByTestId('archetype-inspector');
    await inspector.getByRole('tab', { name: 'Storage' }).click();
    await inspector.getByTestId('archetype-reveal-file-map').click();

    await expect(page.getByTestId('dbmap-canvas')).toBeVisible({ timeout: 10_000 });
  });

  test('Resource card → Reveal in File Map opens the map', async ({ page }) => {
    await gotoWelcome(page);
    await openDevFixture(page);

    await page.getByPlaceholder(/filter resources/i).fill('Fixture.Player');
    await page.getByText('ComponentTable_Typhon.Workbench.Fixture.Player', { exact: true }).click();
    await page.getByTestId('resource-reveal-file-map').click();

    await expect(page.getByTestId('dbmap-canvas')).toBeVisible({ timeout: 10_000 });
  });

  test('a reveal is a single Back stop — one Alt+Left returns to the origin (open+fly coalesced)', async ({ page }) => {
    await gotoWelcome(page);
    await openDevFixture(page);

    await page.getByRole('button', { name: 'Types', exact: true }).click();
    const typeRows = page.locator('[data-testid="schema-explorer-type-row"]');
    await expect.poll(() => typeRows.count()).toBeGreaterThan(0);
    await typeRows.first().dblclick();

    const inspector = page.getByTestId('component-inspector');
    await expect(inspector).toBeVisible();
    await inspector.getByTestId('component-reveal-file-map').click();
    await expect(page.getByTestId('dbmap-canvas')).toBeVisible({ timeout: 10_000 });
    // Let the async fly-to fire its dbmap-navigated (the would-be second entry) so we exercise the coalesce.
    await page.waitForTimeout(1500);

    // The reveal is purely spatial — it must NOT flip the analytical lens to fragmentation (decoupled).
    await expect(page.getByTestId('dbmap-lens')).not.toHaveValue('fragmentation');

    // ONE Back returns to the Component Inspector — not a half-step that stays in the File Map.
    await page.keyboard.press('Alt+ArrowLeft');
    await expect(inspector).toBeVisible();
    await expect(page.getByTestId('dbmap-canvas')).toBeHidden();
  });
});
