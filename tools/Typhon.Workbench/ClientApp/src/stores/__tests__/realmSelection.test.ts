import { beforeEach, describe, expect, it } from 'vitest';
import { useSelectionStore } from '../useSelectionStore';
import { useNavHistoryStore } from '../useNavHistoryStore';
import { buildSelectionSearch, parseSelectionFromSearch } from '../selectionUrlSync';
import { installNavHistorySync } from '../navHistorySync';
import { buildObjectHits } from '@/shell/commands/objectHits';
import { realmAxis, realmExtent, realmLabel, type Realm } from '@/hooks/realms/types';

/**
 * #1083 rung 2 — `realm` as a first-class object of the IA's object model.
 *
 * The information-architecture doc's own claim is that the app is defined over the object-type table rather than over
 * panels: palette search, nav history and deep links all key on it. So the test of "is a realm really an object" is not
 * that a panel renders one — it is that the *spine* carries it. Each case below is one of the four things § 2.1's table
 * is supposed to buy.
 */

function realm(id: number, kind = ''): Realm {
  return {
    id,
    generation: 0,
    source: 'catalog',
    registered: true,
    lifecycle: 'live',
    grid: { minX: 0, minY: 0, minZ: 0, maxX: 64, maxY: 64, maxZ: 64, cellSize: 64, migrationHysteresisRatio: 0.05, deep: false },
    runState: '',
    divisor: null,
    sessions: null,
    kind,
  };
}

beforeEach(() => {
  useSelectionStore.getState().clear();
  useNavHistoryStore.getState().clear();
});

describe('realm on the selection bus', () => {
  it('becomes the Inspector leaf, with the realm id as its ref', () => {
    useSelectionStore.getState().select('realm', 7);

    const leaf = useSelectionStore.getState().leaf;
    expect(leaf?.type).toBe('realm');
    expect(leaf?.ref).toBe(7);
  });

  it('re-selecting the same realm is a no-op, so it does not re-notify', () => {
    useSelectionStore.getState().select('realm', 7);
    const first = useSelectionStore.getState().leaf;

    useSelectionStore.getState().select('realm', 7);

    expect(useSelectionStore.getState().leaf).toBe(first);
  });
});

describe('realm through a deep link', () => {
  /**
   * `?leaf=` is admitted by a whitelist of types whose ref is a primitive, which is the thing rung 2 had to extend: a
   * realm's ref is its id, so it qualifies where a rich-ref leaf like an entity does not.
   */
  it('is accepted from ?leaf=realm:<id>', () => {
    expect(parseSelectionFromSearch('?leaf=realm:7').leaf).toEqual({ type: 'realm', ref: '7' });
  });

  it('is written through to the query string unchanged', () => {
    const search = buildSelectionSearch(new URLSearchParams('session=abc'), {
      viewRange: null,
      system: null,
      component: null,
      queue: null,
      resource: null,
      entity: null,
      leaf: 'realm:7',
    });

    expect(search.get('leaf')).toBe('realm:7');
    expect(search.get('session')).toBe('abc');
  });

  /** The whitelist is what admits a realm, so a type outside it must still be refused. */
  it('does not admit a leaf type that is not URL-linkable', () => {
    expect(parseSelectionFromSearch('?leaf=entity:e-42').leaf).toBeNull();
  });
});

describe('realm through Back/Forward', () => {
  /**
   * The assertion AC-3 is really about. Nav history restores a `bus-leaf` entry by re-driving the bus, and types with no
   * dedicated source store go through its `default` branch — so this passes only if a realm selection is pushed as a
   * leaf in the first place and the restore path accepts an object type it was not written for.
   */
  it('a realm selection survives a Back and a Forward', () => {
    // The bridge is what records a selection; installing it is what the shell does at mount. Without it this test
    // passes vacuously against a bus that never told anyone, which is exactly how the missing entry hid.
    const uninstall = installNavHistorySync();
    try {
      useSelectionStore.getState().select('realm', 3);
      useSelectionStore.getState().select('realm', 9);

      useNavHistoryStore.getState().back();
      expect(useSelectionStore.getState().leaf?.ref).toBe(3);

      useNavHistoryStore.getState().forward();
      expect(useSelectionStore.getState().leaf?.ref).toBe(9);
    } finally {
      uninstall();
    }
  });
});

describe('realm in the palette', () => {
  it('is offered as its own group, above components and archetypes', () => {
    const hits = buildObjectHits(
      '',
      {
        realms: [realm(0), realm(7, 'interior')].map((r) => ({ id: r.id, label: realmLabel(r), sublabel: realmExtent(r.grid) })),
        components: [{ typeName: 'Position' }],
      },
      'open',
    );

    const groups = hits.map((h) => h.group);
    expect(groups).toContain('Realms');
    expect(groups.indexOf('Realms')).toBeLessThan(groups.indexOf('Components'));
  });

  it('is found by its bare id, because that is what a log or a wire frame gives you', () => {
    const hits = buildObjectHits(
      '7',
      { realms: [realm(0), realm(7, 'interior')].map((r) => ({ id: r.id, label: realmLabel(r), sublabel: realmExtent(r.grid) })) },
      'open',
    );

    expect(hits.map((h) => h.ref)).toEqual([7]);
  });

  it('selects by realm id, so the hit drives the bus directly', () => {
    const [hit] = buildObjectHits(
      'interior',
      { realms: [realm(7, 'interior')].map((r) => ({ id: r.id, label: realmLabel(r), sublabel: realmExtent(r.grid) })) },
      'open',
    );

    expect(hit.type).toBe('realm');
    expect(hit.ref).toBe(7);
  });
});

describe('realm labels', () => {
  it('names realm 0 the primary world rather than just a number', () => {
    expect(realmLabel(realm(0))).toContain('primary');
  });

  /**
   * A range per axis, not an extent. An SWG interior is `0 … 64` on every axis and is **not** centred on the origin the
   * way the planets are, so "64×64" would describe two differently-placed realms identically.
   */
  it('shows a realm’s bounds as a range per axis, because a realm is placed as well as sized', () => {
    expect(realmAxis(-8192, 8192)).toBe('-8192 … 8192');
    expect(realmAxis(0, 64)).toBe('0 … 64');
  });

  /**
   * **Every axis is named.** The one-line form used to read `0,0 … 64,64, cell 64`: two of the three axes, unlabelled,
   * with the corners interleaved so which number belonged to which axis was a guess. Reported from a live session.
   */
  it('names each axis, and does not drop Z', () => {
    const line = realmExtent(realm(7).grid);

    expect(line).toBe('X 0 … 64 · Y 0 … 64 · Z 0 … 64 · cell 64');
  });

  it('marks a deep realm, from the grid’s own answer rather than from its Z extent', () => {
    const deep = { ...realm(2).grid, maxZ: 1000, deep: true };
    expect(realmExtent(deep)).toContain('3D');
    // A FLAT realm is exactly one cell deep, never zero — so a non-zero Z extent alone must not read as 3D.
    expect(realmExtent(realm(2).grid)).not.toContain('3D');
  });
});
