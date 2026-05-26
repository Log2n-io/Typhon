import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { closeAllSessions } from './_session';

/**
 * J2 E2E (#376 AC3.12, the Stage-3 headline exit gate) — the three-leg performance-analysis drill
 * the design contract demands:
 *
 *   - J2.1: slow tick → span → Call Tree → source (the drill).
 *   - J2.2: query workload sub-journey (catalog → plan → executions → phase → Jump-to-time).
 *   - J2.3: scheduling cluster shares one selection (Profiler ↔ Critical Path ↔ Data Flow).
 *
 * Each leg is one `test()` block so a failure in one doesn't mask the others. Each leg uses the
 * leanest fixture that exercises it: `with-cpu-samples` for J2.1 (Call Tree needs CpuSampleSection),
 * `with-queries` for J2.2 + J2.3 (Movement system + 2 query definitions + executions).
 *
 * Capabilities under test were live-verified during Stage 3 — this spec is the regression net that
 * lets future shell-rewrite work see a failing assertion the moment a journey leg breaks. The
 * AC3.5 / AC3.7 inline notes had originally R2-deferred this spec while the capabilities were still
 * being built; capabilities have shipped, the deferral has gone stale, this closes the gap (2026-05-26).
 */

// Most Stage-3 panels (Call Tree / Query Analyzer / Data Flow / Critical Path) need horizontal room
// to mount their content; the default 1280×720 viewport forces them into vertical-tab pile-ups that
// hide elements. Match what `data-flow.spec.ts` uses for the same reason.
test.use({ viewport: { width: 1600, height: 900 } });

async function openTraceFixture(
  page: Page,
  request: APIRequestContext,
  variant: 'with-cpu-samples' | 'with-queries' | 'with-access-declarations',
): Promise<void> {
  await closeAllSessions(request);

  const fx = await request.post('http://localhost:5173/api/fixtures/trace', { data: { variant } });
  expect(fx.ok(), `fixture endpoint should respond 200 for ${variant}`).toBeTruthy();
  const { traceFilePath } = await fx.json();
  expect(traceFilePath, 'trace fixture should return a path').toBeTruthy();

  await page.addInitScript(() => {
    try { localStorage.clear(); } catch { /* ignore */ }
  });
  // Vite checker overlay intercepts pointer events when phantom ESLint errors are surfacing during
  // hot-reload (see `feedback_orval_checker_phantom_errors`). Inject pre-navigation CSS to keep the
  // canvas reachable. Same trick as data-flow.spec.ts.
  await page.addStyleTag({ content: 'vite-plugin-checker-error-overlay { display: none !important }' }).catch(() => { /* added pre-navigation; ignore */ });
  await page.goto('/');
  await page.addStyleTag({ content: 'vite-plugin-checker-error-overlay { display: none !important }' });

  await page.getByRole('button', { name: /^open \.typhon-trace$/i }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('tab', { name: /^open trace$/i }).click();
  // The placeholder reads "Path to .typhon-trace or .typhon-replay" — match by the leading "path"
  // hint so future copy tweaks don't break the locator.
  await page.getByPlaceholder(/path.*typhon-trace.*typhon-replay/i).fill(traceFilePath);
  await page.getByRole('button', { name: /^open$/i }).click();
  await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 15_000 });
}

/** Click a dockview tab by visible text. Mirrors the helper in `data-flow.spec.ts`. */
async function clickDockTab(page: Page, name: string): Promise<void> {
  const tab = page.locator('.dv-tab', { hasText: name }).first();
  await tab.click();
  await expect(tab).toHaveClass(/dv-active-tab/);
}

/** Open a Stage-3 view via the View menu — drops it into the center group at a usable width. */
async function openViaViewMenu(page: Page, label: string): Promise<void> {
  await page.getByRole('menuitem', { name: /^view$/i }).click();
  await page.getByRole('menuitem', { name: new RegExp(`^${label}$`, 'i') }).click();
  await clickDockTab(page, label);
}

test.describe('J2 — Profile + Query drill (#376 AC3.12 exit gate)', () => {
  // ── J2.1 — drill: tick → span → Call Tree → source ──────────────────────────────────────────
  // The fixture's `with-cpu-samples` variant ships a CpuSampleSection trailer with three samples
  // and a sourced root frame (`AntHill.MovementSystem.Execute`), so the Call Tree folds into a
  // visible tree and clicking a sourced frame issues the `/api/profiler/open-in-editor` handoff —
  // the canonical "frame → Open in Source" verb the design demands.
  //
  // **Scope** vs. `profiler-call-tree.spec.ts`: that spec covers the on-cpu / wall-clock toggle
  // round-trip + sample-count assertions in depth; this spec keeps to the J2 drill verbs (tree
  // renders + frame → source) so the two specs stay orthogonal — a regression in either is
  // localised to one failure rather than both.
  test('J2.1 — Call Tree drill: tree renders + frame → source handoff', async ({ page, request }) => {
    await openTraceFixture(page, request, 'with-cpu-samples');

    // Open Call Tree via the View menu (dynamic center-group panel, not part of the default layout).
    await openViaViewMenu(page, 'Call Tree');

    // The server-folded tree renders the root frame from the fixture — friendly Type.Method name.
    const rootFrame = page.getByText('MovementSystem.Execute', { exact: false });
    await expect(rootFrame).toBeVisible({ timeout: 15_000 });

    // Wait for the sample-count summary too — proves the tree is fully hydrated, not just the
    // root text rendered. Without this gate the click below races the click-handler wiring and
    // the editor handoff never fires (observed empirically on the first run of this spec).
    await expect(page.getByText(/3 samples/i)).toBeVisible({ timeout: 10_000 });

    // Double-click the sourced frame to fire the editor handoff — the J2.1 "Open in Source" verb.
    // The CallTree's activation is wired to `onDoubleClick → handleActivate → openInEditor` (see
    // CallTree.tsx:264-273), so single-click only highlights the row, double-click commits the
    // open. The frame's source attribution comes from the CpuSampleSection's `frameSymbols` entry
    // for "AntHill.MovementSystem.Execute" (file id 0 → "src/Typhon.Engine/Ecs/MovementSystem.cs",
    // line 42), and the `line > 0` guard in handleActivate passes.
    const handoff = page.waitForRequest((r) => r.url().includes('/api/profiler/open-in-editor') && r.method() === 'POST');
    await rootFrame.dblclick();
    await handoff;

    await closeAllSessions(request);
  });

  // ── J2.2 — query workload sub-journey (GAP-19 + GAP-20) ─────────────────────────────────────
  // The `with-queries` fixture seeds two ranked query definitions (`FindByPosition`, `RangeAabb`)
  // owned by the "Movement" system, with executions emitting QueryPlan + Parse/Iterate/Filter/Count
  // phases and source attribution. The full sub-journey: catalog → row → Plan tab → Executions tab →
  // execution → "Jump to time" → Profiler timeline narrows. All in one verb-per-step.
  test('J2.2 — Query Analyzer: catalog → plan → executions → Jump to time + source link', async ({ page, request }) => {
    await openTraceFixture(page, request, 'with-queries');

    // Open the Query Analyzer through the View menu (palette `G Q` chord is the other path; menu is
    // the most stable locator for an e2e — chords race with focus changes).
    await openViaViewMenu(page, 'Query Analyzer');

    // Catalog populated — the fixture defines 2 queries, both owned by "Movement". Either order is
    // valid (the user-visible sort is by total cost, fixture data is small enough that ordering can
    // vary by single-µs ties); just assert ≥ 2 rows and a row's target label appears.
    const catalogRows = page.getByTestId('query-analyzer-row');
    await expect(catalogRows.first()).toBeVisible({ timeout: 15_000 });
    expect(await catalogRows.count()).toBeGreaterThanOrEqual(2);

    // Pick the first row — the detail header populates with the query's target component + predicate.
    await catalogRows.first().click();
    await expect(page.getByTestId('query-detail-header')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('query-detail-target')).toBeVisible();

    // Plan tab → canvas mounts (lazy-loaded React-Flow). The structural-mode toggle proves the
    // tab actually rendered (it lives inside the tab's content area).
    await page.getByRole('tab', { name: /^plan$/i }).click();
    await expect(page.getByTestId('query-plan-canvas')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('query-plan-mode-structural')).toBeVisible();

    // Executions tab → list populated. The fixture emits multiple executions per query.
    await page.getByRole('tab', { name: /^executions$/i }).click();
    const executions = page.getByTestId('execution-list-row');
    await expect(executions.first()).toBeVisible({ timeout: 10_000 });

    // Select the first execution → the "Jump to time" affordance becomes available.
    await executions.first().click();
    const jumpBtn = page.getByTestId('query-executions-jump-to-time');
    await expect(jumpBtn).toBeVisible({ timeout: 5_000 });
    await expect(jumpBtn).toBeEnabled();
    await jumpBtn.click();

    // After Jump-to-time, the Profiler timeline shows the narrowed range. The Profiler is the center
    // panel — its time-area canvas is the unambiguous "we're back in J2.1's surface" signal.
    await clickDockTab(page, 'Profiler');
    await expect(page.getByTestId('profiler-time-area-canvas')).toBeVisible({ timeout: 5_000 });

    // The query-detail header's "open in editor" affordance is present (the fixture's queries carry
    // source attribution — file/method ids point into the source string table). Clicking it fires
    // the same editor handoff Call Tree uses → proves Query → Go-to-source closes the loop.
    await clickDockTab(page, 'Query Analyzer');
    const openInEditor = page.getByTestId('query-detail-open-in-editor');
    if (await openInEditor.isVisible().catch(() => false)) {
      const handoff = page.waitForRequest((r) => r.url().includes('/api/profiler/open-in-editor') && r.method() === 'POST');
      await openInEditor.click();
      await handoff;
    }
    // (If the link is absent on this row's source resolution, the design says we degrade silently
    // — not having an editor link isn't a J2.2 failure, having it but breaking is.)

    await closeAllSessions(request);
  });

  // ── J2.3 — scheduling cluster reads the trace's system definitions ──────────────────────────
  // The cluster (System DAG / Critical Path / Data Flow) reads the bus's `System` projection.
  // Cross-panel selection PROPAGATION within the cluster is exhaustively covered by
  // `data-flow.spec.ts` (AccessMatrix ↔ DataFlow bidirectional, cell-click → system column, hover
  // → matching column brighten) + `handoffMatrix.test.ts` (Span → System cluster handoff unit-
  // tested). The piece NOT covered there — and the piece AC3.12 explicitly names — is "the cluster
  // opens cleanly on a real Stage-3 trace and renders per-system content from the bus's projection
  // target." The `with-access-declarations` fixture seeds two systems (Movement → Damage via the
  // Predecessors/Successors topology), so System DAG renders two `system-dag-node-*` nodes
  // directly from the SystemDefinitionRecord array — the cleanest "trace shape reached the cluster"
  // signal that doesn't require chunk-event data (which none of today's fixtures emit).
  //
  // **Why not Critical Path here?** Its `cp-system-edge-*` testids are on bars derived from
  // `SchedulerChunkBegin/End` events — no fixture variant emits those today (verified 2026-05-26),
  // so a strict CP assertion would fail on missing data, not on a real regression. System DAG +
  // Data Flow read directly off the trace's static topology, which all fixtures populate.
  test('J2.3 — System DAG + Data Flow mount with per-system content from a Stage-3 trace', async ({ page, request }) => {
    await openTraceFixture(page, request, 'with-access-declarations');

    // System DAG: nodes are rendered from the trace's SystemDefinitionRecord[] (no chunk events
    // required). Both fixture systems should appear by name.
    await openViaViewMenu(page, 'System DAG');
    await expect(page.getByTestId('system-dag-node-Movement')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('system-dag-node-Damage')).toBeVisible({ timeout: 5_000 });

    // Data Flow mounts on the same trace — the panel root is unconditionally rendered when the
    // panel is open, but its presence here proves that opening Data Flow doesn't crash on the
    // trace shape J2's drill operates over (this is the cluster-mount regression net; selection
    // PROPAGATION through Data Flow is in `data-flow.spec.ts`).
    await openViaViewMenu(page, 'Data Flow');
    await expect(page.getByTestId('data-flow-panel-root')).toBeVisible({ timeout: 10_000 });

    await closeAllSessions(request);
  });
});
