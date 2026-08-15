import { traceIdentityOf, type ComponentIdentityLike } from './componentIdentity';

/**
 * Which storage on disk belongs to an archetype (#619, design §4.2 — the physical lens).
 *
 * ## The join key is the archetype's NAME, and that is not a compromise
 *
 * §5.2 is explicit that an archetype's id must never cross this boundary: the trace records the **per-process
 * catalog id** (registration order, never persisted) while the database's durable identity is the **routing id**,
 * and the two are different `ushort`s for the same archetype in any database that gained archetypes over time.
 * §5.3 calls comparing them the landmine most likely to produce a plausible wrong answer.
 *
 * The name is safe because three independent producers derive it from the *same* expression —
 * `meta.Alias ?? ArchetypeType?.FullName ?? ArchetypeType?.Name`:
 *
 * | Side | Site |
 * |---|---|
 * | Trace archetype table | `ProfilerStaticDataBuilder.cs:162` |
 * | Database segment owner | `DatabaseEngine.StorageIntrospection.cs:498` |
 * | Database archetype list | `LiveSchemaProvider.cs:83` |
 *
 * So unlike the component case that #618 had to repair, both sides genuinely write the same string here.
 *
 * ## Owned vs shared — the distinction that keeps the numbers honest
 *
 * `TryGetSegmentOwnerArchetypeName` labels exactly three segment kinds with an archetype's name: its `Cluster`
 * rows, its `EntityMap`, and its per-archetype cluster `Index`. Those it owns alone.
 *
 * A **Versioned** component's table and MVCC revision segment are type-global — one table per component type,
 * shared by *every* archetype carrying it. Folding those pages into one archetype's footprint would count the
 * same bytes once per archetype and look entirely plausible doing it. They are therefore listed separately and
 * never summed, which also states the true situation for a legacy (non-cluster) archetype: almost all of its
 * data lives in tables it shares.
 */

/** The subset of a `/dbmap/health` segment row this module needs. Matches `HealthSegment` in `useDbMapHealth`. */
export interface StorageSegmentLike {
  id: number;
  kind: string;
  typeName: string;
  pageCount: number;
  allocatedChunkCount: number;
  chunkCapacity: number;
  chunkFillPct: number;
  reclaimableBytes: number;
  entityCount: number;
  occupancyPct: number;
}

/** The subset of `ArchetypeInfo` this module needs — its name and the CLR full names of its component types. */
export interface ArchetypeIdentityLike {
  name: string;
  componentTypes: readonly string[];
}

/**
 * The three segment kinds an archetype owns outright. Anything else carrying its name would be a coincidence of
 * a component sharing the archetype's name, so the kind is checked as well as the name.
 */
const OWNED_KINDS: ReadonlySet<string> = new Set(['Cluster', 'EntityMap', 'Index']);

/** True when this segment is one the archetype owns alone. */
export function isOwnedByArchetype(segment: { kind?: string | null; typeName?: string | null }, archetypeName: string): boolean {
  if (!segment || archetypeName.length === 0) return false;
  return OWNED_KINDS.has(segment.kind ?? '') && (segment.typeName ?? '') === archetypeName;
}

/**
 * The database `typeName`s of the component tables this archetype stores rows in.
 *
 * **This is the hop #618 exists to make safe.** `ArchetypeInfo.componentTypes` holds CLR **full** names
 * (`LiveSchemaProvider.cs:57` — `t.FullName ?? t.Name`) while the segment table is labelled with each component's
 * **schema** name (`table.Definition.Name`). Comparing the two directly is precisely the bug `AccessChips` shipped
 * with before #618: it compiles, reads naturally, and silently matches nothing. Routing through
 * {@link traceIdentityOf} resolves each full name to the component the database knows, then takes *its* `typeName`.
 */
export function sharedTableNamesOf(
  archetype: ArchetypeIdentityLike | null | undefined,
  components: readonly ComponentIdentityLike[] | null | undefined,
): Set<string> {
  const names = new Set<string>();
  if (!archetype || !components) return names;
  for (const full of archetype.componentTypes ?? []) {
    if (!full) continue;
    const match = components.find((c) => traceIdentityOf(c) === full);
    const typeName = match?.typeName ?? '';
    if (typeName.length > 0) names.add(typeName);
  }
  return names;
}

/** One segment in an archetype's storage picture. */
export interface ArchetypeStorageSegment {
  id: number;
  kind: string;
  typeName: string;
  pageCount: number;
  chunkFillPct: number;
  reclaimableBytes: number;
  entityCount: number;
  occupancyPct: number;
}

/**
 * What the database says an archetype's storage looks like **right now**.
 *
 * Every total covers {@link owned} only — see the module remarks for why {@link shared} is deliberately excluded
 * from the arithmetic rather than added to it.
 */
export interface ArchetypeStorage {
  /** Segments the archetype owns alone: its cluster rows, entity map, and cluster index. */
  owned: ArchetypeStorageSegment[];
  /** Component tables it stores rows in, shared with every other archetype carrying that component. */
  shared: ArchetypeStorageSegment[];
  /** Pages across {@link owned}. */
  totalPages: number;
  /** Bytes across {@link owned}, from the file's own page size. Zero when the page size is unknown. */
  totalBytes: number;
  /** Capacity-weighted chunk fill across {@link owned} — a sum of allocations over a sum of capacities, never a mean of percentages. */
  chunkFillPct: number;
  /** Reclaimable (free-chunk) bytes across {@link owned}. */
  reclaimableBytes: number;
  /** Live entities in the archetype's cluster rows; null when no owned segment reports one. */
  entityCount: number | null;
  /**
   * False when the archetype owns no segment in this database — it post-dates the capture, was removed, or was
   * renamed past what can be resolved. §5.7: the bridge is then **absent**, and every field above is meaningless.
   * A zeroed rollup would read as "this archetype occupies nothing", which is a different and false claim.
   */
  resolved: boolean;
}

/** The unresolved verdict. Frozen so a consumer cannot mutate the shared instance. */
export const UNRESOLVED_ARCHETYPE_STORAGE: ArchetypeStorage = Object.freeze({
  owned: Object.freeze([]) as unknown as ArchetypeStorageSegment[],
  shared: Object.freeze([]) as unknown as ArchetypeStorageSegment[],
  totalPages: 0,
  totalBytes: 0,
  chunkFillPct: 0,
  reclaimableBytes: 0,
  entityCount: null,
  resolved: false,
});

function toSegment(s: StorageSegmentLike): ArchetypeStorageSegment {
  return {
    id: s.id,
    kind: s.kind,
    typeName: s.typeName,
    pageCount: s.pageCount,
    chunkFillPct: s.chunkFillPct,
    reclaimableBytes: s.reclaimableBytes,
    entityCount: s.entityCount,
    occupancyPct: s.occupancyPct,
  };
}

/**
 * Roll an archetype's storage up from the `/dbmap/health` segment table.
 *
 * @param archetype    The database's own record of the archetype, or null while the list loads.
 * @param components   The database's component list — used for the full-name → schema-name hop.
 * @param segments     Every segment row from `/dbmap/health`.
 * @param pageSizeBytes Bytes per page, derived by the caller from the file's own `dataFileBytes / dataFilePageCount`
 *   rather than assumed, so a future page-size change cannot make this silently wrong. Zero leaves byte totals at 0.
 */
export function rollUpArchetypeStorage(
  archetype: ArchetypeIdentityLike | null | undefined,
  components: readonly ComponentIdentityLike[] | null | undefined,
  segments: readonly StorageSegmentLike[] | null | undefined,
  pageSizeBytes: number,
): ArchetypeStorage {
  const name = archetype?.name ?? '';
  if (name.length === 0 || !segments || segments.length === 0) {
    return UNRESOLVED_ARCHETYPE_STORAGE;
  }

  const owned: ArchetypeStorageSegment[] = [];
  const ownedIds = new Set<number>();
  let totalPages = 0;
  let allocatedChunks = 0;
  let chunkCapacity = 0;
  let reclaimableBytes = 0;
  let entityCount: number | null = null;

  for (const s of segments) {
    if (!isOwnedByArchetype(s, name)) continue;
    owned.push(toSegment(s));
    ownedIds.add(s.id);
    totalPages += s.pageCount;
    allocatedChunks += s.allocatedChunkCount;
    chunkCapacity += s.chunkCapacity;
    reclaimableBytes += s.reclaimableBytes;
    // The entity count comes from the Cluster row specifically, not from "whichever owned row reports a positive
    // one". Only `TryGetClusterStats` produces it (`StorageMapService.cs:93-98`) and every other kind reports 0 —
    // so a first-positive-wins rule would happen to be right today and silently depend on segment ordering the
    // moment another kind starts reporting a count of its own.
    if (s.kind === 'Cluster') {
      entityCount = s.entityCount;
    }
  }

  if (owned.length === 0) {
    return UNRESOLVED_ARCHETYPE_STORAGE;
  }

  // A component sharing an archetype's name would put one Index row in both buckets; owned wins, since that is the
  // stricter claim and the one the totals already counted.
  const sharedNames = sharedTableNamesOf(archetype, components);
  const shared: ArchetypeStorageSegment[] = [];
  for (const s of segments) {
    if (ownedIds.has(s.id)) continue;
    if (sharedNames.has(s.typeName)) shared.push(toSegment(s));
  }

  return {
    owned,
    shared,
    totalPages,
    totalBytes: pageSizeBytes > 0 ? totalPages * pageSizeBytes : 0,
    chunkFillPct: chunkCapacity > 0 ? (allocatedChunks / chunkCapacity) * 100 : 0,
    reclaimableBytes,
    entityCount,
    resolved: true,
  };
}
