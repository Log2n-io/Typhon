import { test, expect, type APIRequestContext } from '@playwright/test';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

// #1417 — the Backups view, end to end: a closed database → Back up now (offline) → the timeline shows #1 → verify full is Ok → restore into a temp
// directory → the restored database settles and opens as a session. A local canary (/wb-dev), not gate-run: the ACs are carried by
// Typhon.Workbench.Tests (BackupsJobEndpointTests) and vitest.

const BASE = 'http://localhost:5173';
// Kestrel resolves a bare filePath against its own working directory (tools/Typhon.Workbench).
const SERVER_CWD = path.resolve('..');

async function seedBundle(request: APIRequestContext, name: string): Promise<string> {
  const bundle = path.join(SERVER_CWD, name);
  fs.rmSync(bundle, { recursive: true, force: true });
  const open = await request.post(`${BASE}/api/sessions/file`, { data: { filePath: name } });
  expect(open.ok(), 'seeding the database must succeed').toBeTruthy();
  const { sessionId } = await open.json();
  // Closed: Back up now then takes the offline route.
  await request.delete(`${BASE}/api/sessions/${sessionId}`, { headers: { 'X-Session-Token': sessionId } });
  return bundle;
}

test.describe('#1417 Backups', () => {
  test('back up, verify, restore and open the restored database', async ({ page, request }) => {
    const bundle = await seedBundle(request, 'e2e-backups.typhon');
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'typhon-e2e-backups-'));
    const store = path.join(root, 'store');
    const restored = path.join(root, 'restored');

    await page.goto('/');
    await page.getByRole('menuitem', { name: 'File' }).click();
    await page.getByRole('menuitem', { name: 'Backups…' }).click();
    await expect(page.getByTestId('backups-standalone')).toBeVisible();

    await page.getByTestId('backups-bundle').fill(bundle);
    await page.getByTestId('backups-store').fill(store);
    await page.getByTestId('backups-store').press('Enter');

    await page.getByTestId('backups-backup-now').click();
    await expect(page.getByTestId('backups-create-receipt')).toContainText('#1', { timeout: 30_000 });
    await expect(page.getByTestId('backups-point-1')).toBeVisible();

    await page.getByTestId('backups-verify-level-full').click();
    await page.getByTestId('backups-verify').click();
    await expect(page.getByTestId('backups-verify-report')).toContainText('Ok', { timeout: 30_000 });

    await page.getByTestId('backups-restore').click();
    await page.getByTestId('backups-restore-point-1').click();
    await page.getByTestId('backups-restore-target').fill(restored);
    await page.getByTestId('backups-restore-preview').click();
    await expect(page.getByTestId('backups-restore-lost')).toBeVisible();
    await page.getByTestId('backups-restore-run').click();

    await expect(page.getByTestId('backups-restore-receipt')).toBeVisible({ timeout: 60_000 });
    await page.getByTestId('backups-open-restored').click();
    await expect(page.getByTestId('backups-standalone')).toBeHidden({ timeout: 30_000 });

    fs.rmSync(root, { recursive: true, force: true });
  });
});
