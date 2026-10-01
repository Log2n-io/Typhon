import type { SpatialRealmRates, TickData } from '@/libs/profiler/model/traceModel';

/**
 * What each realm's partition DID over a window — the rate twin of `readRealmBoard`'s per-tick shape (kind 70).
 *
 * **Two clocks, deliberately, and the panel must label them.** `readRealmBoard` answers from ONE tick, because a
 * realm's run state and cell count are facts about that instant and averaging them would invent a state that never
 * existed. These are RATES: every counter resets each tick, so one tick's value is a sample and not a rate at all, and
 * the only honest reading is differentiated across a window. SO-01's first clause is that this surface carries both
 * kinds; showing them side by side is the documented shape, not a compromise — but an unlabelled table that mixes them
 * is how a reader comes to believe a 60-second average describes the tick they are looking at.
 */
export interface RealmRateRow {
  realmId: number;

  // ── the headline rates, per second of WALL CLOCK over the window ──────────────────────────────────────────────────

  /**
   * Entities this realm migrated between cells, per second.
   *
   * `undefined` when the window has no duration to divide by — a reading that does not exist, which is distinct from a
   * measured zero and renders as the same em dash an untouched realm gets.
   */
  migrationsPerSec: number | undefined;
  /** Entities this realm's drift scan found outside their cluster's target region, per second. See {@link migrationsPerSec}. */
  driftersPerSec: number | undefined;
  /** Repair units admitted in this realm, per second. See {@link migrationsPerSec}. */
  repairUnitsPerSec: number | undefined;
  /** Budget this realm's admitted relocations were charged, in milliseconds per second. See {@link migrationsPerSec}. */
  budgetMsPerSec: number | undefined;

  // ── coverage, which is what keeps the rates above honest ──────────────────────────────────────────────────────────

  /**
   * Ticks in the window on which this realm reported at all.
   *
   * **Load-bearing, not a diagnostic.** The rates above divide by the window's WALL CLOCK, so a realm busy for one
   * second of a sixty-second window reads at a sixtieth of what it does while working. That is the right answer to
   * "what does this realm cost me", which is the question the board exists to answer — but it is the wrong answer to
   * "how expensive is this realm when it runs", and the two differ by the duty cycle. Showing coverage beside the rate
   * is what stops a reader taking one for the other.
   */
  ticksReporting: number;
  /** Ticks in the window, reporting or not — the denominator `ticksReporting` is read against. */
  windowTicks: number;

  // ── totals over the window, for a drill-down that wants counts rather than rates ───────────────────────────────────

  migrations: number;
  drifters: number;
  repairUnits: number;
  clustersScanned: number;
  /** A MAXIMUM across the window and across realms, never a sum — see the folding note on the reading. */
  largestArrivalRun: number;
}

export interface RealmRatesReading {
  rows: RealmRateRow[];
  /** The window the rates were computed over, in milliseconds. Zero when the window held no ticks. */
  windowMs: number;
  /** Ticks in the window. */
  windowTicks: number;
  /**
   * True when the emitter's row cap truncated any tick in the window — i.e. some realm that DID work sent no row.
   *
   * **A panel showing these rows must say so when this is true**, because the rows are then not the complete set of
   * working realms and the missing ones are systematically the same realms every tick (the emitter truncates a prefix
   * of present order, not a rotating sample). Silence here is the difference between "these are the realms that
   * worked" and "these are some of them".
   */
  truncated: boolean;
  /** The largest `ratesRealmsTouched` seen in the window — how many realms worked on the busiest tick. */
  maxRealmsTouched: number;
}

const EMPTY: RealmRatesReading = { rows: [], windowMs: 0, windowTicks: 0, truncated: false, maxRealmsTouched: 0 };

interface Acc {
  realmId: number;
  migrations: number;
  drifters: number;
  repairUnits: number;
  clustersScanned: number;
  spendNs: number;
  largestArrivalRun: number;
  ticksReporting: number;
}

/**
 * Fold one realm's row into its accumulator.
 *
 * **`largestArrivalRun` takes a maximum and everything else sums**, which is SO-01's "fold by KIND, not uniformly"
 * applied to the time axis as well as the realm axis: adding a realm's peak arrival run across sixty ticks would report
 * a burst sixty times larger than any cell ever received.
 */
function fold(acc: Acc, r: SpatialRealmRates): void {
  acc.migrations += r.migrationCount;
  acc.drifters += r.driftersDetected;
  acc.repairUnits += r.repairUnitCount;
  acc.clustersScanned += r.clustersScanned;
  acc.spendNs += r.relocationSpendNs;
  acc.largestArrivalRun = Math.max(acc.largestArrivalRun, r.largestArrivalRun);
  acc.ticksReporting += 1;
}

/**
 * Per-realm rates over `ticks`, summed across archetypes — or over one archetype when `archetypeId` is given.
 *
 * **A realm that did no work in the window is ABSENT, not a zero row.** The emitter sends nothing for a realm it did
 * not touch, so its absence carries information — "measured, did nothing" is a kind-70 row of zeros, while "not
 * measured" is no row. Materialising absent realms as zeros here would erase that distinction before the panel could
 * render it, which is exactly what SO-01's "zero means zero, never unknown" forbids. The catalog, not this reading, is
 * what a panel joins against if it wants a row per known realm.
 */
export function readRealmRates(ticks: readonly TickData[], archetypeId?: number): RealmRatesReading {
  if (ticks.length === 0) {
    return EMPTY;
  }

  const byRealm = new Map<number, Acc>();
  let truncated = false;
  let maxRealmsTouched = 0;

  for (const tick of ticks) {
    // Truncation is a property of the TICK, read off the archetype census, and it has to be checked on every tick in
    // the window rather than on the last: one truncated tick anywhere means these rows are an incomplete set.
    if (tick.spatialByArchetype) {
      for (const row of tick.spatialByArchetype.values()) {
        maxRealmsTouched = Math.max(maxRealmsTouched, row.ratesRealmsTouched);
        if (row.ratesRealmsEmitted < row.ratesRealmsTouched) {
          truncated = true;
        }
      }
    }

    if (!tick.spatialRatesByRealm) {
      continue;
    }

    // A realm reporting for three archetypes on one tick counts as ONE reporting tick, not three: coverage is about
    // how much of the window this realm was busy for, and the archetype count would make a busy realm look like it
    // reported more often than the window has ticks.
    const seenThisTick = new Set<number>();
    for (const r of tick.spatialRatesByRealm.values()) {
      // The Realms board wants every archetype's work summed per realm ("what is this world costing me"); the Spatial
      // panel is archetype-first and wants one archetype's ("what is THIS archetype doing in that world"). Same fold,
      // one filter, rather than two readings that could drift apart.
      if (archetypeId !== undefined && r.archetypeId !== archetypeId) {
        continue;
      }

      let acc = byRealm.get(r.realmId);
      if (acc === undefined) {
        acc = {
          realmId: r.realmId, migrations: 0, drifters: 0, repairUnits: 0, clustersScanned: 0,
          spendNs: 0, largestArrivalRun: 0, ticksReporting: 0,
        };
        byRealm.set(r.realmId, acc);
      }

      fold(acc, r);
      if (seenThisTick.has(r.realmId)) {
        acc.ticksReporting -= 1;   // fold() counted it; this realm already reported on this tick
      } else {
        seenThisTick.add(r.realmId);
      }
    }
  }

  const first = ticks[0];
  const last = ticks[ticks.length - 1];
  const windowMs = Math.max(0, (last.endUs - first.startUs) / 1000);
  // A window of zero duration has no rate — not a rate of zero. Dividing would give Infinity, and substituting 0 would
  // claim a measured idle by this reading's own rule that zero means a measured zero. `undefined` is the third answer,
  // and it renders as the same em dash a realm that did no work gets: "no reading", which is what this is.
  const perSec = windowMs > 0 ? 1000 / windowMs : undefined;

  const rows: RealmRateRow[] = [];
  for (const acc of byRealm.values()) {
    rows.push({
      realmId: acc.realmId,
      migrationsPerSec: perSec === undefined ? undefined : acc.migrations * perSec,
      driftersPerSec: perSec === undefined ? undefined : acc.drifters * perSec,
      repairUnitsPerSec: perSec === undefined ? undefined : acc.repairUnits * perSec,
      budgetMsPerSec: perSec === undefined ? undefined : (acc.spendNs / 1_000_000) * perSec,
      ticksReporting: acc.ticksReporting,
      windowTicks: ticks.length,
      migrations: acc.migrations,
      drifters: acc.drifters,
      repairUnits: acc.repairUnits,
      clustersScanned: acc.clustersScanned,
      largestArrivalRun: acc.largestArrivalRun,
    });
  }

  rows.sort((a, b) => a.realmId - b.realmId);
  return { rows, windowMs, windowTicks: ticks.length, truncated, maxRealmsTouched };
}
