import { describe, expect, it } from 'vitest';
import {
  describeRegistryState,
  missingCount,
  orderForDisplay,
  parentDirectoryOf,
  partitionByExistence,
  type KnownDatabase,
  type KnownDatabaseList,
} from '../knownDatabases';

/**
 * #622 (F9) — the display rules for the machine-local database registry (design D-7).
 *
 * The one that matters is `describeRegistryState`: D-7 argues that a discoverability feature which renders as an empty
 * list has already failed, because the user concludes it is useless and stops looking. "Switched off" and "nothing yet"
 * therefore have to be distinguishable, and that is a property worth pinning rather than a styling choice.
 */

function db(name: string, exists: boolean, lastOpenedUtc = '2026-08-01T10:00:00Z'): KnownDatabase {
  return {
    name,
    bundlePath: `C:\\srv\\data\\${name}.typhon`,
    databaseId: '00000000-0000-0000-0000-000000000001',
    firstSeenUtc: '2026-07-01T10:00:00Z',
    lastOpenedUtc,
    lastOpenedBy: 'AntHill.Demo',
    exists,
  };
}

function list(entries: KnownDatabase[], overrides: Partial<KnownDatabaseList> = {}): KnownDatabaseList {
  return {
    enabled: true,
    disabledReason: null,
    registryDirectory: 'C:\\Users\\x\\AppData\\Local\\Typhon\\databases',
    entries,
    ...overrides,
  };
}

describe('partitionByExistence', () => {
  it('splits present from missing, keeping each group in the order it arrived', () => {
    const { present, missing } = partitionByExistence([db('a', true), db('b', false), db('c', true)]);

    expect(present.map((e) => e.name)).toEqual(['a', 'c']);
    expect(missing.map((e) => e.name)).toEqual(['b']);
  });

  it('demotes missing entries rather than hiding them', () => {
    // A database that moved is still the row the user is looking for, and the only place they can act on it.
    expect(orderForDisplay([db('gone', false), db('here', true)]).map((e) => e.name)).toEqual(['here', 'gone']);
  });
});

describe('missingCount', () => {
  it('counts only the entries whose bundle is gone', () => {
    expect(missingCount([db('a', true), db('b', false), db('c', false)])).toBe(2);
    expect(missingCount([db('a', true)])).toBe(0);
  });
});

describe('describeRegistryState', () => {
  it('distinguishes a switched-off registry from an empty one', () => {
    const off = describeRegistryState(list([], { enabled: false, disabledReason: "A 'disabled' file exists in C:\\x." }));
    const empty = describeRegistryState(list([]));

    expect(off).toEqual({ kind: 'disabled', reason: "A 'disabled' file exists in C:\\x." });
    expect(empty).toEqual({ kind: 'empty' });
  });

  it('reports disabled even when rows survive from before it was switched off', () => {
    // The notice is about whether NEW opens are recorded, not about whether the list happens to be empty. Reporting
    // "ok" here would tell the user their next database will show up, which is exactly wrong.
    const notice = describeRegistryState(list([db('a', true)], { enabled: false, disabledReason: 'off by env var' }));

    expect(notice.kind).toBe('disabled');
  });

  it('falls back to a sentence when the server gives no reason', () => {
    const notice = describeRegistryState(list([], { enabled: false, disabledReason: null }));

    expect(notice.kind).toBe('disabled');
    expect(notice.kind === 'disabled' && notice.reason.length).toBeGreaterThan(0);
  });

  it('says nothing while the list is still loading', () => {
    expect(describeRegistryState(null)).toEqual({ kind: 'ok' });
  });
});

describe('parentDirectoryOf', () => {
  it('handles both separators and a bare drive root', () => {
    expect(parentDirectoryOf('C:\\srv\\data\\world.typhon')).toBe('C:\\srv\\data');
    expect(parentDirectoryOf('/srv/data/world.typhon')).toBe('/srv/data');
    // "C:" alone is not a listable directory — the separator has to survive.
    expect(parentDirectoryOf('C:\\world.typhon')).toBe('C:\\');
  });
});
