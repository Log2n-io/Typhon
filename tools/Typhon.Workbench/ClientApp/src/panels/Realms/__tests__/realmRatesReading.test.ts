import { describe, expect, it } from 'vitest';
import type { SpatialRealmRates, SpatialTickTelemetry, TickData } from '@/libs/profiler/model/traceModel';
import { readRealmRates } from '../realmRatesReading';

/** Only the members the reading folds; the rest of the record is irrelevant to it and stating them would hide which ones matter. */
function rates(over: Partial<SpatialRealmRates> & { realmId: number }): SpatialRealmRates {
  return {
    archetypeId: 1,
    tightnessExtentSum: 0, tightnessBoundSum: 0, relocationSpendNs: 0,
    clustersScanned: 0, slotsScanned: 0, driftersDetected: 0, driftAbsorbed: 0, driftersUnplaced: 0,
    driftGatedClusters: 0, driftSuppressedByDensity: 0, driftersUnplacedNoCandidate: 0, driftersSpilled: 0,
    tightnessSamples: 0, migrationCount: 0, crossingsExecuted: 0, relocationsExecuted: 0, repairsExecuted: 0,
    jumpCrossings: 0, clampedDestinations: 0, staleFlagsDropped: 0, relocationsThrottled: 0,
    relocationsSuperseded: 0, relocationsAdmitted: 0, crossingsQueued: 0, pinsRejected: 0,
    repairedEntityCount: 0, repairUnitCount: 0, repairUnitsRefused: 0, repairValveFires: 0,
    arrivalCellsTouched: 0, largestArrivalRun: 0, cellTreePromotions: 0, cellTreeDemotions: 0,
    ...over,
  };
}

function census(over: Partial<SpatialTickTelemetry> = {}): SpatialTickTelemetry {
  return { archetypeId: 1, presentRealms: 0, runnableRealms: 0, ratesRealmsTouched: 0, ratesRealmsEmitted: 0, ...over } as SpatialTickTelemetry;
}

/** One tick, `durationMs` long, starting at `startMs`. */
function tick(startMs: number, durationMs: number, rows: SpatialRealmRates[], arch?: SpatialTickTelemetry): TickData {
  return {
    startUs: startMs * 1000,
    endUs: (startMs + durationMs) * 1000,
    spatialRatesByRealm: rows.length === 0 ? undefined : new Map(rows.map((r, i) => [r.realmId * 65536 + i, r])),
    spatialByArchetype: arch === undefined ? undefined : new Map([[1, arch]]),
  } as unknown as TickData;
}

describe('per-realm maintenance rates over a window', () => {
  it('divides by the window\'s wall clock, not by the ticks a realm was busy on', () => {
    // Realm 5 migrates 100 entities on ONE tick of a one-second window and sleeps for the rest.
    const ticks = [
      tick(0, 100, [rates({ realmId: 5, migrationCount: 100 })]),
      tick(100, 900, []),
    ];

    const reading = readRealmRates(ticks);

    expect(reading.windowMs).toBe(1000);
    const realm5 = reading.rows.find((r) => r.realmId === 5);
    // 100 migrations per SECOND OF WALL CLOCK — not 1000/s, which is its rate while working. The board's question is
    // "what does this realm cost me", and a realm busy a tenth of the time costs a tenth as much.
    expect(realm5?.migrationsPerSec).toBe(100);
    // ...and the coverage is what says the two differ, so a reader is not left to assume it worked throughout.
    expect(realm5?.ticksReporting).toBe(1);
    expect(realm5?.windowTicks).toBe(2);
  });

  it('leaves a realm that did no work ABSENT rather than materialising a zero row', () => {
    const reading = readRealmRates([tick(0, 1000, [rates({ realmId: 5, migrationCount: 1 })])]);

    expect(reading.rows.map((r) => r.realmId)).toEqual([5]);
    // The emitter sends nothing for an untouched realm, so "not measured" must not become "measured as zero" here.
    expect(reading.rows.find((r) => r.realmId === 9)).toBeUndefined();
  });

  it('maxes the largest arrival run across ticks and realms, and sums everything else', () => {
    const ticks = [
      tick(0, 500, [rates({ realmId: 5, largestArrivalRun: 9, migrationCount: 2 })]),
      tick(500, 500, [rates({ realmId: 5, largestArrivalRun: 4, migrationCount: 3 })]),
    ];

    const realm5 = readRealmRates(ticks).rows.find((r) => r.realmId === 5);

    // 9, not 13: adding two ticks' peaks reports a burst no cell ever received.
    expect(realm5?.largestArrivalRun).toBe(9);
    expect(realm5?.migrations).toBe(5);
  });

  it('counts a realm reporting for several archetypes on one tick as ONE reporting tick', () => {
    const ticks = [tick(0, 1000, [
      rates({ realmId: 5, archetypeId: 1, migrationCount: 2 }),
      rates({ realmId: 5, archetypeId: 2, migrationCount: 3 }),
    ])];

    const realm5 = readRealmRates(ticks).rows.find((r) => r.realmId === 5);

    expect(realm5?.migrations).toBe(5);       // summed across archetypes
    expect(realm5?.ticksReporting).toBe(1);   // but it was busy on one tick, not two
  });

  it('reports truncation from ANY tick in the window, not just the last', () => {
    const ticks = [
      tick(0, 500, [rates({ realmId: 5 })], census({ ratesRealmsTouched: 300, ratesRealmsEmitted: 64 })),
      tick(500, 500, [rates({ realmId: 5 })], census({ ratesRealmsTouched: 2, ratesRealmsEmitted: 2 })),
    ];

    const reading = readRealmRates(ticks);

    // One truncated tick makes the whole window's row set incomplete; reading only the last tick would say otherwise.
    expect(reading.truncated).toBe(true);
    expect(reading.maxRealmsTouched).toBe(300);
  });

  it('does not claim truncation when every tick emitted everything it touched', () => {
    const reading = readRealmRates([
      tick(0, 1000, [rates({ realmId: 5 })], census({ ratesRealmsTouched: 1, ratesRealmsEmitted: 1 })),
    ]);

    expect(reading.truncated).toBe(false);
  });

  it('reports NO reading for a degenerate window — not Infinity, and not a measured zero either', () => {
    const reading = readRealmRates([tick(0, 0, [rates({ realmId: 5, migrationCount: 7 })])]);

    // Three candidate answers, two of them wrong. Infinity renders as a number and is nonsense; 0 claims a measured
    // idle, which this reading's own rule reserves for a realm that was measured and did nothing — and this realm
    // demonstrably did seven migrations. `undefined` is "no rate exists over a window of no duration", and it renders
    // as the same em dash an untouched realm gets.
    expect(reading.rows[0].migrationsPerSec).toBeUndefined();
    expect(reading.rows[0].migrations).toBe(7);
  });

  it('is empty for no ticks at all', () => {
    expect(readRealmRates([]).rows).toEqual([]);
  });
});
