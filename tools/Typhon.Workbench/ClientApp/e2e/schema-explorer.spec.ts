import { test, expect } from '@playwright/test';
import { closeAllSessions, gotoWelcome, openDevFixture } from './_session';

// Stage 2 · AC2.1 + J1 orient→drill (E2E-J1.1 steps 1-3 down-payment). The Schema Explorer is the Open
// default center: opening a database lands here, archetypes list, selecting one drives the bus leaf
// (→ URL + right-rail Inspector), and the Types toggle reaches component types. Uses the Dev Fixture
// (base-tests) because it carries real archetypes (the empty demo DB has none).
test.describe('AC2.1 — Schema Explorer (Open default center)', () => {
  test('open lands on Schema Explorer; archetype + type selection drive the bus', async ({ page, request }) => {
    await closeAllSessions(request);
    await gotoWelcome(page); // clears localStorage → fresh layout (buildDefaultLayout mounts the center)
    await openDevFixture(page);

    // It mounts as the CENTER workspace, not an edge group (regression guard for the dock-mount fix).
    const explorer = page.getByTestId('schema-explorer');
    await expect(explorer).toBeVisible();
    expect(await explorer.evaluate((el) => !el.closest('.dv-edge-group'))).toBe(true);

    // Archetypes mode (default): real archetypes listed.
    const archRows = page.locator('[data-testid="schema-explorer-archetype"]');
    await expect.poll(() => archRows.count()).toBeGreaterThan(0);

    // Select an archetype → bus leaf → URL + Inspector reacts (deep view is a later stage, but the
    // summary card replaces the empty "select anything" prompt — no dead end).
    await archRows.first().click();
    await expect.poll(() => new URL(page.url()).searchParams.get('leaf')).toMatch(/^archetype:/);
    await expect(page.getByText(/select anything/i)).toHaveCount(0);

    // Toggle to Types → component types listed → select one → bus leaf flips to the component.
    await page.getByRole('button', { name: 'Types', exact: true }).click();
    const typeRows = page.locator('[data-testid="schema-explorer-type-row"]');
    await expect.poll(() => typeRows.count()).toBeGreaterThan(0);
    await typeRows.first().click();
    await expect.poll(() => new URL(page.url()).searchParams.get('leaf')).toMatch(/^component:/);
  });

  test('double-clicking an archetype opens the Archetype Inspector deep view (J1 step 4)', async ({ page, request }) => {
    await closeAllSessions(request);
    await gotoWelcome(page);
    await openDevFixture(page);

    const archRows = page.locator('[data-testid="schema-explorer-archetype"]');
    await expect.poll(() => archRows.count()).toBeGreaterThan(0);
    await archRows.first().dblclick();

    // Opens as a CENTER tab (next to Schema Explorer), not an edge group.
    const inspector = page.getByTestId('archetype-inspector');
    await expect(inspector).toBeVisible();
    expect(await inspector.evaluate((el) => !el.closest('.dv-edge-group'))).toBe(true);
    // Drives the deep view: the archetype's components + the four tabs.
    await expect.poll(() => page.locator('[data-testid="archetype-component-row"]').count()).toBeGreaterThan(0);
    await expect(inspector.getByRole('tab', { name: 'Storage' })).toBeVisible();
  });

  test('double-clicking a component type opens the Component Inspector deep view (J1 step 6)', async ({ page, request }) => {
    await closeAllSessions(request);
    await gotoWelcome(page);
    await openDevFixture(page);

    await page.getByRole('button', { name: 'Types', exact: true }).click();
    const typeRows = page.locator('[data-testid="schema-explorer-type-row"]');
    await expect.poll(() => typeRows.count()).toBeGreaterThan(0);
    await typeRows.first().dblclick();

    const inspector = page.getByTestId('component-inspector');
    await expect(inspector).toBeVisible();
    expect(await inspector.evaluate((el) => !el.closest('.dv-edge-group'))).toBe(true);
    // Layout (default) renders the byte-grid canvas; Indexes + Used-in tabs present (Storage-mode /
    // Relationships land in later increments).
    await expect(inspector.getByTestId('schema-layout-canvas')).toBeVisible();
    await expect(inspector.getByRole('tab', { name: 'Indexes' })).toBeVisible();
    await expect(inspector.getByRole('tab', { name: 'Used in' })).toBeVisible();
  });
});
