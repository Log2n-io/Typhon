import type { TickData } from '@/libs/profiler/model/traceModel';
import { realmRunStateName } from '@/panels/SpatialMaintenance/spatialReadings';

/**
 * The live realm board (#1083 rung 6) — what every runnable realm is doing, across all archetypes.
 *
 * **A different question from `readRealmShapes`, which is per archetype.** That one answers "how is this archetype
 * partitioned in each realm"; this answers "what is each realm doing". The distinction is not cosmetic: a realm's run
 * state, divisor and cell size are properties of the **realm**, so every archetype's row for realm 7 carries the same
 * values, while clusters and cells are properties of the pair and have to be summed across archetypes to describe the
 * realm.
 */
export interface RealmBoardRow {
  realmId: number;
  /** `RealmRunState`: Dormant 0, Simulated 1, Active 2, Closing 3 — a realm fact, identical on every row of this realm. */
  runState: number;
  runStateName: string;
  /** 1 = visited every tick; N = once every N ticks. A realm fact. */
  divisor: number;
  /** This realm's cell edge. A realm fact — two realms routinely differ by orders of magnitude. */
  cellSize: number;
  cellCount: number;
  /** 1 for a flat realm: the record states dimensionality this way rather than with a flag. */
  gridDepth: number;
  /** How many archetypes reported a row for this realm on the sampled tick. */
  archetypes: number;
  /** Summed across archetypes — these are properties of the (realm, archetype) pair, not of the realm. */
  clusters: number;
  escapedClusters: number;
  promotedCells: number;
  blockedCells: number;
}

export interface RealmBoardReading {
  /** The tick these rows came from, or null when the window carried no per-realm record at all. */
  tickNumber: number | null;
  rows: RealmBoardRow[];
  /**
   * Realms the engine holds, from the same tick's archetype census.
   *
   * <b>The whole reason the census is reported.</b> A dormant realm sends no kind-67 row, so `rows.length` is the
   * RUNNABLE count and says nothing about the rest. "3 realms" over a galaxy of 1 188 would be a panel confidently
   * describing 0.25 % of the engine.
   */
  presentRealms: number;
  runnableRealms: number;
  /** Realms the engine holds that sent no row: present minus the rows shown. Never negative. */
  dormantRealms: number;
}

const EMPTY: RealmBoardReading = { tickNumber: null, rows: [], presentRealms: 0, runnableRealms: 0, dormantRealms: 0 };

/**
 * The most recent tick in `ticks` that carried per-realm rows, folded across archetypes into one row per realm.
 *
 * **One tick, not a window.** Every field here is a per-tick shape rather than a rate, so averaging across a window
 * would invent a realm state that never existed — a realm that went Dormant half way through would read as
 * "Simulated 1.5". The tick number is reported so the reader knows which instant they are looking at.
 *
 * **The realm-level fields are taken from the realm's first row**, because the engine writes the same value on every
 * archetype's row for a realm. A disagreement would be an engine defect rather than something for a panel to average;
 * `realmFieldsAgree` exists so a test can hold that contract rather than this code silently papering over it.
 */
export function readRealmBoard(ticks: readonly TickData[]): RealmBoardReading {
  for (let i = ticks.length - 1; i >= 0; i--) {
    const byRealm = ticks[i].spatialByRealm;
    if (byRealm === undefined || byRealm.size === 0) {
      continue;
    }

    const byId = new Map<number, RealmBoardRow>();
    for (const shape of byRealm.values()) {
      const existing = byId.get(shape.realmId);
      if (existing === undefined) {
        byId.set(shape.realmId, {
          realmId: shape.realmId,
          runState: shape.runState,
          runStateName: realmRunStateName(shape.runState),
          divisor: shape.divisor,
          cellSize: shape.cellSize,
          cellCount: shape.cellCount,
          gridDepth: shape.gridDepth,
          archetypes: 1,
          clusters: shape.clusters,
          escapedClusters: shape.escapedClusters,
          promotedCells: shape.promotedCells,
          blockedCells: shape.blockedCells,
        });
        continue;
      }

      existing.archetypes += 1;
      existing.clusters += shape.clusters;
      existing.escapedClusters += shape.escapedClusters;
      existing.promotedCells += shape.promotedCells;
      existing.blockedCells += shape.blockedCells;
    }

    const rows = [...byId.values()].sort((a, b) => a.realmId - b.realmId);

    // The census from the SAME tick, for the reason readRealmShapes gives: pairing this tick's rows with another tick's
    // counts produces "3 of 1 188" over four rows, which reads as an engine bug rather than a panel one.
    let presentRealms = 0;
    let runnableRealms = 0;
    const archetypeRows = ticks[i].spatialByArchetype;
    if (archetypeRows !== undefined) {
      for (const row of archetypeRows.values()) {
        presentRealms = Math.max(presentRealms, row.presentRealms ?? 0);
        runnableRealms = Math.max(runnableRealms, row.runnableRealms ?? 0);
      }
    }

    return {
      tickNumber: ticks[i].tickNumber,
      rows,
      presentRealms: Math.max(presentRealms, rows.length),
      runnableRealms: Math.max(runnableRealms, rows.length),
      dormantRealms: Math.max(0, Math.max(presentRealms, rows.length) - rows.length),
    };
  }

  return EMPTY;
}

/**
 * Whether every archetype's row for a realm agrees on that realm's own facts.
 *
 * Exists for the test that holds the engine to it, not for the panel: `readRealmBoard` takes the first row's values, so
 * a disagreement would be silently hidden. Naming the contract makes it checkable.
 */
export function realmFieldsAgree(ticks: readonly TickData[]): boolean {
  for (const tick of ticks) {
    const byRealm = tick.spatialByRealm;
    if (byRealm === undefined) {
      continue;
    }

    const seen = new Map<number, { runState: number; divisor: number; cellSize: number }>();
    for (const shape of byRealm.values()) {
      const first = seen.get(shape.realmId);
      if (first === undefined) {
        seen.set(shape.realmId, { runState: shape.runState, divisor: shape.divisor, cellSize: shape.cellSize });
        continue;
      }

      if (first.runState !== shape.runState || first.divisor !== shape.divisor || first.cellSize !== shape.cellSize) {
        return false;
      }
    }
  }

  return true;
}
