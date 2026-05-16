import { test, expect } from '@playwright/test';

/**
 * Tier-0 canary for the **Open Trace** flow (umbrella #267 / sub-issue #261). Guards the core UX
 * contract that shipped without an end-to-end test: a valid `.typhon-trace` path → sidecar cache
 * build → metadata → Profiler panel mounts with the header showing the tick count.
 *
 * The fixture trace is generated on-the-fly by the DEBUG-only `POST /api/fixtures/trace` endpoint
 * so nothing is committed to the repo. The Workbench process writes it to its local app-data
 * fixtures directory; leftover files there are harmless and overwritten every test run.
 *
 * All `/api/...` calls route through the Vite dev proxy at `:5173` so the bootstrap token is
 * auto-attached — hitting Kestrel at `:5200` directly would 401 without a manual header.
 */

interface SessionSummary { sessionId: string }

async function closeAllSessions(request: import('@playwright/test').APIRequestContext): Promise<void> {
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
 * Opens a trace via the File ▸ Open .typhon-trace… menu — works whether the Welcome screen or an
 * already-open session is showing, so it can be reused for the "open a second trace" flow.
 */
async function openTraceViaMenu(page: import('@playwright/test').Page, traceFilePath: string): Promise<void> {
  await page.getByRole('menuitem', { name: 'File' }).click();
  await page.getByRole('menuitem', { name: /^open \.typhon-trace/i }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('tab', { name: /^open trace$/i }).click();
  await page.getByPlaceholder(/\.typhon-trace or/i).fill(traceFilePath);
  await page.getByRole('button', { name: /^open$/i }).click();
  await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 15_000 });
}

test.describe('Profiler — open trace (Tier-0 canary)', () => {
  test('valid trace → metadata → panel mounts with tick count', async ({ page, request }) => {
    await closeAllSessions(request);

    // Generate a minimal valid trace on the server; path is the absolute FS location we paste.
    const fx = await request.post('http://localhost:5173/api/fixtures/trace', {
      data: { tickCount: 5, instantsPerTick: 3 },
    });
    expect(fx.ok(), 'fixture endpoint should respond 200').toBeTruthy();
    const { traceFilePath, tickCount } = await fx.json();
    expect(traceFilePath, 'trace file path should be returned').toBeTruthy();
    expect(tickCount).toBe(5);

    // Start from the Welcome screen with no lingering recents. Without `localStorage.clear()` the
    // Welcome redirects to Recent Files if any past session is remembered.
    await page.addInitScript(() => {
      try { localStorage.clear(); } catch { /* ignore */ }
    });
    await page.goto('/');

    await page.getByRole('button', { name: /^open \.typhon-trace$/i }).click();
    await expect(page.getByRole('dialog')).toBeVisible();

    // The Welcome button currently snaps the dialog to the Cached tab; click the Open Trace tab
    // explicitly so the test isn't coupled to that wiring choice.
    await page.getByRole('tab', { name: /^open trace$/i }).click();
    await expect(page.getByRole('tab', { name: /^open trace$/i })).toHaveAttribute('data-state', 'active');

    // Paste the absolute path into the "Or paste absolute path" input. Avoids the FileBrowser
    // dance — the paste input is a stable entry point that the OpenTraceTab explicitly supports.
    await page.getByPlaceholder(/\.typhon-trace or/i).fill(traceFilePath);
    await page.getByRole('button', { name: /^open$/i }).click();

    // Dialog closes once the POST /api/sessions/trace round-trips.
    await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 10_000 });

    // Profiler panel mounts and the metadata branch renders the tick-count pill in the header.
    // Sidecar cache build is fast on a 5-tick fixture (<1 s) so the BuildProgressOverlay flashes
    // and disappears before we can catch it — assert on the steady-state content instead.
    await expect(page.getByText(/\b5 ticks\b/)).toBeVisible({ timeout: 15_000 });

    // Systems count comes from the header DTO's systemCount (0 for our empty-tables fixture).
    await expect(page.getByText(/\b0 systems\b/)).toBeVisible();

    // Cleanup: drop the session so the next test's closeAllSessions doesn't race this one.
    await closeAllSessions(request);
  });

  test('opening a second trace resets the viewport to the new trace’s first tick', async ({ page, request }) => {
    await closeAllSessions(request);

    // Two fixtures with distinct instantsPerTick → distinct first-tick durations, so the committed
    // viewport µs-range differs between them and a stale-viewport regression is observable.
    const fxA = await request.post('http://localhost:5173/api/fixtures/trace', {
      data: { tickCount: 6, instantsPerTick: 2 },
    });
    const fxB = await request.post('http://localhost:5173/api/fixtures/trace', {
      data: { tickCount: 6, instantsPerTick: 5 },
    });
    expect(fxA.ok() && fxB.ok(), 'both fixture builds should respond 200').toBeTruthy();
    const { traceFilePath: pathA } = await fxA.json();
    const { traceFilePath: pathB } = await fxB.json();

    await page.addInitScript(() => {
      try { localStorage.clear(); } catch { /* ignore */ }
    });
    await page.goto('/');

    // Open trace A — the first-tick effect snaps the viewport to tick 0 and selectionUrlSync
    // mirrors it into `?time=`.
    await openTraceViaMenu(page, pathA);
    await expect(page.getByText(/\b6 ticks\b/)).toBeVisible({ timeout: 15_000 });
    await expect.poll(() => new URL(page.url()).searchParams.get('time')).not.toBeNull();
    const timeA = new URL(page.url()).searchParams.get('time');

    // Open trace B over the top. Regression guard: a prior trace's viewport must NOT linger — if it
    // does, the first-tick effect's {0,0}-sentinel gate early-returns and useProfilerCache cannot
    // load chunks for a window outside B's time range, leaving the TimeArea blank until the user
    // re-selects. The fix resets viewRange on every session change, so B snaps to its own tick 0.
    await openTraceViaMenu(page, pathB);
    await expect
      .poll(() => new URL(page.url()).searchParams.get('time'), {
        message: 'viewport should switch to trace B’s first tick, not keep trace A’s',
        timeout: 15_000,
      })
      .not.toBe(timeA);

    const timeB = new URL(page.url()).searchParams.get('time');
    expect(timeB, 'trace B must commit a viewport').not.toBeNull();
    // The fixture's synthetic first tick starts at µs-origin 10 — well under 1 ms — confirming B
    // landed on its own tick 0 rather than an arbitrary leftover range.
    expect(Number(timeB!.split('-')[0])).toBeLessThan(1000);

    await closeAllSessions(request);
  });

  test('reopening the same trace restores the last viewport', async ({ page, request }) => {
    await closeAllSessions(request);

    const fx = await request.post('http://localhost:5173/api/fixtures/trace', {
      data: { tickCount: 8, instantsPerTick: 3 },
    });
    expect(fx.ok(), 'fixture build should respond 200').toBeTruthy();
    const { traceFilePath } = await fx.json();

    await page.addInitScript(() => {
      try { localStorage.clear(); } catch { /* ignore */ }
    });
    await page.goto('/');

    // Open the trace — the viewport snaps to tick 0 and selectionUrlSync mirrors it into `?time=`.
    await openTraceViaMenu(page, traceFilePath);
    await expect(page.getByText(/\b8 ticks\b/)).toBeVisible({ timeout: 15_000 });
    await expect.poll(() => new URL(page.url()).searchParams.get('time')).not.toBeNull();
    const firstTickTime = new URL(page.url()).searchParams.get('time');

    // Click into the TickOverview canvas to select a tick away from the first — the viewport
    // commits and `?time=` follows it.
    await page.locator('canvas').first().click();
    await expect
      .poll(() => new URL(page.url()).searchParams.get('time'), {
        message: 'clicking the tick overview should move the viewport off the first tick',
      })
      .not.toBe(firstTickTime);
    const selectedTime = new URL(page.url()).searchParams.get('time');

    // Reopen the SAME file. Per-file viewport memory must restore the selected tick rather than
    // snap back to the first tick — regression guard for the viewport-reset-on-every-open bug.
    await openTraceViaMenu(page, traceFilePath);
    await expect
      .poll(() => new URL(page.url()).searchParams.get('time'), {
        message: 'reopening the same trace should restore the previously selected viewport',
        timeout: 15_000,
      })
      .toBe(selectedTime);

    await closeAllSessions(request);
  });

  test('file not found → server rejects with 404 → dialog stays open with error pill', async ({ page, request }) => {
    await closeAllSessions(request);

    await page.addInitScript(() => {
      try { localStorage.clear(); } catch { /* ignore */ }
    });
    await page.goto('/');

    await page.getByRole('button', { name: /^open \.typhon-trace$/i }).click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await page.getByRole('tab', { name: /^open trace$/i }).click();
    await page.getByPlaceholder(/\.typhon-trace or/i).fill('C:\\does-not-exist\\nope.typhon-trace');
    await page.getByRole('button', { name: /^open$/i }).click();

    // Dialog stays open; error pill surfaces the server's 404 problem detail.
    await expect(page.getByRole('dialog')).toBeVisible();
    await expect(page.getByText(/failed to open trace/i)).toBeVisible({ timeout: 10_000 });
    await page.keyboard.press('Escape');
  });
});
