import { test, expect, type APIRequestContext } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { closeAllSessions, DEMO_DIR } from './_session';

/**
 * #621 — the Workbench yields the database, and stays useful while it has.
 *
 * <p>The scenario is the dev loop this feature exists for: your application is running and owns the database, and you
 * want to look at a capture. Before this change the Workbench had no session at all in that state, so there was
 * nothing to show; now it opens <b>paused</b>, keeps every file-backed capability, and reopens on its own when the
 * application exits.</p>
 *
 * <p>The holder is simulated by writing <c>db.lock</c> with a live PID — which is exactly what the engine's own
 * acquisition path reads. It never asks whether that PID is <i>another</i> process, so this takes the same branch a
 * real second engine takes, with none of the timing flake of spawning one.</p>
 */
const BUNDLE = path.join(DEMO_DIR, 'paused-e2e.typhon');
const LOCK = path.join(BUNDLE, 'db.lock');

async function seedBundle(request: APIRequestContext): Promise<void> {
  // Create + initialise a real database by opening it once, then closing it — a bare directory is not openable.
  fs.mkdirSync(DEMO_DIR, { recursive: true });
  const seed = await request.post('http://localhost:5173/api/sessions/file', { data: { filePath: BUNDLE } });
  if (seed.ok()) {
    const j = await seed.json();
    if (j?.sessionId) {
      await request.delete(`http://localhost:5173/api/sessions/${j.sessionId}`, {
        headers: { 'X-Session-Token': j.sessionId },
      });
    }
  }
}

function lockDatabase(): void {
  fs.writeFileSync(
    LOCK,
    JSON.stringify({ pid: process.pid, startedAt: new Date().toISOString(), machineName: process.env.COMPUTERNAME ?? 'unknown' }),
  );
}

function releaseDatabase(): void {
  try {
    fs.rmSync(LOCK, { force: true });
  } catch {
    /* already gone */
  }
}

/**
 * Opens BUNDLE through the real Connect-dialog flow: type the containing DIRECTORY, click the bundle row, press Open.
 * Filling the whole path and pressing Enter does not drive this browser — a spec that did would appear to pass while
 * never opening anything.
 */
async function openBundle(page: import('@playwright/test').Page): Promise<void> {
  await page.goto('/');
  await page.getByRole('button', { name: /^open typhon database$/i }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByPlaceholder(/path/i).first().fill(DEMO_DIR);
  const row = page.getByText(/^paused-e2e\.typhon$/).first();
  await expect(row).toBeVisible({ timeout: 10_000 });
  await row.click();
  await page.getByRole('button', { name: /^open$/i }).click();
  await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 15_000 });
}

test.describe('#621 — paused session', () => {
  test.beforeEach(async ({ request, page }) => {
    await closeAllSessions(request);
    releaseDatabase();
    await seedBundle(request);
    await page.addInitScript(() => {
      try {
        localStorage.clear();
      } catch {
        /* ignore */
      }
    });
  });

  test.afterEach(async ({ request }) => {
    releaseDatabase();
    await closeAllSessions(request);
  });

  test('opening a held database shows the paused banner naming the holder', async ({ page }) => {
    lockDatabase();

    await openBundle(page);

    // The banner is the whole point: "busy" without naming who is not actionable.
    const banner = page.getByTestId('paused-banner');
    await expect(banner).toBeVisible({ timeout: 20_000 });
    await expect(banner).toContainText(/database released/i);
    await expect(banner).toContainText(String(process.pid));

    // A paused session is NOT an error state — the shell stays up rather than bouncing back to Welcome.
    await expect(page.getByRole('button', { name: /^open typhon database$/i })).toHaveCount(0);
  });

  test('the banner clears on its own once the holder releases the database', async ({ page }) => {
    lockDatabase();

    await openBundle(page);
    await expect(page.getByTestId('paused-banner')).toBeVisible({ timeout: 20_000 });

    // The application exits. Nothing is clicked — resume is detected server-side and the banner must follow.
    releaseDatabase();

    await expect(page.getByTestId('paused-banner')).toHaveCount(0, { timeout: 30_000 });
  });

  test('a normally-opened database settles live, with no paused banner', async ({ page }) => {
    // Negative control. It asserts the shell actually loaded FIRST — otherwise "no banner" would also be true of a page
    // where nothing opened at all, and the two tests above would be passing for free.
    //
    // Written as "settles unpaused" rather than "never pauses": the seed session's file handle is released
    // asynchronously on Windows, so an open moments later can legitimately land paused for a beat and resume itself.
    // That is the feature working, not a defect, and a stricter assertion here would fail on the engine being correct.
    await openBundle(page);
    await expect(page.locator('body')).toContainText(/Storage|DataEngine|Inspector/i, { timeout: 20_000 });
    await expect(page.getByTestId('paused-banner')).toHaveCount(0, { timeout: 30_000 });
  });
});

test.describe('#621 — cooperative handoff', () => {
  test.beforeEach(async ({ request, page }) => {
    await closeAllSessions(request);
    releaseDatabase();
    fs.rmSync(path.join(BUNDLE, 'db.lock.request'), { force: true });
    await seedBundle(request);
    await page.addInitScript(() => {
      try {
        localStorage.clear();
      } catch {
        /* ignore */
      }
    });
  });

  test.afterEach(async ({ request }) => {
    fs.rmSync(path.join(BUNDLE, 'db.lock.request'), { force: true });
    releaseDatabase();
    await closeAllSessions(request);
  });

  test('the Workbench releases the database when an application asks for it', async ({ page }) => {
    // The scenario the whole feature exists for: the Workbench is sitting on a database and you start your app. Its
    // engine finds a lock advertising `yieldable`, publishes a claim, and waits instead of failing. Writing that claim
    // directly is exactly what the engine's claimant path does — the protocol is entirely file-mediated.
    await openBundle(page);
    await expect(page.getByTestId('paused-banner')).toHaveCount(0);

    // Our own lock must carry the advertisement, or no claimant would ever ask.
    const lock = JSON.parse(fs.readFileSync(LOCK, 'utf8'));
    expect(lock.yieldable).toBe(true);

    fs.writeFileSync(
      path.join(BUNDLE, 'db.lock.request'),
      JSON.stringify({ pid: process.pid, machineName: process.env.COMPUTERNAME ?? 'unknown', requestedAt: new Date().toISOString() }),
    );

    const banner = page.getByTestId('paused-banner');
    await expect(banner).toBeVisible({ timeout: 20_000 });
    await expect(banner).toContainText(String(process.pid));
    // Yielding means dropping the lock, not merely closing the engine — otherwise the claimant still cannot get in.
    await expect.poll(() => fs.existsSync(LOCK), { timeout: 20_000 }).toBe(false);
  });
});

test.describe('#621 — two entry modes', () => {
  test('the Connect dialog offers no standalone trace or cached-data mode', async ({ page }) => {
    await page.goto('/');
    await page.getByRole('button', { name: /^open typhon database$/i }).click();
    await expect(page.getByRole('dialog')).toBeVisible();

    // Open database + Attach are the modes; Recent / Known are shortcuts into the first.
    await expect(page.getByRole('tab', { name: /^open file$/i })).toBeVisible();
    await expect(page.getByRole('tab', { name: /^attach$/i })).toBeVisible();
    await expect(page.getByRole('tab', { name: /open trace/i })).toHaveCount(0);
    await expect(page.getByRole('tab', { name: /cached data/i })).toHaveCount(0);
  });
});
