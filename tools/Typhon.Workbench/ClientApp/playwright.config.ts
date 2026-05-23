import { defineConfig, devices } from '@playwright/test';

// Solo-dev, local-only E2E. No CI integration (intentional).
// Prerequisite: run `dotnet run` and `npm run dev` in two terminals before `npm run test:e2e`.
export default defineConfig({
  testDir: './e2e',
  // Stage 0 (#372) deactivated every deep/workspace (zone-D) view, so the specs that drive those views
  // can no longer reach them. They are ignored — not deleted — and return (rewritten for the redesign) as
  // each view is reintroduced in Stages 2-4. Shell specs (resource tree, connect, theme, stage0-shell,
  // conformance-affordances) still run.
  testIgnore: [
    '**/schema-inspector.spec.ts',
    '**/data-browser.spec.ts',
    '**/data-flow.spec.ts',
    '**/dbmap-*.spec.ts',
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
