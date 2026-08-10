import { test, expect, type APIRequestContext } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

// #729 — the Database Integrity view, driven end to end against a *deliberately damaged* database.
//
// The damage is the point. A canary over a healthy fixture can only ever assert the green path, which is the
// one outcome whose regression matters least: the verdict banner's non-green states, the findings table, the
// repair plan, the consent gate and the receipt are all unreachable without real damage. So each spec seeds a
// database, corrupts it through the DEBUG-only fixture endpoint, and drives the UI over the result.

const BASE = 'http://localhost:5173';
// Kestrel resolves a bare filePath against its own working directory (tools/Typhon.Workbench), not DemoData.
const SERVER_CWD = path.resolve('..');

function bundlePath(name: string): string {
  return path.join(SERVER_CWD, name);
}

/**
 * Creates a fresh, initialised, closed `.typhon` bundle and returns its absolute path.
 *
 * Deletes any previous one first, which is not mere tidiness: these specs deliberately leave damaged
 * databases behind, and the `unopenable` variant leaves one that by construction *cannot* be opened. Reusing
 * it would fail the seed itself on the second run — the spec would pass once and then fail forever, which is
 * worse than failing outright because it looks like a regression in the feature rather than in the fixture.
 */
async function seedBundle(request: APIRequestContext, name: string): Promise<string> {
  const bundle = bundlePath(name);
  // Leftover pre-repair backups accumulate one directory per run otherwise.
  for (const entry of fs.existsSync(SERVER_CWD) ? fs.readdirSync(SERVER_CWD) : []) {
    if (entry === name || entry.startsWith(`${name}.pre-repair-`)) {
      fs.rmSync(path.join(SERVER_CWD, entry), { recursive: true, force: true });
    }
  }

  const open = await request.post(`${BASE}/api/sessions/file`, { data: { filePath: name } });
  expect(open.ok(), 'seeding the fixture database must succeed').toBeTruthy();
  const { sessionId } = await open.json();
  // Closed immediately: repair needs exclusive access, and a scan of a live database can only ever report
  // `Suspected` confidence.
  await request.delete(`${BASE}/api/sessions/${sessionId}`, { headers: { 'X-Session-Token': sessionId } });
  return bundle;
}

async function damage(request: APIRequestContext, bundle: string, variant: string): Promise<string> {
  const res = await request.post(`${BASE}/api/fixtures/damaged`, { data: { path: bundle, variant } });
  expect(res.ok(), `damaging the fixture (${variant}) must succeed`).toBeTruthy();
  const body = await res.json();
  return body.verdict as string;
}

function dataHash(bundle: string): string {
  return crypto.createHash('sha256').update(fs.readFileSync(path.join(bundle, 'data'))).digest('hex');
}

/** Opens the view from the Welcome screen — the no-session entry, and the one that matters most. */
async function openIntegrityFromWelcome(page: import('@playwright/test').Page, bundle: string) {
  await page.goto('/');
  await page.getByTestId('welcome-check-integrity').click();
  await expect(page.getByTestId('integrity-standalone')).toBeVisible();
  await page.getByTestId('integrity-path').fill(bundle);
}

test.describe('#729 Database Integrity', () => {
  test('reachable with no session, and reports damage on a database that is not open', async ({ page, request }) => {
    const bundle = await seedBundle(request, 'e2e-integrity-damage.typhon');
    expect(await damage(request, bundle, 'meta-slot')).toBe('Divergent');

    await openIntegrityFromWelcome(page, bundle);
    await page.getByTestId('integrity-scan').click();

    await expect(page.getByTestId('integrity-verdict-badge')).toContainText('Divergent');
    // The findings table must actually have rows — a verdict with an empty table would mean the report
    // rendered its headline and dropped its evidence.
    await expect(page.getByTestId('integrity-finding-row')).not.toHaveCount(0);
    await expect(page.getByTestId('integrity-totals')).toBeVisible();
  });

  test('the Limits block renders on a GREEN report and cannot be collapsed', async ({ page, request }) => {
    // The single most suppressible piece of the report, and the one the design forbids suppressing: a scan
    // verifies internal consistency only, and the moment that gap is most likely to mislead is exactly when
    // everything passed and the reader stops reading.
    const bundle = await seedBundle(request, 'e2e-integrity-green.typhon');

    await openIntegrityFromWelcome(page, bundle);
    await page.getByTestId('integrity-scan').click();

    await expect(page.getByTestId('integrity-verdict-badge')).toContainText('Sound');
    const limits = page.getByTestId('integrity-limits');
    await expect(limits).toBeVisible();
    await expect(limits).toContainText(/limits of this scan/i);
    // No disclosure control of any kind inside the block — no <details>, no toggle button.
    await expect(limits.locator('details, button, [role="button"]')).toHaveCount(0);
  });

  test('plan → dry run → apply → verified green, with the dry run mutating nothing', async ({ page, request }) => {
    const bundle = await seedBundle(request, 'e2e-integrity-repair.typhon');
    await damage(request, bundle, 'meta-slot');

    await openIntegrityFromWelcome(page, bundle);
    await page.getByTestId('integrity-scan').click();
    await expect(page.getByTestId('integrity-verdict-badge')).toContainText('Divergent');

    // Plan — read-only, and it must produce steps or there is nothing to review.
    await page.getByTestId('integrity-plan').click();
    await expect(page.getByTestId('integrity-repair')).toBeVisible();
    await expect(page.getByTestId('integrity-step-row')).not.toHaveCount(0);

    // Dry run — the file must come back byte-identical. This is the assertion that makes "rehearsal" mean
    // something; without it "dry run" is a label on a button.
    const before = dataHash(bundle);
    await page.getByTestId('integrity-dry-run').click();
    await expect(page.getByTestId('integrity-rehearsal')).toBeVisible();
    expect(dataHash(bundle), 'a dry run must not write a single byte').toBe(before);

    // Apply — and the receipt must carry the post-repair verification, not just a success flag.
    await page.getByTestId('integrity-apply').click();
    await expect(page.getByTestId('integrity-receipt')).toBeVisible();
    await expect(page.getByTestId('integrity-result-row')).not.toHaveCount(0);
    await expect(page.getByTestId('integrity-verification')).toContainText('Sound');
    expect(dataHash(bundle), 'a real repair must write').not.toBe(before);
  });

  test('an unopenable database still produces a report', async ({ page, request }) => {
    // The case that most justifies the whole feature: no engine can open this, so no session-scoped view
    // could ever have shown it.
    const bundle = await seedBundle(request, 'e2e-integrity-unopenable.typhon');
    expect(await damage(request, bundle, 'unopenable')).toBe('Unopenable');

    await openIntegrityFromWelcome(page, bundle);
    await page.getByTestId('integrity-scan').click();

    await expect(page.getByTestId('integrity-verdict-badge')).toContainText('Unopenable');
    await expect(page.getByTestId('integrity-limits')).toBeVisible();
  });

  test('a stale plan is refused with the server’s own words, not a status code', async ({ page, request }) => {
    const bundle = await seedBundle(request, 'e2e-integrity-stale.typhon');
    await damage(request, bundle, 'meta-slot');

    await openIntegrityFromWelcome(page, bundle);
    await page.getByTestId('integrity-scan').click();
    await page.getByTestId('integrity-plan').click();
    await expect(page.getByTestId('integrity-repair')).toBeVisible();

    // Move the database underneath the reviewed plan, then apply it.
    await damage(request, bundle, 'checksum');
    await page.getByTestId('integrity-apply').click();

    // The point is the sentence, not the 409: the server explains what to do about it, and that explanation
    // must survive the client's error handling.
    await expect(page.getByTestId('integrity-apply-error')).toContainText(/database has changed since the plan was reviewed/i);
  });
});
