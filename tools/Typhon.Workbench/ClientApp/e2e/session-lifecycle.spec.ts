import { test, expect } from '@playwright/test';
import { closeAllSessions, gotoWelcome } from './_session';

/**
 * AC7 of the Workbench ALC lifecycle fix. The user-stated bug: open DB1, close, open DB2 — DB2 fails because
 * DB1's collectible AssemblyLoadContext can't unload (the engine's process-static `ArchetypeRegistry` pinned
 * its Types). This spec drives the full UI flow that the fix is supposed to make work end-to-end:
 *
 * 1. Generate Dev Fixture A via the panel (`base-tests-a`).
 * 2. Wait for the auto-open — session lands in Ready.
 * 3. Close the session.
 * 4. Open the Dev Fixture panel again, generate fixture B (`base-tests-b`) with different config.
 * 5. Auto-open succeeds. No errors in the log; the new session reports a fresh `sessionId` + the new path.
 *
 * Pre-fix: step 4 surfaced as either a hung "Generating…" progress bar (back-pressure timeout cascade) or
 * an `Archetype not registered` server-side assertion that killed the process.
 *
 * Requires `/wb-dev start` — uses the Vite dev proxy at :5173 so the bootstrap token flows automatically.
 */
test.describe('Workbench ALC lifecycle — sequential generate + open of different fixtures', () => {
  test('generate fixture A → open → close → generate fixture B → open: both opens succeed', async ({ page, request }) => {
    await closeAllSessions(request);
    await gotoWelcome(page);

    // ── Open the Dev Fixture panel from Welcome (refactored — it's a panel, not a dialog tab) ───────
    await page.getByRole('button', { name: /^dev fixture$/i }).click();

    // The panel mounts inside the dock host; identify it by the database-name input testid.
    const dbNameInput = page.getByTestId('devfixture-dbname');
    await expect(dbNameInput).toBeVisible({ timeout: 10_000 });

    // ── Round 1: generate "base-tests-a" with the Tiny preset ──────────────────────────────────────
    await page.getByTestId('devfixture-preset-tiny').click();
    await dbNameInput.fill('base-tests-a');
    // Force = true so we don't reuse any prior on-disk state.
    await page.getByTestId('devfixture-force').check();
    await page.getByTestId('devfixture-start').click();

    // Wait for the auto-open after generation completes. The session is established when the dock host
    // shows engine content (Resource Tree / DataEngine).
    await expect(page.locator('body')).toContainText(/DataEngine|ManagedPagedMMF/i, { timeout: 60_000 });

    // Snapshot the active sessionId from the API — we use it later to confirm the second open is a NEW session.
    const sessionsAfterFirst = await listSessions(request);
    expect(sessionsAfterFirst.length).toBe(1);
    const firstSessionId = sessionsAfterFirst[0].sessionId;
    const firstFilePath = sessionsAfterFirst[0].filePath ?? '';
    expect(firstFilePath).toContain('base-tests-a');

    // ── Close the session — the canonical UI path is Session → Close ───────────────────────────────
    await page.getByRole('menuitem', { name: 'Session' }).click();
    await page.getByRole('menuitem', { name: /^close session$/i }).click();

    // Wait for the close to land — sessions list goes back to 0.
    await expect.poll(async () => (await listSessions(request)).length, { timeout: 15_000 }).toBe(0);

    // ── Round 2: generate "base-tests-b" with a DIFFERENT preset + name ─────────────────────────────
    // The Dev Fixture panel should still be docked from before (panels persist across sessions). If for some
    // reason it isn't, the View menu reopens it; we test the typical "still there" case here.
    await page.getByRole('menuitem', { name: 'View' }).click();
    await page.getByRole('menuitem', { name: /^dev fixture/i }).click();
    await expect(dbNameInput).toBeVisible();

    // Pick a different shape so the on-disk hash differs from round 1 — Sparse drops factories + multi-affix items.
    await page.getByTestId('devfixture-preset-sparse').click();
    await dbNameInput.fill('base-tests-b');
    await page.getByTestId('devfixture-force').check();
    await page.getByTestId('devfixture-start').click();

    // ── Verify the second auto-open succeeds. PRE-FIX: this is the step that crashed or hung ────────
    await expect(page.locator('body')).toContainText(/DataEngine|ManagedPagedMMF/i, { timeout: 60_000 });

    const sessionsAfterSecond = await listSessions(request);
    expect(sessionsAfterSecond.length, 'exactly one session after round 2').toBe(1);
    const secondSession = sessionsAfterSecond[0];
    expect(secondSession.sessionId, 'second session has a fresh sessionId').not.toBe(firstSessionId);
    expect(secondSession.filePath ?? '', 'second session points at base-tests-b').toContain('base-tests-b');

    // Cleanup — leave no live sessions for the next test in the run.
    await closeAllSessions(request);
  });
});

/** Pull the active session list via the API. The Vite proxy attaches the bootstrap token automatically. */
async function listSessions(request: import('@playwright/test').APIRequestContext): Promise<Array<{ sessionId: string; filePath?: string }>> {
  const r = await request.get('http://localhost:5173/api/sessions');
  if (!r.ok()) return [];
  const j = await r.json();
  return (j?.sessions ?? []) as Array<{ sessionId: string; filePath?: string }>;
}
