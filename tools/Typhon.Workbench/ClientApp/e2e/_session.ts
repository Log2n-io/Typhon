import { expect, type Page, type APIRequestContext } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// Shared session helpers for the Stage-1 e2e suites (slice / nav-history / palette / density / session-switch).
// All `/api/...` calls route through the Vite dev proxy at :5173 so the bootstrap token is auto-attached.

export const DEMO_DIR = path.resolve('../bin/Debug/net10.0/DemoData');

export async function closeAllSessions(request: APIRequestContext): Promise<void> {
  const list = await request.get('http://localhost:5173/api/sessions');
  if (!list.ok()) return;
  const { sessions = [] } = await list.json();
  for (const s of sessions as Array<{ sessionId: string }>) {
    await request.delete(`http://localhost:5173/api/sessions/${s.sessionId}`, { headers: { 'X-Session-Token': s.sessionId } });
  }
}

/** Create an empty `.typhon` file in the DemoData dir (idempotent). Returns its bare name. */
export function ensureDemoFile(name = 'demo.typhon'): string {
  fs.mkdirSync(DEMO_DIR, { recursive: true });
  fs.writeFileSync(path.join(DEMO_DIR, name), '');
  return name;
}

/**
 * Seed a demo `.typhon` into a *valid empty database*. A 0-byte file is NOT openable — the engine has to
 * initialise it. We do that the same way the slice test does: POST a throwaway session on the bare name
 * (which creates + initialises the file under DemoData), then delete that session. Idempotent.
 */
export async function seedDemoFile(request: APIRequestContext, name = 'demo.typhon'): Promise<void> {
  ensureDemoFile(name);
  const seed = await request.post('http://localhost:5173/api/sessions/file', { data: { filePath: name } });
  if (seed.ok()) {
    const j = await seed.json();
    if (j?.sessionId) {
      await request.delete(`http://localhost:5173/api/sessions/${j.sessionId}`, { headers: { 'X-Session-Token': j.sessionId } });
    }
  }
}

/** Land on the Welcome screen with a clean slate (no remembered recents redirecting us away). */
export async function gotoWelcome(page: Page): Promise<void> {
  await page.addInitScript(() => {
    try { localStorage.clear(); } catch { /* ignore */ }
  });
  await page.goto('/');
}

/**
 * Open an (empty) demo `.typhon` through the real Open-File dialog. Works whether the Welcome screen is
 * showing (uses the Welcome button) or a session is already open (uses File ▸ Open .typhon File…), so it
 * doubles as the "switch session without closing" path.
 */
export async function openDemoFile(page: Page, request: APIRequestContext, name = 'demo.typhon'): Promise<void> {
  await seedDemoFile(request, name);
  const welcomeBtn = page.getByRole('button', { name: /^open \.typhon file$/i });
  if (await welcomeBtn.isVisible().catch(() => false)) {
    await welcomeBtn.click();
  } else {
    await page.getByRole('menuitem', { name: 'File' }).click();
    await page.getByRole('menuitem', { name: /^open \.typhon file/i }).click();
  }
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByPlaceholder(/path/i).first().fill(DEMO_DIR);
  const row = page.getByText(new RegExp(`^${name.replace('.', '\\.')}$`)).first();
  await expect(row).toBeVisible({ timeout: 10_000 });
  await row.click();
  await page.getByRole('button', { name: /^open$/i }).click();
  await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 10_000 });
  await expect(page.locator('body')).toContainText(/Storage|DataEngine/i, { timeout: 10_000 });
}

/** Open the deterministic Dev Fixture database (base-tests) from the Welcome screen — reliably valid. */
export async function openDevFixture(page: Page): Promise<void> {
  await page.getByRole('button', { name: /^dev fixture$/i }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('button', { name: /^create & open$/i }).click();
  await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 20_000 });
  await expect(page.locator('body')).toContainText(/Storage|DataEngine/i, { timeout: 15_000 });
}

/** Generate a minimal valid `.typhon-trace` via the DEBUG fixture endpoint; returns its absolute path. */
export async function makeTraceFixture(
  request: APIRequestContext,
  opts: { tickCount?: number; instantsPerTick?: number } = {},
): Promise<string> {
  const fx = await request.post('http://localhost:5173/api/fixtures/trace', {
    data: { tickCount: opts.tickCount ?? 5, instantsPerTick: opts.instantsPerTick ?? 3 },
  });
  expect(fx.ok(), 'trace fixture endpoint should respond 200').toBeTruthy();
  const { traceFilePath } = await fx.json();
  expect(traceFilePath, 'trace fixture should return a path').toBeTruthy();
  return traceFilePath;
}

/** Open a trace through File ▸ Open .typhon-trace… (works from Welcome or an open session). */
export async function openTrace(page: Page, traceFilePath: string): Promise<void> {
  const welcomeBtn = page.getByRole('button', { name: /^open \.typhon-trace$/i });
  if (await welcomeBtn.isVisible().catch(() => false)) {
    await welcomeBtn.click();
  } else {
    await page.getByRole('menuitem', { name: 'File' }).click();
    await page.getByRole('menuitem', { name: /^open \.typhon-trace/i }).click();
  }
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('tab', { name: /^open trace$/i }).click();
  await page.getByPlaceholder(/\.typhon-trace or/i).fill(traceFilePath);
  await page.getByRole('button', { name: /^open$/i }).click();
  await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 15_000 });
}

/** Start an in-process mock TCP profiler via the DEBUG fixture endpoint; returns its loopback port. */
export async function startMockProfiler(
  request: APIRequestContext,
  opts: { blockIntervalMs?: number; maxBlocks?: number } = {},
): Promise<number> {
  const start = await request.post('http://localhost:5173/api/fixtures/mock-profiler', {
    data: { blockIntervalMs: opts.blockIntervalMs ?? 50, maxBlocks: opts.maxBlocks ?? 200 },
  });
  expect(start.ok(), 'mock-profiler fixture endpoint should respond 200').toBeTruthy();
  const { port } = (await start.json()) as { port: number };
  expect(port).toBeGreaterThan(0);
  return port;
}

export async function stopMockProfiler(request: APIRequestContext, port: number | null): Promise<void> {
  if (port == null) return;
  await request.delete(`http://localhost:5173/api/fixtures/mock-profiler/${port}`);
}

/** Attach to a running profiler endpoint through File ▸ Attach / the Welcome Attach button. */
export async function attachTo(page: Page, port: number): Promise<void> {
  const welcomeBtn = page.getByRole('button', { name: /^attach to engine$/i });
  if (await welcomeBtn.isVisible().catch(() => false)) {
    await welcomeBtn.click();
  } else {
    await page.getByRole('menuitem', { name: 'File' }).click();
    await page.getByRole('menuitem', { name: /^attach to engine/i }).click();
  }
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByPlaceholder('localhost:9100').fill(`127.0.0.1:${port}`);
  await page.getByRole('button', { name: /^attach$/i }).click();
  await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 10_000 });
}

/** Open the command palette and type a value (real keystrokes — cmdk is a controlled React input). */
export async function openPalette(page: Page, value: string): Promise<void> {
  await page.keyboard.press('Control+k');
  const input = page.locator('input[cmdk-input]');
  await expect(input).toBeVisible();
  await input.fill(value);
}
