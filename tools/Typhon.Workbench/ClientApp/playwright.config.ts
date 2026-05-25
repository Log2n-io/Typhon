import { defineConfig, devices } from '@playwright/test';

// Solo-dev, local-only E2E. No CI integration (intentional).
// Prerequisite: run `dotnet run` and `npm run dev` in two terminals before `npm run test:e2e`.
export default defineConfig({
  testDir: './e2e',
  // Stage 0 (#372) deactivated every deep/workspace (zone-D) view, so the specs that drive those views
  // can no longer reach them. They are ignored — not deleted — and return (rewritten for the redesign) as
  // each view is reintroduced in Stages 2-4. Shell specs (resource tree, connect, theme, stage0-shell,
  // conformance-affordances) still run. Data Browser returned in Stage 2 Phase 2; File Map in Phase 3.
  //
  // Stage 3 Phase 1 reintroduced the Profiler timeline + Top Spans (the views render — the trace-open canaries
  // pass). BUT these profiler specs predate Stage 0 and assert OLD-shell behaviours that Stages 1-2 rewrote
  // (the `?time=` viewport-on-canvas-click sync, session switch-without-close chrome, per-file viewport restore,
  // `/aggregate` coalescing). They need **rewriting for the redesigned shell** before they can run green — that
  // is a focused Stage-3 e2e pass, not a Phase-1 line item. Kept ignored until then; Phase-1 functional proof
  // rides the unit/component suite (resolver + conformance) + the passing render canaries + manual.
  testIgnore: [
    '**/data-flow.spec.ts',
    '**/profiler-*.spec.ts',
  ],
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: 'list',
  use: {
    baseURL: 'http://localhost:5173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
  ],
});
