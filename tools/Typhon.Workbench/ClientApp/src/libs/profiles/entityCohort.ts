/**
 * Spawn/destroy cohorts, and whether the database may be asked about them (#620, design §4.4 — the entity lens).
 *
 * ## Why this bridge is safe where the others needed care
 *
 * `EntityId` is monotonic and never recycled (`EntityId.cs:13`), so an id lifted out of an old capture either finds
 * the same entity or finds nothing. **It can never resolve to a different one.** §4.1 needed a name join and §4.2
 * needed an owned-vs-shared distinction to avoid confident wrong answers; here the identifier itself carries the
 * guarantee.
 *
 * ## The one thing that can still go wrong
 *
 * An entity id embeds its archetype's **routing** id in the low 16 bits. The capture *also* carries a per-process
 * **catalog** id for the same archetype, and the two are usually different numbers — design §5.3's landmine, and it
 * is not hypothetical: the SWG capture this was built against has catalog id 10 and routing id 1 for `Character`.
 * Joining a cohort to a database archetype whose routing id disagrees would answer "none of these are alive" with
 * complete confidence. {@link canJoinCohort} is what stops that, and the caller must honour it.
 */

/** The server's answer for one spawn/destroy cohort. Mirrors `EntityCohortDto`. */
export interface EntityCohort {
  kind: 'spawn' | 'destroy';
  fromTick: number;
  toTick: number;
  totalEntities: number;
  offset: number;
  entityIds: string[];
  hasMore: boolean;
  /** Durable per-database archetype id shared by every entity, or null when the cohort spans archetypes. */
  routingId?: number | null;
  /** The capture's per-process archetype id. Absent for destroy cohorts — display only, never a join key. */
  catalogArchetypeId?: number | null;
  /** Name resolved from the routing id, or null when unknown. Null means *unknown*, never *unnamed*. */
  archetypeName?: string | null;
}

/** The database's verdict on a cohort. Mirrors `CohortResolutionDto`. */
export interface CohortResolution {
  archetypeId: string;
  routingId: number;
  /** TSN the answer was computed against — which present the past cohort was compared to. */
  revision: number;
  aliveIds: string[];
  missingIds: string[];
  /** Ids whose routing id was not this archetype's. Non-zero means the join is wrong, not that entities died. */
  foreignRoutingCount: number;
}

/** Why a cohort cannot be joined to a database archetype, or `null` when it can. */
export type CohortJoinBlocker =
  | 'no-database'
  | 'cohort-empty'
  | 'archetype-unknown'
  | 'cohort-mixed'
  | 'routing-id-mismatch';

/** Routing id the server uses to mean "this cohort spans more than one archetype". */
export const MIXED_ROUTING_ID = 0xffff;

/** The routing id embedded in a raw entity id's low 16 bits. */
export function routingIdOf(rawEntityId: string): number | null {
  // Raw ids exceed 2^53, so they arrive as strings and must stay in BigInt until masked. Parsing to Number first
  // would round the high bits away and could yield a *valid-looking* routing id for the wrong archetype.
  //
  // The digits guard is not belt-and-braces: `BigInt('')` is `0n`, not a throw, so an empty or whitespace id would
  // otherwise resolve to routing id 0 — a perfectly valid archetype — and a missing id would silently join to it.
  if (!/^\d+$/.test(rawEntityId)) {
    return null;
  }
  try {
    return Number(BigInt(rawEntityId) & 0xffffn);
  } catch {
    return null;
  }
}

/**
 * Decides whether a cohort may be resolved against a database archetype, and says why not when it may not.
 *
 * The check is deliberately stricter than "we have both halves": the archetype's routing id, as the database reports
 * it, must equal both the cohort's routing id **and** the id embedded in the cohort's own entity ids. Two independent
 * agreements, because each alone can be satisfied by a mis-join — the design's §5.3 failure mode produces a plausible
 * answer precisely when only one of them is checked.
 *
 * @param cohort            The capture's answer, or null while loading.
 * @param hasDatabase       Whether the session has a database at all.
 * @param databaseRoutingId The routing id the database reports for the archetype being joined to, or null when unknown.
 */
export function canJoinCohort(
  cohort: EntityCohort | null | undefined,
  hasDatabase: boolean,
  databaseRoutingId: number | null | undefined,
): CohortJoinBlocker | null {
  if (!hasDatabase) return 'no-database';
  if (!cohort || cohort.totalEntities === 0) return 'cohort-empty';
  if (cohort.routingId == null || cohort.routingId === MIXED_ROUTING_ID) return 'cohort-mixed';
  if (databaseRoutingId == null) return 'archetype-unknown';
  if (databaseRoutingId !== cohort.routingId) return 'routing-id-mismatch';

  // Third agreement: the ids themselves. A cohort could in principle carry a routing id that its members contradict
  // (a hand-built request, a future encoding change); the ids are the ground truth the database will be queried with.
  const fromIds = cohort.entityIds.length > 0 ? routingIdOf(cohort.entityIds[0]) : null;
  if (fromIds != null && fromIds !== cohort.routingId) return 'routing-id-mismatch';

  return null;
}

/** Plain-language explanation of a blocker, for a UI that must never render a disabled mystery. */
export function explainBlocker(blocker: CohortJoinBlocker, archetypeName?: string | null): string {
  switch (blocker) {
    case 'no-database':
      return 'No database is open, so there is nothing to compare this cohort against.';
    case 'cohort-empty':
      return 'No entities were recorded here.';
    case 'cohort-mixed':
      return 'This range spans more than one archetype, so it cannot be matched to a single one. Narrow it to one archetype first.';
    case 'archetype-unknown':
      return `The open database has no archetype named ${archetypeName ?? 'this'}.`;
    case 'routing-id-mismatch':
      // The one worth spelling out: it means the capture and the database disagree about identity, not that the data is missing.
      return 'These entity ids belong to a different archetype than the one open in this database — the capture and the database disagree, so no answer is offered.';
  }
}

/** How much of a cohort survived, shaped for display. `resolved: false` means the question was not asked. */
export interface CohortSurvival {
  resolved: boolean;
  total: number;
  alive: number;
  destroyed: number;
  /** Non-zero only when a mis-joined cohort slipped through — evidence of a bug, surfaced rather than folded into `destroyed`. */
  foreign: number;
  /** TSN the answer was computed against. */
  revision: number;
  alivePct: number;
}

/** The unresolved verdict — a cohort whose survival was never computed, which is not the same as one where nothing survived. */
export const UNRESOLVED_SURVIVAL: CohortSurvival = Object.freeze({
  resolved: false,
  total: 0,
  alive: 0,
  destroyed: 0,
  foreign: 0,
  revision: 0,
  alivePct: 0,
});

/** Shapes a server resolution for display. Returns {@link UNRESOLVED_SURVIVAL} when there is nothing to shape. */
export function summariseSurvival(resolution: CohortResolution | null | undefined): CohortSurvival {
  if (!resolution) return UNRESOLVED_SURVIVAL;

  const alive = resolution.aliveIds.length;
  const destroyed = resolution.missingIds.length;
  const total = alive + destroyed;
  return {
    resolved: true,
    total,
    alive,
    destroyed,
    foreign: resolution.foreignRoutingCount,
    revision: resolution.revision,
    alivePct: total > 0 ? (alive / total) * 100 : 0,
  };
}

/**
 * True when an archetype holds entities the capture has no spawn record for.
 *
 * Captures written before `SpawnBatch` learned to emit (#620) recorded nothing at all for bulk-loaded worlds, so a
 * populated archetype could show an empty cohort set and look simply quiet. Saying so is the difference between
 * "nothing spawned here" and "this capture cannot tell you".
 */
export function hasUnrecordedEntities(archetypeEntityCount: number, recordedSpawnCount: number): boolean {
  return archetypeEntityCount > 0 && recordedSpawnCount === 0;
}
