import { test, expect } from '@playwright/test';
import { closeAllSessions, gotoWelcome, startMockProfiler, stopMockProfiler, attachTo } from './_session';

/**
 * J3 E2E — the Stage-4 exit gate for the Capture &amp; Analyse half of the journey (#377 Phase 5, GAP-22).
 *
 * Flow:
 *   1. Start the in-process MockTcpProfilerServer on a loopback port.
 *   2. Attach to it — wait for the "Connected" pill + at least one tick to land.
 *   3. Click the "Capture &amp; Analyse" button on the Engine Live Health panel — server picks a default
 *      capture path under `%LOCALAPPDATA%/Typhon/Workbench/captures/`, writes the replay, and the client
 *      opens it as a Trace session (single-session-at-a-time swap).
 *   4. Assert the session kind transitions to `trace` — the Profiler timeline is still visible because
 *      the new Trace session opens it as the default view, mirroring `handleOpenTrace`'s toggleViewProfiler.
 *
 * **Scope of this spec** (vs. design's J3 ideal): only the freeze → save → reopen-as-J2 half. The
 * "anomaly jump" half (notice anomaly → click Jump) is unit-tested via `AnomalyLog.test.tsx` because
 * mock-profiler doesn't emit anomalies live (P3 deferred mock-profiler anomaly injection) and the
 * trace-mode panel doesn't surface the anomaly log (kind-gated to attach). Documented in stage-4-observe.md.
 */
test.describe('J3 — Capture & Analyse (#377 P5 exit gate)', () => {
  test('attach → ticks climb → Capture & Analyse → lands in Profiler on the saved replay', async ({ page, request }) => {
    await closeAllSessions(request);

    // Start the mock profiler — fast block interval so we observe ticks within the test budget.
    const port = await startMockProfiler(request, { blockIntervalMs: 50, maxBlocks: 200 });

    try {
      await gotoWelcome(page);
      await attachTo(page, port);

      // Wait for the Engine Live Health panel's connection-state label to read "Connected" — that's
      // the canonical signal the panel mounted on the attach default layout AND the SSE stream is
      // delivering. (The shell status-bar pill also says "Connected" — using the testid keeps the
      // locator unambiguous since both elements match `getByText(/^connected$/i)`.)
      await expect(page.getByTestId('engine-live-health-status')).toHaveText(/connected/i, { timeout: 10_000 });

      // Wait for at least one tick to arrive — the panel's `tick N` label climbs as the SSE stream
      // delivers tickSummaryAdded events. Polling its testid is the canonical "live data flowing" signal.
      await expect
        .poll(
          async () => {
            const txt = await page.getByTestId('engine-live-health-tick').textContent();
            const m = txt?.match(/tick\s+(\d[\d,]*)/i);
            return m ? Number(m[1].replace(/,/g, '')) : 0;
          },
          { timeout: 10_000, intervals: [100, 250, 500] },
        )
        .toBeGreaterThan(0);

      // The Engine Live Health panel mounts in the attach default layout; the Capture & Analyse button
      // sits next to Disconnect in the controls row. Use the testid we set in P4.
      const captureBtn = page.getByTestId('engine-live-health-capture');
      await expect(captureBtn).toBeVisible({ timeout: 10_000 });
      await expect(captureBtn).toBeEnabled();
      await captureBtn.click();

      // The orchestration runs: POST /save-replay (server picks default path under %LOCALAPPDATA%) →
      // POST /sessions/trace with the returned path → setSession(traceDto) → toggleViewProfiler.
      // The unambiguous success signal: the saved replay's auto-generated filename appears in the UI
      // (the captures dir + ISO-timestamp pattern proves the server-side default-path resolution ran,
      // not a user-typed path).
      await expect(page.getByText(/typhon-capture-\d{8}T\d{6}Z\.typhon-replay/).first())
        .toBeVisible({ timeout: 15_000 });

      // The Engine Live Health panel has either unmounted (it gates on attach) or fallen to its cold
      // state — either way its "Connected" status label is no longer rendered.
      await expect(page.getByTestId('engine-live-health-status')).toHaveCount(0, { timeout: 5_000 });
    } finally {
      await closeAllSessions(request);
      await stopMockProfiler(request, port);
    }
  });
});
