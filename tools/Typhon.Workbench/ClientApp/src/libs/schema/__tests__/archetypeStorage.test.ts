import { describe, expect, it } from 'vitest';
import {
  isOwnedByArchetype,
  rollUpArchetypeStorage,
  sharedTableNamesOf,
  type StorageSegmentLike,
} from '../archetypeStorage';

/**
 * #619 §4.2 — the physical lens's join.
 *
 * Two cases carry the weight. The first is the full-name → schema-name hop: an archetype lists its components by
 * CLR full name while the segment table labels them by schema name, so comparing the two directly is #618's bug
 * wearing new clothes. The second is owned-vs-shared: a Versioned component's table is type-global, so summing it
 * into an archetype's footprint counts the same pages once per archetype carrying that component.
 */

function seg(over: Partial<StorageSegmentLike> & { id: number; kind: string; typeName: string }): StorageSegmentLike {
  return {
    pageCount: 1,
    allocatedChunkCount: 0,
    chunkCapacity: 0,
    chunkFillPct: 0,
    reclaimableBytes: 0,
    entityCount: 0,
    occupancyPct: 0,
    ...over,
  };
}

const UNIT = { name: 'Swg.Shard.Unit', componentTypes: ['Typhon.Samples.Swg.Shard.Transform', 'Typhon.Samples.Swg.Shard.Wallet'] };

// The database's component list: `typeName` is the [Component("…")] schema name, `fullName` the CLR type.
const COMPONENTS = [
  { typeName: 'Swg.Shard.Transform', fullName: 'Typhon.Samples.Swg.Shard.Transform' },
  { typeName: 'Swg.Shard.Wallet', fullName: 'Typhon.Samples.Swg.Shard.Wallet' },
  { typeName: 'Swg.Shard.Ham', fullName: 'Typhon.Samples.Swg.Shard.Ham' },
];

describe('isOwnedByArchetype (#619 §4.2)', () => {
  it('claims only the three kinds an archetype owns alone', () => {
    expect(isOwnedByArchetype({ kind: 'Cluster', typeName: 'Swg.Shard.Unit' }, 'Swg.Shard.Unit')).toBe(true);
    expect(isOwnedByArchetype({ kind: 'EntityMap', typeName: 'Swg.Shard.Unit' }, 'Swg.Shard.Unit')).toBe(true);
    expect(isOwnedByArchetype({ kind: 'Index', typeName: 'Swg.Shard.Unit' }, 'Swg.Shard.Unit')).toBe(true);
  });

  it('never claims a component table, even one named after the archetype', () => {
    // Component / Revision segments are type-global. The kind check is what stops a name collision from
    // attributing another type's pages to this archetype.
    expect(isOwnedByArchetype({ kind: 'Component', typeName: 'Swg.Shard.Unit' }, 'Swg.Shard.Unit')).toBe(false);
    expect(isOwnedByArchetype({ kind: 'Revision', typeName: 'Swg.Shard.Unit' }, 'Swg.Shard.Unit')).toBe(false);
  });

  it('does not claim another archetype’s segment, and an empty name claims nothing', () => {
    expect(isOwnedByArchetype({ kind: 'Cluster', typeName: 'Swg.Shard.Structure' }, 'Swg.Shard.Unit')).toBe(false);
    expect(isOwnedByArchetype({ kind: 'Cluster', typeName: 'Swg.Shard.Unit' }, '')).toBe(false);
  });
});

describe('sharedTableNamesOf (#619 AC4 — the full-name → schema-name hop)', () => {
  it('resolves CLR full names to the schema names the segment table actually carries', () => {
    // The whole point: 'Typhon.Samples.Swg.Shard.Transform' never appears in the segment table — 'Swg.Shard.Transform' does.
    const names = sharedTableNamesOf(UNIT, COMPONENTS);

    expect(Array.from(names).sort()).toEqual(['Swg.Shard.Transform', 'Swg.Shard.Wallet']);
    expect(names.has('Typhon.Samples.Swg.Shard.Transform')).toBe(false);
  });

  it('drops a component the database does not know rather than passing its full name through', () => {
    // Passing the unresolved full name through would match nothing in the segment table anyway — but it would do so
    // silently, which is how #618's bug survived untested. An unresolvable component is simply absent.
    const names = sharedTableNamesOf({ name: 'A', componentTypes: ['Some.Removed.Component'] }, COMPONENTS);

    expect(names.size).toBe(0);
  });

  it('is empty for a null archetype or component list', () => {
    expect(sharedTableNamesOf(null, COMPONENTS).size).toBe(0);
    expect(sharedTableNamesOf(UNIT, null).size).toBe(0);
  });
});

describe('rollUpArchetypeStorage (#619 §4.2)', () => {
  const SEGMENTS: StorageSegmentLike[] = [
    seg({ id: 0, kind: 'Cluster', typeName: 'Swg.Shard.Unit', pageCount: 100, allocatedChunkCount: 60, chunkCapacity: 100, chunkFillPct: 60, reclaimableBytes: 4_000, entityCount: 340_182, occupancyPct: 71 }),
    seg({ id: 1, kind: 'EntityMap', typeName: 'Swg.Shard.Unit', pageCount: 4, allocatedChunkCount: 20, chunkCapacity: 100, chunkFillPct: 20, reclaimableBytes: 1_000 }),
    seg({ id: 2, kind: 'Index', typeName: 'Swg.Shard.Unit', pageCount: 6 }),
    seg({ id: 3, kind: 'Component', typeName: 'Swg.Shard.Transform', pageCount: 900, reclaimableBytes: 9_999 }),
    seg({ id: 4, kind: 'Revision', typeName: 'Swg.Shard.Wallet', pageCount: 700 }),
    seg({ id: 5, kind: 'Cluster', typeName: 'Swg.Shard.Structure', pageCount: 50 }),
  ];

  it('sums only what the archetype owns', () => {
    const r = rollUpArchetypeStorage(UNIT, COMPONENTS, SEGMENTS, 4_096);

    expect(r.resolved).toBe(true);
    expect(r.owned.map((s) => s.kind).sort()).toEqual(['Cluster', 'EntityMap', 'Index']);
    expect(r.totalPages).toBe(110);
    expect(r.totalBytes).toBe(110 * 4_096);
    expect(r.reclaimableBytes).toBe(5_000);
    expect(r.entityCount).toBe(340_182);
  });

  it('lists shared component tables but never adds their pages to the total', () => {
    // 900 + 700 pages of shared tables are real, and belong to every archetype carrying those components. Adding
    // them here would report the same bytes again for the next archetype that lists Transform.
    const r = rollUpArchetypeStorage(UNIT, COMPONENTS, SEGMENTS, 4_096);

    expect(r.shared.map((s) => s.typeName).sort()).toEqual(['Swg.Shard.Transform', 'Swg.Shard.Wallet']);
    expect(r.totalPages).toBe(110);
    expect(r.reclaimableBytes).toBe(5_000);
  });

  it('weights chunk fill by capacity rather than averaging percentages', () => {
    // 80 allocated over 200 capacity = 40%. The mean of the two segments' own percentages (60, 20) is also 40 here,
    // so the case is chosen to make the arithmetic differ where it matters: unequal capacities.
    const uneven = [
      seg({ id: 0, kind: 'Cluster', typeName: 'A', pageCount: 1, allocatedChunkCount: 90, chunkCapacity: 100, chunkFillPct: 90 }),
      seg({ id: 1, kind: 'EntityMap', typeName: 'A', pageCount: 1, allocatedChunkCount: 1, chunkCapacity: 900, chunkFillPct: 0.11 }),
    ];

    const r = rollUpArchetypeStorage({ name: 'A', componentTypes: [] }, COMPONENTS, uneven, 4_096);

    expect(r.chunkFillPct).toBeCloseTo((91 / 1000) * 100, 5);
    expect(r.chunkFillPct).not.toBeCloseTo((90 + 0.11) / 2, 1);
  });

  it('an archetype owning no segment is ABSENT, not zeroed (§5.7)', () => {
    // "0 pages, 0% full" is a claim about an archetype that exists. This one does not exist in this database —
    // renamed, removed, or post-dating the file. The two must never render the same.
    const r = rollUpArchetypeStorage({ name: 'Swg.Shard.Vanished', componentTypes: [] }, COMPONENTS, SEGMENTS, 4_096);

    expect(r.resolved).toBe(false);
    expect(r.entityCount).toBeNull();
  });

  it('a legacy archetype reports its small owned footprint plus the shared tables holding its rows', () => {
    // No Cluster segment: the rows are in the shared component tables. The honest answer is a 4-page entity map and
    // an explicit shared list — not "this archetype occupies 4 pages" full stop, and not 1,600 pages either.
    const legacy = SEGMENTS.filter((s) => !(s.kind === 'Cluster' && s.typeName === 'Swg.Shard.Unit'));

    const r = rollUpArchetypeStorage(UNIT, COMPONENTS, legacy, 4_096);

    expect(r.resolved).toBe(true);
    expect(r.totalPages).toBe(10);
    expect(r.entityCount).toBeNull();
    expect(r.shared).toHaveLength(2);
  });

  it('takes the entity count from the Cluster row, not from whichever row happens to report one first', () => {
    // Ordering-independence: if the entity map ever started reporting a count of its own, a first-positive-wins
    // rule would silently start printing it as the archetype's live entity count.
    const reordered = [
      seg({ id: 1, kind: 'EntityMap', typeName: 'A', pageCount: 4, entityCount: 99 }),
      seg({ id: 0, kind: 'Cluster', typeName: 'A', pageCount: 100, entityCount: 340_182 }),
    ];

    const r = rollUpArchetypeStorage({ name: 'A', componentTypes: [] }, COMPONENTS, reordered, 4_096);

    expect(r.entityCount).toBe(340_182);
  });

  it('leaves byte totals at zero when the page size is unknown rather than assuming one', () => {
    const r = rollUpArchetypeStorage(UNIT, COMPONENTS, SEGMENTS, 0);

    expect(r.totalPages).toBe(110);
    expect(r.totalBytes).toBe(0);
  });

  it('is unresolved while the segment table is still loading', () => {
    expect(rollUpArchetypeStorage(UNIT, COMPONENTS, [], 4_096).resolved).toBe(false);
    expect(rollUpArchetypeStorage(UNIT, COMPONENTS, null, 4_096).resolved).toBe(false);
    expect(rollUpArchetypeStorage(null, COMPONENTS, SEGMENTS, 4_096).resolved).toBe(false);
  });
});
