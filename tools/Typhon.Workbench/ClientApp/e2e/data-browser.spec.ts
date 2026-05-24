import { test, expect } from '@playwright/test';
import { closeAllSessions, gotoWelcome, openDevFixture, openPalette } from './_session';

// Stage 2 Phase 2 — the Data Browser reintroduced onto the unified selection bus (GAP-03/05). Drives the
// real J1 data drill against the Dev Fixture (deterministic, populated): open the panel → scope to an
// archetype → page its entities → click a row → the right-rail Inspector shows the decoded component cards.
// (The old Phase-1 spec drove the since-removed "Component Browser" view — replaced by the schema spine.)

test.describe('Data Browser (Stage 2 — reintroduced)', () => {
  test.beforeEach(async ({ request }) => {
    await closeAllSessions(request);
  });

  test('View → Data Browser opens the entity list with an archetype picker', async ({ page }) => {
    await gotoWelcome(page);
    await openDevFixture(page);

    await page.getByRole('menuitem', { name: /^view$/i }).click();
    await page.getByRole('menuitem', { name: /^data browser$/i }).click();

    // Panel-mount canary: the archetype picker is unique to the Entity List panel.
    await expect(page.getByTestId('archetype-picker')).toBeVisible({ timeout: 5_000 });
  });

  test('Palette command "Open Data Browser" opens the panel', async ({ page }) => {
    await gotoWelcome(page);
    await openDevFixture(page);

    await openPalette(page, 'data browser');
    await page.getByRole('option', { name: /open data browser/i }).click();

    await expect(page.getByTestId('archetype-picker')).toBeVisible({ timeout: 5_000 });
  });

  test('scoped drill: archetype → paged rows → row click → Inspector component cards (AC2.5)', async ({ page }) => {
    await gotoWelcome(page);
    await openDevFixture(page);

    await page.getByRole('menuitem', { name: /^view$/i }).click();
    await page.getByRole('menuitem', { name: /^data browser$/i }).click();
    const picker = page.getByTestId('archetype-picker');
    await expect(picker).toBeVisible({ timeout: 5_000 });

    // Scope to the Particle archetype (largest fixture shape) — selected by label so it survives id shifts.
    const particleValue = await picker.locator('option', { hasText: /Particle/ }).first().getAttribute('value');
    expect(particleValue, 'fixture should expose a Particle archetype').toBeTruthy();
    await picker.selectOption(particleValue as string);

    // Paged rows render with a "n–m of total" range (no dupes/skips is covered by server tests).
    const rows = page.getByTestId('entity-row');
    await expect(rows.first()).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('page-range')).toContainText(/of [\d,]+/);

    // Row click → bus entity leaf → right-rail Inspector renders the entity's decoded component cards.
    await rows.first().click();
    const card = page.getByTestId('component-card').first();
    await expect(card).toBeVisible({ timeout: 5_000 });
    await expect(card).toHaveAttribute('data-type-name', /Particle/);

    // GAP-05: the highlighted row is the bus selection (the clicked row carries the active styling).
    await expect(rows.first()).toHaveClass(/bg-accent/);
  });
});

// Uniform "Open in → Data Browser" handoffs (GAP-03 / AC2.6 / AC2.7) — a real verb from the Archetype
// Inspector (1:1) and the Component Inspector (M:N type-first auto-pick), each landing the Data Browser
// scoped to a populated archetype. These are the J1 Phase-4 entry points from the schema spine.
test.describe('Data Browser — Open-in handoffs', () => {
  test.beforeEach(async ({ request }) => {
    await closeAllSessions(request);
  });

  test('Archetype Inspector → Open in Data Browser scopes the entity list (AC2.6)', async ({ page }) => {
    await gotoWelcome(page);
    await openDevFixture(page);

    const archRows = page.locator('[data-testid="schema-explorer-archetype"]');
    await expect.poll(() => archRows.count()).toBeGreaterThan(0);
    await archRows.first().dblclick();

    const inspector = page.getByTestId('archetype-inspector');
    await expect(inspector).toBeVisible();
    await inspector.getByRole('tab', { name: 'Entities' }).click();

    const open = page.getByTestId('archetype-open-data-browser');
    await expect(open).toBeVisible({ timeout: 5_000 });
    await open.click();

    // The Data Browser opens scoped: picker present + entity rows for the chosen archetype.
    await expect(page.getByTestId('archetype-picker')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('entity-row').first()).toBeVisible({ timeout: 5_000 });
  });

  test('Component Inspector → type-first Open in Data Browser auto-picks a populated archetype (AC2.7)', async ({ page }) => {
    await gotoWelcome(page);
    await openDevFixture(page);

    // Drill to a *used* component: archetype → Archetype Inspector → Components tab → a component row.
    const archRows = page.locator('[data-testid="schema-explorer-archetype"]');
    await expect.poll(() => archRows.count()).toBeGreaterThan(0);
    await archRows.first().dblclick();
    const archInspector = page.getByTestId('archetype-inspector');
    await expect(archInspector).toBeVisible();
    await archInspector.locator('[data-testid="archetype-component-row"]').first().dblclick();

    // The Component Inspector's type-first verb appears (component has populated archetypes) and scopes the browser.
    const compInspector = page.getByTestId('component-inspector');
    await expect(compInspector).toBeVisible();
    const open = compInspector.getByTestId('component-open-data-browser');
    await expect(open).toBeVisible({ timeout: 5_000 });
    await open.click();

    await expect(page.getByTestId('archetype-picker')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('entity-row').first()).toBeVisible({ timeout: 5_000 });
  });
});

// Stage 2 Phase 2 polish — client filter (GAP-15 client / degraded), keyboard row-nav (PC-8/F), PC-1
// preferences (AC2.16), and the Resource → Data Browser handoff (AC2.6 remainder).
test.describe('Data Browser — filter / keyboard / prefs / resource', () => {
  test.beforeEach(async ({ request }) => {
    await closeAllSessions(request);
  });

  async function openParticles(page: import('@playwright/test').Page) {
    await gotoWelcome(page);
    await openDevFixture(page);
    await page.getByRole('menuitem', { name: /^view$/i }).click();
    await page.getByRole('menuitem', { name: /^data browser$/i }).click();
    const picker = page.getByTestId('archetype-picker');
    await expect(picker).toBeVisible({ timeout: 5_000 });
    const particle = await picker.locator('option', { hasText: /Particle/ }).first().getAttribute('value');
    await picker.selectOption(particle as string);
    await expect(page.getByTestId('entity-row').first()).toBeVisible({ timeout: 5_000 });
    return picker;
  }

  test('client filter narrows the loaded page by Entity Id and shows the degraded note (GAP-15)', async ({ page }) => {
    await openParticles(page);
    const firstId = await page.getByTestId('entity-row').first().getAttribute('data-entity-id');
    await page.getByTestId('entity-filter').fill(`Entity ID = ${firstId}`);
    // Exactly the one matching row remains; the note states the loaded-page degradation.
    await expect.poll(() => page.getByTestId('entity-row').count()).toBe(1);
    await expect(page.getByTestId('entity-row').first()).toHaveAttribute('data-entity-id', firstId as string);
    await expect(page.getByTestId('entity-filter-note')).toContainText(/loaded-page find/i);
  });

  test('keyboard: focus the list, ArrowDown + Enter selects a row → Inspector card (PC-8/F)', async ({ page }) => {
    await openParticles(page);
    await page.locator('div[tabindex="0"].overflow-auto').focus();
    await page.keyboard.press('ArrowDown'); // cursor → row 0
    await page.keyboard.press('Enter'); // commit → bus entity leaf
    await expect(page.getByTestId('entity-row').first()).toHaveClass(/bg-accent/);
    await expect(page.getByTestId('component-card').first()).toBeVisible({ timeout: 5_000 });
  });

  test('PC-1: page size is remembered per archetype (AC2.16)', async ({ page }) => {
    const picker = await openParticles(page);
    await page.getByTestId('page-size').selectOption('50');
    await expect(page.getByTestId('page-size')).toHaveValue('50');

    // Switch to another archetype (its own default) then back — the 50 is restored for the Particle archetype.
    const other = await picker.locator('option', { hasText: /CompD|Guild|Player/ }).first().getAttribute('value');
    await picker.selectOption(other as string);
    await expect(page.getByTestId('page-size')).not.toHaveValue('50');
    const particle = await picker.locator('option', { hasText: /Particle/ }).first().getAttribute('value');
    await picker.selectOption(particle as string);
    await expect(page.getByTestId('page-size')).toHaveValue('50');
  });

  test('Resource → Open in Data Browser scopes the entity list (AC2.6)', async ({ page }) => {
    await gotoWelcome(page);
    await openDevFixture(page);

    // Narrow the (virtualized) resource tree so the CompA ComponentTable node renders, then select it →
    // right-rail Inspector → its type-first Data Browser verb.
    await page.getByPlaceholder(/filter resources/i).fill('Fixture.CompA');
    await page.getByText('ComponentTable_Typhon.Workbench.Fixture.CompA', { exact: true }).click();
    const open = page.getByTestId('resource-open-data-browser');
    await expect(open).toBeVisible({ timeout: 5_000 });
    await open.click();

    await expect(page.getByTestId('archetype-picker')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('entity-row').first()).toBeVisible({ timeout: 5_000 });
  });
});
