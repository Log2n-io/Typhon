import { describe, expect, it } from 'vitest';
import type { TickData } from '@/libs/profiler/model/traceModel';
import { readRealmBoard, realmFieldsAgree } from '../realmBoardReading';

/**
 * #1083 rung 6 — the live realm board, folded across archetypes.
 *
 * The board answers "what is each realm doing"; WB-05's `readRealmShapes` answers "how is this archetype partitioned in
 * each realm". The fold is where that difference lives, so it is what these cases pin.
 */

function shape(realmId: number, archetypeId: number, over: Partial<Record<string, number>> = {}) {
  return {
    realmId,
    archetypeId,
    runState: 2,
    divisor: 1,
    cellSize: 64,
    cellCount: 1,
    gridDepth: 1,
    clusters: 1,
    clusterReach: 0,
    escapedClusters: 0,
    promotedCells: 0,
    blockedCells: 0,
    budgetConfiguredMs: 1,
    efficiencyTolerance: 0.1,
    ...over,
  };
}

function tick(n: number, shapes: ReturnType<typeof shape>[], census?: { presentRealms: number; runnableRealms: number }): TickData {
  const byRealm = new Map(shapes.map((s) => [s.realmId * 65536 + s.archetypeId, s]));
  const t = { tickNumber: n, spatialByRealm: byRealm } as unknown as TickData;
  if (census) {
    (t as { spatialByArchetype?: Map<number, unknown> }).spatialByArchetype = new Map([[1, census]]);
  }
  return t;
}

describe('readRealmBoard', () => {
  it('reports nothing when no tick carried a per-realm record', () => {
    expect(readRealmBoard([]).tickNumber).toBeNull();
    expect(readRealmBoard([{ tickNumber: 5 } as TickData]).rows).toEqual([]);
  });

  /**
   * The fold. Three archetypes reporting realm 7 is ONE realm doing things, not three realms — and its clusters are the
   * sum, because clusters belong to the (realm, archetype) pair while the run state belongs to the realm.
   */
  it('folds a realm’s archetype rows into one row, summing what is per-pair', () => {
    const board = readRealmBoard([
      tick(10, [
        shape(7, 1, { clusters: 3, escapedClusters: 1 }),
        shape(7, 2, { clusters: 4, promotedCells: 2 }),
        shape(7, 3, { clusters: 5, blockedCells: 6 }),
      ]),
    ]);

    expect(board.rows).toHaveLength(1);
    const [row] = board.rows;
    expect(row.realmId).toBe(7);
    expect(row.archetypes).toBe(3);
    expect(row.clusters).toBe(12);
    expect(row.escapedClusters).toBe(1);
    expect(row.promotedCells).toBe(2);
    expect(row.blockedCells).toBe(6);
    // Realm facts are taken once, not summed — a divisor of 3 would be nonsense.
    expect(row.divisor).toBe(1);
    expect(row.cellSize).toBe(64);
  });

  it('sorts by realm id and names the run state', () => {
    const board = readRealmBoard([tick(10, [shape(9, 1, { runState: 0 }), shape(2, 1, { runState: 1 })])]);

    expect(board.rows.map((r) => r.realmId)).toEqual([2, 9]);
    expect(board.rows.map((r) => r.runStateName)).toEqual(['Simulated', 'Dormant']);
  });

  /**
   * **The count that makes the board honest.** A dormant realm emits no row at all — that is RLM-B's claim, measured at
   * 0.20 µs per *active* realm — so `rows.length` is the runnable count and says nothing about the rest. A board that
   * showed "2 realms" over a galaxy of 1 188 would be describing 0.17 % of the engine with no hint that it was.
   */
  it('counts the realms it is not showing, from the same tick’s census', () => {
    const board = readRealmBoard([
      tick(10, [shape(0, 1), shape(7, 1)], { presentRealms: 1188, runnableRealms: 2 }),
    ]);

    expect(board.rows).toHaveLength(2);
    expect(board.presentRealms).toBe(1188);
    expect(board.dormantRealms).toBe(1186);
  });

  it('never reports a negative dormant count when the census lags the rows', () => {
    const board = readRealmBoard([tick(10, [shape(0, 1), shape(7, 1)], { presentRealms: 1, runnableRealms: 1 })]);

    expect(board.dormantRealms).toBe(0);
    expect(board.presentRealms).toBe(2);
  });

  /**
   * One tick, not a window: every field is a per-tick shape, so averaging would invent a realm state that never
   * existed — a realm that went Dormant half way through would read as "Simulated 1.5".
   */
  it('reads the most recent tick that has rows, and says which', () => {
    const board = readRealmBoard([
      tick(10, [shape(7, 1, { runState: 2 })]),
      { tickNumber: 11 } as TickData,
      tick(12, [shape(7, 1, { runState: 0 })]),
    ]);

    expect(board.tickNumber).toBe(12);
    expect(board.rows[0].runStateName).toBe('Dormant');
  });
});

describe('realmFieldsAgree', () => {
  it('holds when every archetype reports the same realm facts', () => {
    expect(realmFieldsAgree([tick(10, [shape(7, 1), shape(7, 2)])])).toBe(true);
  });

  /** The contract `readRealmBoard` relies on by taking the first row: a disagreement is an engine defect, not an average. */
  it('fails when two archetypes disagree about a realm’s own state', () => {
    expect(realmFieldsAgree([tick(10, [shape(7, 1, { runState: 2 }), shape(7, 2, { runState: 0 })])])).toBe(false);
  });
});
