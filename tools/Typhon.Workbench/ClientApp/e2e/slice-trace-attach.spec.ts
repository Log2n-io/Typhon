import { test, expect } from '@playwright/test';
import {
  closeAllSessions,
  gotoWelcome,
  makeTraceFixture,
  openTrace,
  startMockProfiler,
  stopMockProfiler,
  attachTo,
} from './_session';

// AC1.9 (Trace + Attach legs) — the load-a-file slice across the three session kinds. The Open leg lives in
// slice-load-inspect.spec.ts. For Trace/Attach in Stage 1 we assert *shell-frame correctness*: opening the
// session mounts the right zones — the Systems & Queries navigator (zone C), the Inspector (zone E) and the
// Logs drawer (zone F) — and mounts NO zone-D deep view (the Profiler is gated off until Stage 2).
//
// Deferred (noted, not silently skipped): the populated navigate→inspect leg for Trace/Attach needs either
// the reactivated Profiler timeline or a systems-bearing fixture (the DEBUG trace/attach fixtures emit zero
// systems), so the navigator is legitimately empty here. That leg returns with Stage 2.

const tab = (page: import('@playwright/test').Page, name: string | RegExp) =>
  page.locator('.dv-tab').filter({ hasText: name });

async function assertShellFrame(page: import('@playwright/test').Page) {
  // Zone C navigator, zone E inspector, zone F logs all mounted (a tab exists because dockview added the
  // panel) — the cross-kind shell-frame contract.
  await expect(tab(page, 'Systems & Queries')).toBeVisible();
  await expect(tab(page, 'Detail')).toBeVisible();
  await expect(tab(page, 'Logs')).toBeVisible();
  // No zone-D deep view: the Profiler / Timeline / Top Spans must NOT be mounted in Stage 1.
  await expect(tab(page, /Profiler|Timeline|Top Spans/i)).toHaveCount(0);
}

test.describe('AC1.9 — load-a-file slice (Trace / Attach shell frame)', () => {
  test('opening a trace mounts the trace shell (navigator + inspector + logs, no zone-D)', async ({ page, request }) => {
    await closeAllSessions(request);
    const tracePath = await makeTraceFixture(request, { tickCount: 5, instantsPerTick: 3 });
    await gotoWelcome(page);
    await openTrace(page, tracePath);

    await assertShellFrame(page);
    // The navigator mounted its content (not just a tab stub): the empty fixture carries no systems/queries,
    // so it shows its empty state. (A systems-bearing fixture / Stage-2 Profiler enables the select→inspect leg.)
    await expect(page.getByText(/no systems or queries/i)).toBeVisible({ timeout: 10_000 });
    await closeAllSessions(request);
  });

  test('attaching to a live engine mounts the attach shell (navigator + inspector + logs, no zone-D)', async ({ page, request }) => {
    await closeAllSessions(request);
    let port: number | null = null;
    try {
      port = await startMockProfiler(request, { blockIntervalMs: 50, maxBlocks: 200 });
      await gotoWelcome(page);
      await attachTo(page, port);

      await assertShellFrame(page);
    } finally {
      await closeAllSessions(request);
      await stopMockProfiler(request, port);
    }
  });
});
