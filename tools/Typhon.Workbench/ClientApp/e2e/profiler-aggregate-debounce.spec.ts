import { test, expect, type Page, type APIRequestContext } from '@playwright/test';

/**
 * #345 — Pan/zoom on TimeArea must NOT fan out a `/aggregate` POST per gesture frame. The shipped
 * fix splits the viewport into a transient slot (written on every wheel notch / drag pixel) and a
 * committed slot (debounced via `WorkbenchOptions.Profiler.ViewRangeDebounceMs`, default 150 ms).
 * Cross-panel consumers (SystemDag, CriticalPath, DataFlow, AccessMatrix) re-aggregate off the
 * committed slot only — so a burst of N wheel events should produce one fan-out, not N.
 *
 * Methodology: open SystemDag (it fires `/aggregate` over the view range), record a baseline,
 * emit a burst of wheel events on the TimeArea canvas, wait past the debounce window, and assert
 * the post-burst POST count is bounded by the number of `/aggregate`-consuming hooks active in
 * the panel (≤ 3), not by the number of gesture frames in the burst (which is 12).
 *
 * The exact post-burst count depends on which hooks fire for the fixture (SystemDag wires
 * `useSystemStats` always and `useQueueBackpressure` when derived edges name queues). The
 * load-bearing assertion is that count is small and decoupled from gesture-frame count.
 */

const WHEEL_BURST_FRAMES = 12;
const DEBOUNCE_SETTLE_MS = 500; // 150 ms default debounce + generous buffer for React commit

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

async function openTraceFixture(page: Page, request: APIRequestContext): Promise<void> {
  await closeAllSessions(request);

  const fx = await request.post('http://localhost:5173/api/fixtures/trace', {
    data: { variant: 'with-access-declarations' },
  });
  expect(fx.ok(), 'fixture endpoint should respond 200').toBeTruthy();
  const { traceFilePath } = await fx.json();
  expect(traceFilePath).toBeTruthy();

  await page.addInitScript(() => {
    try { localStorage.clear(); } catch { /* ignore */ }
  });
  // Suppress the vite-plugin-checker overlay (pre-existing ESLint warnings — see data-flow.spec.ts).
  await page.addStyleTag({ content: 'vite-plugin-checker-error-overlay { display: none !important }' }).catch(() => { /* pre-nav */ });
  await page.goto('/');
  await page.addStyleTag({ content: 'vite-plugin-checker-error-overlay { display: none !important }' });

  await page.getByRole('button', { name: /^open \.typhon-trace$/i }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('tab', { name: /^open trace$/i }).click();
  await page.getByPlaceholder(/path.*typhon-trace.*typhon-replay/i).fill(traceFilePath);
  await page.getByRole('button', { name: /^open$/i }).click();

  await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 10_000 });
  await expect(page.getByText(/\b3 ticks\b/)).toBeVisible({ timeout: 15_000 });
}

async function openViaViewMenu(page: Page, label: string): Promise<void> {
  await page.getByRole('menuitem', { name: /^view$/i }).click();
  await page.getByRole('menuitem', { name: new RegExp(`^${label}$`, 'i') }).click();
  const tab = page.locator('.dv-tab', { hasText: label }).first();
  await tab.click();
  await expect(tab).toHaveClass(/dv-active-tab/);
}

async function activateTab(page: Page, label: string): Promise<void> {
  const tab = page.locator('.dv-tab', { hasText: label }).first();
  await tab.click();
  await expect(tab).toHaveClass(/dv-active-tab/);
}

test.use({ viewport: { width: 1600, height: 900 } });

test.describe('Profiler — /aggregate debounce on TimeArea pan/zoom (#345)', () => {
  test('burst of wheel events on TimeArea fires /aggregate at most once per consumer hook, not per gesture frame', async ({
    page,
    request,
  }) => {
    await openTraceFixture(page, request);
    await openViaViewMenu(page, 'System DAG');
    // SystemDag landed in the same dockview group as Profiler and stole the active-tab slot. Reactivate
    // Profiler so its TimeArea canvas is visible + hit-testable for `page.mouse.wheel`. Dockview keeps
    // inactive tabs mounted in the DOM, so SystemDag's hooks still fire `/aggregate` on viewRange
    // changes — which is exactly what we're measuring.
    await activateTab(page, 'Profiler');

    // Count every /aggregate POST against the trace session — the SystemDag panel is the canonical
    // consumer that re-fetches on viewRange changes. `page.on('request')` fires for every fetch the
    // page makes; we filter by URL + method.
    let aggregateCount = 0;
    page.on('request', (req) => {
      if (req.method() === 'POST' && /\/api\/sessions\/[^/]+\/aggregate$/.test(req.url())) {
        aggregateCount++;
      }
    });

    // Let SystemDag's initial /aggregate fetch(es) settle before we measure the burst impact. The
    // panel mounts, fetches per-system stats, possibly per-queue stats — wait long enough that
    // those are flushed into the request log, then snapshot the count as the baseline.
    await page.waitForTimeout(800);
    const baselineCount = aggregateCount;

    // Locate the TimeArea canvas (data-testid added for this canary). The canvas is the gesture
    // surface — wheel events on it translate to viewport zoom in the renderer.
    const canvas = page.getByTestId('profiler-time-area-canvas');
    await expect(canvas).toBeVisible({ timeout: 10_000 });
    const box = await canvas.boundingBox();
    expect(box, 'TimeArea canvas should have a bounding box').toBeTruthy();

    // Park the pointer over the canvas centre so wheel events target the right element. Then emit a
    // burst of 12 wheel notches — emulates a real wheel-zoom gesture (each notch is ~16 ms apart in
    // hardware). Before #345 every notch synchronously wrote viewRange and fanned out /aggregate to
    // every consumer; after the fix, only transientViewRange moves and the debounced commit fires
    // /aggregate once after the burst settles.
    await page.mouse.move(box!.x + box!.width / 2, box!.y + box!.height / 2);
    for (let i = 0; i < WHEEL_BURST_FRAMES; i++) {
      await page.mouse.wheel(0, -100); // negative = zoom in (browsers report wheel up as -delta)
    }

    // Wait past the default 150 ms debounce window with generous slack for React commit + fetch
    // start. After this point every consumer's debounced fetch has run.
    await page.waitForTimeout(DEBOUNCE_SETTLE_MS);

    const burstCount = aggregateCount - baselineCount;

    // Load-bearing assertion: the burst-triggered POST count must be decoupled from the gesture-
    // frame count. Before #345, every wheel notch synchronously fanned out /aggregate to every
    // consumer (~24 POSTs for this fixture). After the fix, the debounce coalesces the burst into
    // one settle cycle per consumer (≤ ~3 hooks). The assertion `burstCount < WHEEL_BURST_FRAMES`
    // is the cleanest possible statement of "not proportional to N gesture frames" — it grows with
    // the consumer-hook count rather than the frame count, so adding a new hook to SystemDag won't
    // silently weaken this assertion the way an absolute `≤ 3` would.
    expect(
      burstCount,
      `${WHEEL_BURST_FRAMES} wheel frames produced ${burstCount} /aggregate POSTs — should be << frame count (one debounced fetch per consumer hook, not one per frame)`,
    ).toBeLessThan(WHEEL_BURST_FRAMES);

    // Sanity check: prove the wheel events actually reached the canvas. If burstCount were 0 it
    // could mean the wheel never fired or the queryKey didn't change (zoom hit a viewport bound).
    // The fixture is 3 ticks wide; zooming in always shrinks the range, so a successful gesture
    // must shift the queryKey and produce at least one post-debounce fetch.
    expect(
      burstCount,
      'Zoom gesture should produce at least one /aggregate POST after the debounce settles — zero means the wheel events never reached the canvas or the viewport snapped',
    ).toBeGreaterThanOrEqual(1);

    await closeAllSessions(request);
  });
});
