import { describe, expect, it } from 'vitest';
import { processTickEvents } from '@/libs/profiler/model/traceModel';
import type { TraceEvent } from '@/libs/profiler/model/types';

/**
 * A decoded kind-70 event must reach `TickData.spatialRatesByRealm`.
 *
 * **This test exists because its absence let the feature ship dead twice over.** The decoder had no case for kind 70, so
 * every record was dropped; and the tick builder filled a local map it then forgot to put on the returned tick. Either
 * break alone made the panel show nothing, and each hid the other. Meanwhile the reading layer's tests passed
 * throughout, because they construct `TickData` by hand and never exercise the path from an event to a tick.
 *
 * The lesson is narrow and worth stating: a pure function tested against its own hand-built input proves the
 * arithmetic and nothing about whether anything ever calls it with real data.
 */

// `TraceEventKind` is a const enum and Vitest does not inline those, so the numeric literal is used deliberately —
// the same convention as `processTickEvents.test.ts`.
const SPATIAL_REALM_RATES = 70;
const SPATIAL_ARCHETYPE_TELEMETRY = 66;

function rateEvent(realmId: number, archetypeId: number, over: Partial<TraceEvent> = {}): TraceEvent {
  return {
    kind: SPATIAL_REALM_RATES, threadSlot: 0, tickNumber: 1, timestampUs: 0,
    realmId, archetypeId, migrationCount: 4, clustersScanned: 2, largestArrivalRun: 6,
    ...over,
  } as unknown as TraceEvent;
}

describe('kind 70 reaches the tick', () => {
  it('lands in spatialRatesByRealm, keyed by (realm, archetype)', () => {
    const tick = processTickEvents(1, [rateEvent(7, 3)], []);

    expect(tick.spatialRatesByRealm, 'the map must be ON the tick, not merely built inside the builder').toBeDefined();
    const row = tick.spatialRatesByRealm?.get(7 * 65536 + 3);
    expect(row).toBeDefined();
    expect(row?.realmId).toBe(7);
    expect(row?.archetypeId).toBe(3);
    expect(row?.migrationCount).toBe(4);
    expect(row?.largestArrivalRun).toBe(6);
  });

  it('keeps one row per (realm, archetype) rather than letting a realm overwrite itself across archetypes', () => {
    const tick = processTickEvents(1, [rateEvent(7, 3), rateEvent(7, 4, { migrationCount: 11 })], []);

    expect(tick.spatialRatesByRealm?.size).toBe(2);
    expect(tick.spatialRatesByRealm?.get(7 * 65536 + 3)?.migrationCount).toBe(4);
    expect(tick.spatialRatesByRealm?.get(7 * 65536 + 4)?.migrationCount).toBe(11);
  });

  it('is undefined — not an empty map — on a tick with no rate rows, so a consumer can tell the two apart', () => {
    const tick = processTickEvents(1, [], []);

    expect(tick.spatialRatesByRealm).toBeUndefined();
  });

  it('carries the rate-row census from kind 66 onto the archetype row', () => {
    const censusEvent = {
      kind: SPATIAL_ARCHETYPE_TELEMETRY, threadSlot: 0, tickNumber: 1, timestampUs: 0,
      archetypeId: 3, ratesRealmsTouched: 312, ratesRealmsEmitted: 64,
    } as unknown as TraceEvent;

    const tick = processTickEvents(1, [censusEvent], []);

    // Without these two the truncation banner can never fire, whatever the engine sent.
    expect(tick.spatialByArchetype?.get(3)?.ratesRealmsTouched).toBe(312);
    expect(tick.spatialByArchetype?.get(3)?.ratesRealmsEmitted).toBe(64);
  });
});
