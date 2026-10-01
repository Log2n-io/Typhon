import { describe, expect, it } from 'vitest';
import type { Realm } from '@/hooks/realms/types';
import type { RealmBoardReading, RealmBoardRow } from '../realmBoardReading';
import { awakeCount, mergeRealmRows } from '../realmRows';

/**
 * #1083 — a realm has two halves, and a session may hold either or both.
 *
 * The panel used to choose one body by `kind === 'attach'`, which made them exclusive. That hid the live half on the
 * two session shapes that have BOTH: a file with a capture attached, and a file whose holder is being watched — each
 * `kind === 'open'` and each carrying per-realm records.
 */

function catalogRealm(id: number): Realm {
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
    kind: '',
  };
}

function liveRow(realmId: number): RealmBoardRow {
  return {
    realmId,
    runState: 1,
    runStateName: 'Simulated',
    divisor: 1,
    cellSize: 64,
    cellCount: 1,
    gridDepth: 1,
    archetypes: 2,
    clusters: 7,
    escapedClusters: 0,
    promotedCells: 0,
    blockedCells: 0,
  };
}

function reading(rows: RealmBoardRow[], over: Partial<RealmBoardReading> = {}): RealmBoardReading {
  return { rows, tickNumber: 824, presentRealms: 100, dormantRealms: 100 - rows.length, ...over } as RealmBoardReading;
}

describe('merging the two halves of a realm', () => {
  it('a file alone gives catalog rows with no live side', () => {
    const set = mergeRealmRows([catalogRealm(0), catalogRealm(3)], null);

    expect(set.rows.map((r) => r.id)).toEqual([0, 3]);
    expect(set.rows.every((r) => r.live === null)).toBe(true);
    expect(set.tickNumber).toBeNull();
    expect(awakeCount(set)).toBe(0);
  });

  it('telemetry alone gives live rows with no catalog side', () => {
    const set = mergeRealmRows([], reading([liveRow(0), liveRow(7)]));

    expect(set.rows.map((r) => r.id)).toEqual([0, 7]);
    expect(set.rows.every((r) => r.catalog === null)).toBe(true);
    expect(set.tickNumber).toBe(824);
    expect(awakeCount(set)).toBe(2);
  });

  /** The case the kind-based branch could not express: both sources, one row, the complete realm. */
  it('both sources put identity and run state on the SAME row', () => {
    const set = mergeRealmRows([catalogRealm(0), catalogRealm(3)], reading([liveRow(0)]));

    expect(set.rows).toHaveLength(2);
    const [realm0, realm3] = set.rows;
    expect(realm0.catalog).not.toBeNull();
    expect(realm0.live).not.toBeNull();
    // Realm 3 is registered and silent — asleep, not missing. It keeps its catalog half and simply has no live one.
    expect(realm3.catalog).not.toBeNull();
    expect(realm3.live).toBeNull();
    expect(awakeCount(set)).toBe(1);
  });

  /**
   * **A union, not a join.** A realm registered at run time may have telemetry and no catalog row yet; dropping it
   * would hide a realm that demonstrably exists, since it is reporting.
   */
  it('keeps a realm that only telemetry knows about', () => {
    const set = mergeRealmRows([catalogRealm(0)], reading([liveRow(0), liveRow(42)]));

    expect(set.rows.map((r) => r.id)).toEqual([0, 42]);
    expect(set.rows[1].catalog).toBeNull();
    expect(set.rows[1].live).not.toBeNull();
  });

  /**
   * The dormant census answers "how many am I not showing". With a catalog the panel lists every realm, so the list
   * already answers it and printing the census beside a complete list would be a second, contradictory count.
   */
  it('reports the dormant census only when telemetry is the sole source', () => {
    expect(mergeRealmRows([], reading([liveRow(0)])).dormantRealms).toBe(99);
    expect(mergeRealmRows([catalogRealm(0)], reading([liveRow(0)])).dormantRealms).toBeNull();
    expect(mergeRealmRows([catalogRealm(0)], null).presentRealms).toBeNull();
  });
});
