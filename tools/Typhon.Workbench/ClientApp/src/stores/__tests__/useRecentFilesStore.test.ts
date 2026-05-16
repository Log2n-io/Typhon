import { beforeEach, describe, expect, it } from 'vitest';
import {
  dirOf,
  getRecentLocations,
  useRecentFilesStore,
  type RecentFile,
} from '../useRecentFilesStore';

const makeEntry = (overrides: Partial<RecentFile> = {}): RecentFile => ({
  filePath: 'C:/games/demo.typhon',
  schemaDllPaths: ['C:/games/Game.schema.dll'],
  lastOpenedAt: '2026-04-21T12:00:00Z',
  lastState: 'Ready',
  ...overrides,
});

beforeEach(() => {
  useRecentFilesStore.setState({ entries: [] });
});

describe('useRecentFilesStore', () => {
  it('starts empty', () => {
    expect(useRecentFilesStore.getState().entries).toEqual([]);
  });

  it('record prepends new entries', () => {
    useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon' }));
    useRecentFilesStore.getState().record(makeEntry({ filePath: 'b.typhon' }));
    const entries = useRecentFilesStore.getState().entries;
    expect(entries.map((e) => e.filePath)).toEqual(['b.typhon', 'a.typhon']);
  });

  it('record dedupes case-insensitively and moves to front', () => {
    useRecentFilesStore.getState().record(makeEntry({ filePath: 'C:/DB/demo.typhon' }));
    useRecentFilesStore.getState().record(makeEntry({ filePath: 'x.typhon' }));
    useRecentFilesStore
      .getState()
      .record(makeEntry({ filePath: 'c:/db/demo.typhon', lastState: 'Incompatible' }));
    const entries = useRecentFilesStore.getState().entries;
    expect(entries).toHaveLength(2);
    expect(entries[0].filePath).toBe('c:/db/demo.typhon');
    expect(entries[0].lastState).toBe('Incompatible');
  });

  it('record caps at 20 entries', () => {
    for (let i = 0; i < 25; i++) {
      useRecentFilesStore.getState().record(makeEntry({ filePath: `f${i}.typhon` }));
    }
    const entries = useRecentFilesStore.getState().entries;
    expect(entries).toHaveLength(20);
    expect(entries[0].filePath).toBe('f24.typhon');
  });

  it('remove deletes by filePath (case-insensitive)', () => {
    useRecentFilesStore.getState().record(makeEntry({ filePath: 'A.typhon' }));
    useRecentFilesStore.getState().record(makeEntry({ filePath: 'B.typhon' }));
    useRecentFilesStore.getState().remove('a.typhon');
    const entries = useRecentFilesStore.getState().entries;
    expect(entries.map((e) => e.filePath)).toEqual(['B.typhon']);
  });

  it('clear empties the store', () => {
    useRecentFilesStore.getState().record(makeEntry());
    useRecentFilesStore.getState().clear();
    expect(useRecentFilesStore.getState().entries).toEqual([]);
  });

  describe('removeUnderDirectory', () => {
    it('removes every entry under a directory and leaves others intact', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'C:/db/a.typhon' }));
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'C:/db/b.typhon' }));
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'C:/other/c.typhon' }));
      useRecentFilesStore.getState().removeUnderDirectory('C:/db');
      expect(useRecentFilesStore.getState().entries.map((e) => e.filePath)).toEqual(['C:/other/c.typhon']);
    });

    it('matches the directory case-insensitively', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'C:/DB/a.typhon' }));
      useRecentFilesStore.getState().removeUnderDirectory('c:/db');
      expect(useRecentFilesStore.getState().entries).toEqual([]);
    });
  });

  describe('pins', () => {
    it('pinResource adds an id and getPins reads it back', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon' }));
      useRecentFilesStore.getState().pinResource('a.typhon', 'Storage/Cache');
      expect(useRecentFilesStore.getState().getPins('a.typhon')).toEqual(['Storage/Cache']);
    });

    it('pinResource is case-insensitive on filePath', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'A.typhon' }));
      useRecentFilesStore.getState().pinResource('a.typhon', 'node/1');
      expect(useRecentFilesStore.getState().getPins('A.typhon')).toEqual(['node/1']);
    });

    it('pinResource is idempotent', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon' }));
      useRecentFilesStore.getState().pinResource('a.typhon', 'r1');
      useRecentFilesStore.getState().pinResource('a.typhon', 'r1');
      expect(useRecentFilesStore.getState().getPins('a.typhon')).toEqual(['r1']);
    });

    it('unpinResource removes the id', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon' }));
      useRecentFilesStore.getState().pinResource('a.typhon', 'r1');
      useRecentFilesStore.getState().pinResource('a.typhon', 'r2');
      useRecentFilesStore.getState().unpinResource('a.typhon', 'r1');
      expect(useRecentFilesStore.getState().getPins('a.typhon')).toEqual(['r2']);
    });

    it('pins are per-file', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon' }));
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'b.typhon' }));
      useRecentFilesStore.getState().pinResource('a.typhon', 'rA');
      useRecentFilesStore.getState().pinResource('b.typhon', 'rB');
      expect(useRecentFilesStore.getState().getPins('a.typhon')).toEqual(['rA']);
      expect(useRecentFilesStore.getState().getPins('b.typhon')).toEqual(['rB']);
    });

    it('record preserves existing pins on re-record of same file', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon' }));
      useRecentFilesStore.getState().pinResource('a.typhon', 'pinned-id');
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon', lastState: 'MigrationRequired' }));
      expect(useRecentFilesStore.getState().getPins('a.typhon')).toEqual(['pinned-id']);
    });
  });

  describe('lastViewport', () => {
    const vp = { fingerprint: 'fp-1', startUs: 100, endUs: 250 };

    it('setLastViewport records a viewport and getLastViewport reads it back', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon' }));
      useRecentFilesStore.getState().setLastViewport('a.typhon', vp);
      expect(useRecentFilesStore.getState().getLastViewport('a.typhon')).toEqual(vp);
    });

    it('getLastViewport returns null when none was recorded', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon' }));
      expect(useRecentFilesStore.getState().getLastViewport('a.typhon')).toBeNull();
    });

    it('setLastViewport / getLastViewport are case-insensitive on filePath', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'A.typhon' }));
      useRecentFilesStore.getState().setLastViewport('a.typhon', vp);
      expect(useRecentFilesStore.getState().getLastViewport('A.typhon')).toEqual(vp);
    });

    it('setLastViewport on an unknown file is a no-op (no entry created)', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon' }));
      useRecentFilesStore.getState().setLastViewport('ghost.typhon', vp);
      expect(useRecentFilesStore.getState().getLastViewport('ghost.typhon')).toBeNull();
      expect(useRecentFilesStore.getState().entries).toHaveLength(1);
    });

    it('viewports are per-file', () => {
      const vpB = { fingerprint: 'fp-2', startUs: 5, endUs: 9 };
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon' }));
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'b.typhon' }));
      useRecentFilesStore.getState().setLastViewport('a.typhon', vp);
      useRecentFilesStore.getState().setLastViewport('b.typhon', vpB);
      expect(useRecentFilesStore.getState().getLastViewport('a.typhon')).toEqual(vp);
      expect(useRecentFilesStore.getState().getLastViewport('b.typhon')).toEqual(vpB);
    });

    it('setLastViewport overwrites a previously saved viewport', () => {
      const vp2 = { fingerprint: 'fp-9', startUs: 1, endUs: 2 };
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon' }));
      useRecentFilesStore.getState().setLastViewport('a.typhon', vp);
      useRecentFilesStore.getState().setLastViewport('a.typhon', vp2);
      expect(useRecentFilesStore.getState().getLastViewport('a.typhon')).toEqual(vp2);
    });

    it('record preserves an existing viewport on re-record of the same file', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon' }));
      useRecentFilesStore.getState().setLastViewport('a.typhon', vp);
      // Reopening the file calls record() again with a fresh entry carrying no viewport — the
      // remembered viewport must survive so the restore effect can read it.
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon', lastState: 'MigrationRequired' }));
      expect(useRecentFilesStore.getState().getLastViewport('a.typhon')).toEqual(vp);
    });
  });
});

describe('dirOf', () => {
  it('splits a Windows path on the last backslash', () => {
    expect(dirOf('C:\\games\\db\\demo.typhon')).toBe('C:\\games\\db');
  });

  it('splits a POSIX path on the last slash', () => {
    expect(dirOf('C:/games/db/demo.typhon')).toBe('C:/games/db');
  });

  it('keeps the separator for a bare drive root', () => {
    expect(dirOf('C:\\demo.typhon')).toBe('C:\\');
    expect(dirOf('C:/demo.typhon')).toBe('C:/');
  });

  it('returns the input when there is no directory part', () => {
    expect(dirOf('demo.typhon')).toBe('demo.typhon');
  });
});

describe('getRecentLocations', () => {
  const entry = (filePath: string, kind?: RecentFile['kind'], lastOpenedAt = '2026-04-21T12:00:00Z'): RecentFile => ({
    filePath,
    schemaDllPaths: [],
    lastOpenedAt,
    lastState: 'Ready',
    kind,
  });

  it('dedupes files in the same directory to one location, most-recent-first', () => {
    const locations = getRecentLocations([
      entry('C:/db/b.typhon'),
      entry('C:/db/a.typhon'),
      entry('C:/traces/t.typhon-trace', 'trace'),
    ]);
    expect(locations.map((l) => l.dir)).toEqual(['C:/db', 'C:/traces']);
  });

  it('dedupes case-insensitively', () => {
    const locations = getRecentLocations([entry('C:/DB/a.typhon'), entry('c:/db/b.typhon')]);
    expect(locations).toHaveLength(1);
  });

  it('filters by kind, treating legacy (no-kind) entries as db', () => {
    const all = [
      entry('C:/db/a.typhon', 'db'),
      entry('C:/legacy/old.typhon'),
      entry('C:/traces/t.typhon-trace', 'trace'),
    ];
    expect(getRecentLocations(all, 'db').map((l) => l.dir)).toEqual(['C:/db', 'C:/legacy']);
    expect(getRecentLocations(all, 'trace').map((l) => l.dir)).toEqual(['C:/traces']);
  });

  it('carries the kind and lastOpenedAt of the first file seen for the directory', () => {
    const locations = getRecentLocations([entry('C:/traces/t.typhon-trace', 'trace', '2026-05-01T00:00:00Z')]);
    expect(locations[0]).toEqual({ dir: 'C:/traces', kind: 'trace', lastOpenedAt: '2026-05-01T00:00:00Z' });
  });
});
