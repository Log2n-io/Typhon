/**
 * The machine-local database registry, shaped for display (#622, design D-7).
 *
 * Pure on purpose — the interesting decisions here are about what the list is allowed to *imply*, and those are worth
 * asserting without a dialog harness. The registry is discoverability only (D-8): nothing downstream may treat a row's
 * presence, absence or staleness as a fact about correlation.
 */

/** One database this machine has opened, as returned by `GET /api/databases`. */
export interface KnownDatabase {
  name: string;
  bundlePath: string;
  databaseId: string;
  firstSeenUtc: string;
  lastOpenedUtc: string;
  lastOpenedBy: string;
  /** Whether the bundle is still on disk. Recomputed server-side on every listing — never a stored flag. */
  exists: boolean;
}

/** The registry's whole state: its rows, and whether it is even recording. */
export interface KnownDatabaseList {
  enabled: boolean;
  disabledReason: string | null;
  registryDirectory: string;
  entries: KnownDatabase[];
}

/**
 * Present rows first, missing ones after — each group keeping the server's most-recently-opened order.
 *
 * Demoting rather than hiding: a database that moved is still the row the user is looking for, and it is the only place
 * they can act on it ("forget this"). Hiding it would leave a list that silently shrinks with no way to clean it up.
 */
export function partitionByExistence(entries: KnownDatabase[]): { present: KnownDatabase[]; missing: KnownDatabase[] } {
  const present: KnownDatabase[] = [];
  const missing: KnownDatabase[] = [];
  for (const e of entries) {
    (e.exists ? present : missing).push(e);
  }
  return { present, missing };
}

/** Rows in display order: present first, then missing. */
export function orderForDisplay(entries: KnownDatabase[]): KnownDatabase[] {
  const { present, missing } = partitionByExistence(entries);
  return [...present, ...missing];
}

/** What the panel should say when it has no rows to show — or nothing, when it has. */
export type RegistryNotice =
  | { kind: 'ok' }
  | { kind: 'disabled'; reason: string }
  | { kind: 'empty' };

/**
 * Distinguishes "switched off" from "nothing recorded yet".
 *
 * This is the one piece of display logic D-7 argues for directly: *"an empty list teaches the user the feature is
 * useless and they stop looking"*. A disabled registry that renders as an empty list is therefore not a cosmetic
 * problem — it is the feature failing in exactly the silent way the design exists to avoid. The reason names the
 * switch, so the state is undoable rather than merely observed.
 *
 * Rows recorded before the registry was switched off are still shown, so `disabled` is reported even when entries
 * exist — the notice is about whether *new* opens are being recorded, not about whether the list is empty.
 */
export function describeRegistryState(list: KnownDatabaseList | null | undefined): RegistryNotice {
  if (!list) {
    return { kind: 'ok' };
  }
  if (!list.enabled) {
    return { kind: 'disabled', reason: list.disabledReason ?? 'The database registry is switched off.' };
  }
  return list.entries.length === 0 ? { kind: 'empty' } : { kind: 'ok' };
}

/** How many rows point at a bundle that is gone — the count the "Prune missing" action offers to remove. */
export function missingCount(entries: KnownDatabase[]): number {
  let n = 0;
  for (const e of entries) {
    if (!e.exists) {
      n++;
    }
  }
  return n;
}

/** Parent directory of a bundle path, for the secondary line under the name. Handles both separators. */
export function parentDirectoryOf(bundlePath: string): string {
  const idx = Math.max(bundlePath.lastIndexOf('\\'), bundlePath.lastIndexOf('/'));
  if (idx <= 0) {
    return bundlePath;
  }
  const dir = bundlePath.slice(0, idx);
  // `C:` on its own is not a listable directory — keep the separator that made it a root.
  return /^[a-zA-Z]:$/.test(dir) ? dir + bundlePath[idx] : dir;
}
