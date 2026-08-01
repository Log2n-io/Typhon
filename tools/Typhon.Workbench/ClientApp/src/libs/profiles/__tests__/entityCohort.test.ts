import { describe, expect, it } from 'vitest';
import {
  canJoinCohort,
  explainBlocker,
  hasUnrecordedEntities,
  MIXED_ROUTING_ID,
  routingIdOf,
  summariseSurvival,
  UNRESOLVED_SURVIVAL,
  type EntityCohort,
} from '../entityCohort';

/**
 * #620 §4.4 — the entity lens's safety rules.
 *
 * The bridge itself is safe by construction: entity ids are never recycled, so an old id either finds its entity or
 * finds nothing. What these tests protect is the one thing that can still go wrong — joining a cohort to an archetype
 * whose routing id disagrees, which produces a confident "none of these are alive" (design §5.3).
 */

const ROUTING = 1;
const CATALOG = 10;

/** Raw id for (key, routingId). Built with BigInt because a real id exceeds 2^53. */
const raw = (key: number, routing = ROUTING) => ((BigInt(key) << 16n) | BigInt(routing)).toString();

function cohort(over: Partial<EntityCohort> = {}): EntityCohort {
  return {
    kind: 'spawn',
    fromTick: 4102,
    toTick: 4102,
    totalEntities: 3,
    offset: 0,
    entityIds: [raw(1), raw(2), raw(3)],
    hasMore: false,
    routingId: ROUTING,
    catalogArchetypeId: CATALOG,
    archetypeName: 'Swg.Shard.Character',
    ...over,
  };
}

describe('routingIdOf', () => {
  it('extracts the routing id from ids far beyond 2^53', () => {
    // 340,282 entities in, the raw id is ~2.2e16 — past Number.MAX_SAFE_INTEGER. Parsing to Number first would round
    // the high bits and could yield a *valid-looking* routing id for the wrong archetype.
    const big = raw(340_282_000_000, 7);
    expect(Number(big) > Number.MAX_SAFE_INTEGER).toBe(true);
    expect(routingIdOf(big)).toBe(7);
  });

  it('returns null for a malformed id rather than a plausible number', () => {
    expect(routingIdOf('not-an-id')).toBeNull();
    expect(routingIdOf('')).toBeNull();
  });
});

describe('canJoinCohort (§5.3 — the landmine, as a check)', () => {
  it('allows the join when the database, the cohort and the ids all agree', () => {
    expect(canJoinCohort(cohort(), true, ROUTING)).toBeNull();
  });

  it('refuses when the database archetype has a different routing id', () => {
    // The live SWG capture has catalog id 10 and routing id 1 for the same archetype. Joining on the catalog id is
    // exactly this case, and it would answer "0 of 3 alive" with total confidence.
    expect(canJoinCohort(cohort(), true, CATALOG)).toBe('routing-id-mismatch');
  });

  it('refuses when the ids themselves contradict the cohort’s stated routing id', () => {
    // Third agreement: a cohort could claim a routing id its members do not carry. The ids are what the database will
    // actually be queried with, so they get the final say.
    const lying = cohort({ routingId: ROUTING, entityIds: [raw(1, 9), raw(2, 9)] });
    expect(canJoinCohort(lying, true, ROUTING)).toBe('routing-id-mismatch');
  });

  it('refuses a cohort that spans several archetypes instead of picking one', () => {
    expect(canJoinCohort(cohort({ routingId: MIXED_ROUTING_ID }), true, ROUTING)).toBe('cohort-mixed');
    expect(canJoinCohort(cohort({ routingId: null }), true, ROUTING)).toBe('cohort-mixed');
  });

  it('reports no database rather than an empty result', () => {
    expect(canJoinCohort(cohort(), false, ROUTING)).toBe('no-database');
  });

  it('reports an unknown archetype distinctly from a mismatched one', () => {
    expect(canJoinCohort(cohort(), true, null)).toBe('archetype-unknown');
  });

  it('treats an empty cohort as nothing to ask about', () => {
    expect(canJoinCohort(cohort({ totalEntities: 0 }), true, ROUTING)).toBe('cohort-empty');
    expect(canJoinCohort(null, true, ROUTING)).toBe('cohort-empty');
  });
});

describe('explainBlocker', () => {
  it('says the capture and the database disagree — not that the data is missing', () => {
    const text = explainBlocker('routing-id-mismatch');
    expect(text).toMatch(/different archetype/i);
    expect(text).toMatch(/disagree/i);
  });

  it('every blocker has a plain-language reason, so nothing renders as an unexplained absence', () => {
    for (const b of ['no-database', 'cohort-empty', 'archetype-unknown', 'cohort-mixed', 'routing-id-mismatch'] as const) {
      expect(explainBlocker(b, 'Character').length).toBeGreaterThan(20);
    }
  });
});

describe('summariseSurvival', () => {
  it('splits a cohort and reports the survival rate', () => {
    const s = summariseSurvival({
      archetypeId: '10',
      routingId: ROUTING,
      revision: 132,
      aliveIds: [raw(1), raw(2)],
      missingIds: [raw(3)],
      foreignRoutingCount: 0,
    });

    expect(s.resolved).toBe(true);
    expect(s.alive).toBe(2);
    expect(s.destroyed).toBe(1);
    expect(s.total).toBe(3);
    expect(s.alivePct).toBeCloseTo(66.67, 1);
    expect(s.revision).toBe(132);
  });

  it('keeps foreign-routing ids out of the destroyed count', () => {
    // Folding them in would render a wrong join as a mass extinction — the most misleading possible outcome, since
    // "everything died" is a plausible thing to see in a real capture.
    const s = summariseSurvival({
      archetypeId: '10',
      routingId: ROUTING,
      revision: 1,
      aliveIds: [],
      missingIds: [],
      foreignRoutingCount: 50,
    });

    expect(s.destroyed).toBe(0);
    expect(s.foreign).toBe(50);
    expect(s.total).toBe(0);
  });

  it('an unasked question is UNRESOLVED, not zero survivors', () => {
    expect(summariseSurvival(null)).toBe(UNRESOLVED_SURVIVAL);
    expect(summariseSurvival(null).resolved).toBe(false);
  });
});

describe('hasUnrecordedEntities (the bulk-load blind spot)', () => {
  it('flags an archetype that holds entities the capture recorded no spawn for', () => {
    // Captures written before SpawnBatch learned to emit contain nothing for bulk-loaded worlds. Silence there means
    // "this capture cannot tell you", not "nothing spawned".
    expect(hasUnrecordedEntities(340_182, 0)).toBe(true);
  });

  it('does not flag a genuinely quiet archetype', () => {
    expect(hasUnrecordedEntities(0, 0)).toBe(false);
  });

  it('does not flag an archetype whose spawns were recorded', () => {
    expect(hasUnrecordedEntities(500, 500)).toBe(false);
  });
});
