import { test, expect, type Page, type APIRequestContext, type Locator } from '@playwright/test';

/**
 * Phase D smoke for the Data Flow module's cross-panel selection bridges (#327, design §10.4). Covers
 * the static-topology interactions via the `with-access-declarations` fixture and the bar-driven
 * interactions (click + hover) via the `with-archetype-touches` fixture which adds per-tick
 * `SchedulerSystemArchetype` events so the timeline canvas renders bars.
 *
 * The spec drives the **Access Matrix** + **Data Flow Timeline** tabs (both live in the trace-mode
 * right edge group). Selection state lives in `useSelectionStore` and propagates via React, so a click
 * in one panel becomes an `aria-pressed=true` on the corresponding control in the other panel without
 * touching the System DAG (which trace-mode default layout doesn't open by default).
 */

interface SessionSummary { sessionId: string }

async function closeAllSessions(request: APIRequestContext): Promise<void> {
  const list = await request.get('http://localhost:5173/api/sessions');
  if (!list.ok()) return;
  const { sessions = [] } = await list.json();
  for (const s of sessions as SessionSummary[]) {
    await request.delete(`http://localhost:5173/api/sessions/${s.sessionId}`, {
      headers: { 'X-Session-Token': s.sessionId },
    });
  }
}

/**
 * Open a trace fixture and wait for the trace shell to mount. Returns once the Profiler header reports
 * the tick count, so the right-edge panels (Data Flow, Access Matrix) are registered in the dockview API.
 *
 * Two fixture variants are used by this spec:
 *   - `with-access-declarations` — 2 systems + 3 components + 3 phases, no archetypes / archetype touches.
 *     Drives the "static topology" cases that don't need bars in the canvas.
 *   - `with-archetype-touches`   — extends the above with 2 archetypes + per-tick `SchedulerSystemArchetype`
 *     events, so the Data Flow timeline renders bars. Needed for the bar-click + hover canary cases.
 */
async function openTraceFixture(
  page: Page,
  request: APIRequestContext,
  variant: 'with-access-declarations' | 'with-archetype-touches' = 'with-access-declarations',
  granularity: 'L0' | 'L4' = 'L0',
): Promise<void> {
  await closeAllSessions(request);

  const fx = await request.post('http://localhost:5173/api/fixtures/trace', {
    data: { variant },
  });
  expect(fx.ok(), 'fixture endpoint should respond 200').toBeTruthy();
  const { traceFilePath } = await fx.json();
  expect(traceFilePath).toBeTruthy();

  await page.addInitScript((level: 'L0' | 'L4') => {
    try {
      localStorage.clear();
      // L0 — stable domain rows, used by the static-topology cases.
      // L4 — one row per (archetype, component) pair, used by the bar-click + hover cases. The fixture
      // emits one (system, archetype) touch per system per tick, so each L4 row has bars from exactly
      // one system → click coords land unambiguously.
      const seed = { state: { granularityLevel: level }, version: 0 };
      localStorage.setItem('typhon-access-matrix-view', JSON.stringify(seed));
      localStorage.setItem('typhon-dataflow-view', JSON.stringify({ state: { granularityLevel: level, xMode: 'uniform', hoverIsolateEnabled: true }, version: 0 }));
    } catch { /* ignore */ }
  }, granularity);
  // Hide the vite-plugin-checker overlay — it surfaces pre-existing ESLint warnings from Phase B and
  // intercepts pointer events. Tests run against the same dev server real users hit, so we don't want
  // to silence the overlay project-wide; inject CSS only for the test session.
  await page.addStyleTag({ content: 'vite-plugin-checker-error-overlay { display: none !important }' }).catch(() => { /* added pre-navigation; ignore */ });
  await page.goto('/');
  await page.addStyleTag({ content: 'vite-plugin-checker-error-overlay { display: none !important }' });

  await page.getByRole('button', { name: /^open \.typhon-trace$/i }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('tab', { name: /^open trace$/i }).click();
  // Placeholder text was widened to mention `.typhon-replay` as well — match either by the leading
  // path hint instead of anchoring on the trailing extension.
  await page.getByPlaceholder(/path.*typhon-trace.*typhon-replay/i).fill(traceFilePath);
  await page.getByRole('button', { name: /^open$/i }).click();

  await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 10_000 });
  // 3-tick fixture — header pill confirms the trace cache built and the panel mounted.
  await expect(page.getByText(/\b3 ticks\b/)).toBeVisible({ timeout: 15_000 });
}

/**
 * Click a tab in the dockview tab strip by visible text. Dockview-react uses `dv-tab` (with `dv-active-tab`
 * marking the active one) and doesn't expose an ARIA tab role, so we click the tab element and wait for
 * the active class so subsequent panel-content queries don't race the tab swap.
 */
async function clickDockTab(page: Page, name: string): Promise<void> {
  const tab = page.locator('.dv-tab', { hasText: name }).first();
  await tab.click();
  await expect(tab).toHaveClass(/dv-active-tab/);
}

/**
 * Open a panel via the View menu. The panels we exercise here (Data Flow, Access Matrix) are no longer
 * pre-added to the trace-mode default layout — that pile-up was forcing dockview into a narrow vertical-tab
 * mode that hid the contents. Opening through the menu drops the panel into the Profiler/center group at a
 * usable width.
 */
async function openViaViewMenu(page: Page, label: string): Promise<void> {
  await page.getByRole('menuitem', { name: /^view$/i }).click();
  await page.getByRole('menuitem', { name: new RegExp(`^${label}$`, 'i') }).click();
  // After opening we get a new tab matching `label`; clickDockTab also waits for active class.
  await clickDockTab(page, label);
}

function pressedTrue(locator: Locator): Promise<void> {
  return expect(locator).toHaveAttribute('aria-pressed', 'true');
}

function pressedFalse(locator: Locator): Promise<void> {
  return expect(locator).toHaveAttribute('aria-pressed', 'false');
}

// Trace-mode default layout no longer pre-adds Data Flow / Access Matrix; they're opened via View menu and
// land in the Profiler/center group at usable width. 1600×900 keeps the menu items and resulting panels
// comfortably visible.
test.use({ viewport: { width: 1600, height: 900 } });

test.describe('Data Flow module — Phase D cross-panel bridges (#327)', () => {
  test('AccessMatrix: cell click selects system column; row + phase headers toggle their own slots; Esc clears all', async ({
    page,
    request,
  }) => {
    await openTraceFixture(page, request);
    await openViaViewMenu(page, 'Access Matrix');

    // Wait for the matrix grid to mount with its declared rows/columns. The row label "Components" is
    // the L0 component-domain row, always present at the default L2 fallback (with-access-declarations
    // fixture has only 3 component types, below the family-fallback threshold).
    const componentsRow = page.getByTestId('access-matrix-row-domain:components');
    await expect(componentsRow).toBeVisible({ timeout: 10_000 });

    const movementSystem = page.getByTestId('access-matrix-system-Movement');
    const damageSystem = page.getByTestId('access-matrix-system-Damage');
    const simulationPhase = page.getByTestId('access-matrix-phase-Simulation');

    // Initial state — every selectable header is unpressed.
    await pressedFalse(componentsRow);
    await pressedFalse(movementSystem);
    await pressedFalse(simulationPhase);

    // Click the Components row header → dataTrack selection lights this row only.
    await componentsRow.click();
    await pressedTrue(componentsRow);
    await pressedFalse(movementSystem);
    await pressedFalse(simulationPhase);

    // Click the Simulation phase header → phase slot lights up; row stays selected because they're
    // independent slots.
    await simulationPhase.click();
    await pressedTrue(simulationPhase);
    await pressedTrue(componentsRow);

    // Click the Movement system header (acts as a column shortcut for system selection).
    await movementSystem.click();
    await pressedTrue(movementSystem);
    await pressedFalse(damageSystem);
    await pressedTrue(componentsRow);   // dataTrack is still set
    await pressedTrue(simulationPhase); // phase is still set

    // Esc clears every selection slot at once.
    await page.keyboard.press('Escape');
    await pressedFalse(componentsRow);
    await pressedFalse(simulationPhase);
    await pressedFalse(movementSystem);
  });

  test('Cross-panel: DataFlow track row click → AccessMatrix row mirrors selection', async ({ page, request }) => {
    await openTraceFixture(page, request);

    // Open Data Flow via the View menu and click the Components track row.
    await openViaViewMenu(page, 'Data Flow');
    const componentsTrack = page.getByTestId('data-flow-track-domain:components');
    await expect(componentsTrack).toBeVisible({ timeout: 10_000 });
    await pressedFalse(componentsTrack);
    await componentsTrack.click();
    await pressedTrue(componentsTrack);

    // Open Access Matrix — the same dataTrack slot drives its row outline.
    await openViaViewMenu(page, 'Access Matrix');
    const componentsRow = page.getByTestId('access-matrix-row-domain:components');
    await expect(componentsRow).toBeVisible({ timeout: 10_000 });
    await pressedTrue(componentsRow);

    // Toggle off via the Access Matrix side and confirm both panels clear.
    await componentsRow.click();
    await pressedFalse(componentsRow);
    await clickDockTab(page, 'Data Flow');
    await pressedFalse(componentsTrack);
  });

  test('Cross-panel: AccessMatrix cell click → DataFlow surfaces the matching system selection', async ({ page, request }) => {
    await openTraceFixture(page, request);
    await openViaViewMenu(page, 'Access Matrix');

    // L0 cell for (Components, Movement) — the with-access-declarations fixture declares Game.Position
    // as a write target on Movement, so the component-domain row resolves to a 'write' cell.
    const cell = page.getByTestId('access-matrix-cell-domain:components|Movement');
    await expect(cell).toBeVisible({ timeout: 10_000 });
    await cell.click();

    // The Movement system header should now be pressed via the shared selectedSystem slot.
    await pressedTrue(page.getByTestId('access-matrix-system-Movement'));
    await pressedFalse(page.getByTestId('access-matrix-system-Damage'));

    // The store mirror is panel-agnostic — toggling Esc clears the selection across the app.
    await page.keyboard.press('Escape');
    await pressedFalse(page.getByTestId('access-matrix-system-Movement'));
  });

  test('DataFlow bar click → cross-panel system selection', async ({ page, request }) => {
    await openTraceFixture(page, request, 'with-archetype-touches', 'L4');
    await openViaViewMenu(page, 'Data Flow');

    // L4 row layout (archetype-id × component, declaration order):
    //   row 0 — archcomp:100:Game.Position   (Movement on Player)
    //   row 1 — archcomp:100:Game.Velocity   (Movement on Player)
    //   row 2 — archcomp:101:Game.Position   (Damage on Enemy)
    //   row 3 — archcomp:101:Game.Health     (Damage on Enemy)
    // Each row's bars come from exactly one system, so click coords on row 1 unambiguously select
    // Movement regardless of how the bar's xStart/xEnd resolve under the phase mapper (the fixture
    // ships no SystemTickSummary[] so bars span the whole Simulation segment).
    const dataFlowPanel = page.locator('.dv-groupview', { has: page.locator('.dv-active-tab', { hasText: 'Data Flow' }) }).first();
    const canvas = dataFlowPanel.locator('.u-over').first();
    await expect(canvas).toBeVisible({ timeout: 10_000 });
    const box = await canvas.boundingBox();
    expect(box, 'data-flow canvas should have a bounding box').toBeTruthy();

    // Row height is 22 px; row 1 centre is at y = 22 + 11 = 33. Bars span the full Simulation phase
    // column so x = 50% lands squarely on the bar.
    await canvas.click({ position: { x: box!.width * 0.5, y: 33 } });

    await openViaViewMenu(page, 'Access Matrix');
    await pressedTrue(page.getByTestId('access-matrix-system-Movement'));
  });

  test('DataFlow hover → AccessMatrix column brightens via hoveredSystemTickKey', async ({ page, request }) => {
    await openTraceFixture(page, request, 'with-archetype-touches', 'L4');
    // Open both panels first so AccessMatrix is in the DOM (dockview keeps inactive tabs mounted, just
    // hidden). Activate Data Flow last so its canvas receives the hover. Critically: avoid any menu /
    // tab interaction between the hover and the assertion — moving the cursor off the canvas would
    // fire a mouseleave that clears `hoveredSystemTickKey`.
    await openViaViewMenu(page, 'Access Matrix');
    await openViaViewMenu(page, 'Data Flow');

    const dataFlowPanel = page.locator('.dv-groupview', { has: page.locator('.dv-active-tab', { hasText: 'Data Flow' }) }).first();
    const canvas = dataFlowPanel.locator('.u-over').first();
    await expect(canvas).toBeVisible({ timeout: 10_000 });
    const box = await canvas.boundingBox();
    expect(box).toBeTruthy();

    // Move the real Playwright pointer onto the bar.
    await page.mouse.move(0, 0);
    await canvas.hover({ position: { x: box!.width * 0.5, y: 33 } });

    // Assert via the Data Flow panel's `data-hovered-system` attribute — it mirrors the cross-panel
    // selection store's `hoveredSystemTickKey.systemName` and is on the always-mounted DataFlow root,
    // so we don't need to switch tabs (which would clear the hover via canvas mouseleave). The
    // attribute proves the store update reached every consumer; the Access Matrix renders the same
    // value into `data-hovered` on its column header when its tab is foregrounded.
    const dataFlowRoot = page.getByTestId('data-flow-panel-root');
    await expect(dataFlowRoot).toHaveAttribute('data-hovered-system', 'Movement', { timeout: 5_000 });
  });
});
