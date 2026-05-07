import { describe, expect, it } from 'vitest';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import { deriveEdges, type DerivedEdge, type DerivedEdgeKind } from '../edgeDerivation';

function sys(overrides: Partial<SystemDefinitionDto> & { name: string }): SystemDefinitionDto {
  return {
    index: 0,
    type: 0,
    priority: 0,
    isParallel: false,
    tierFilter: 15,
    predecessors: [],
    successors: [],
    isExclusivePhase: false,
    phaseName: 'Simulation',
    reads: [],
    readsFresh: [],
    readsSnapshot: [],
    additionalReads: [],
    writes: [],
    sideWrites: [],
    writesEvents: [],
    readsEvents: [],
    writesResources: [],
    readsResources: [],
    explicitAfter: [],
    explicitBefore: [],
    ...overrides,
  };
}

function summarise(edges: DerivedEdge[]): { kind: DerivedEdgeKind; source: string; target: string; via: string[] }[] {
  return edges.map((e) => ({ kind: e.kind, source: e.source, target: e.target, via: e.via }));
}

describe('deriveEdges', () => {
  it('returns no edges for an empty system list', () => {
    expect(deriveEdges([])).toEqual([]);
  });

  it('emits a fresh-read edge writer → reader', () => {
    const edges = deriveEdges([
      sys({ name: 'Movement', writes: ['Position'] }),
      sys({ name: 'AI', readsFresh: ['Position'] }),
    ]);
    expect(summarise(edges)).toEqual([
      { kind: 'fresh', source: 'Movement', target: 'AI', via: ['Position'] },
    ]);
    expect(edges[0].reason).toContain('AI reads Position fresh');
    expect(edges[0].reason).toContain('runs after Movement');
  });

  it('emits a snapshot edge reader → writer (reader runs before writer)', () => {
    const edges = deriveEdges([
      sys({ name: 'Movement', writes: ['Position'] }),
      sys({ name: 'Render', readsSnapshot: ['Position'] }),
    ]);
    expect(summarise(edges)).toEqual([
      { kind: 'snapshot', source: 'Render', target: 'Movement', via: ['Position'] },
    ]);
  });

  it('plain Reads (ambiguous freshness) does NOT produce an edge', () => {
    // Per RFC 07: plain Reads<T> alongside a same-phase writer is a Build()-time error, not a
    // derived edge. The DAG view trusts the engine to reject those, so no edge here.
    const edges = deriveEdges([
      sys({ name: 'Movement', writes: ['Position'] }),
      sys({ name: 'Auditor', reads: ['Position'] }),
    ]);
    expect(edges).toEqual([]);
  });

  it('skips edges across phases', () => {
    const edges = deriveEdges([
      sys({ name: 'Movement', writes: ['Position'], phaseName: 'Simulation' }),
      sys({ name: 'Render', readsSnapshot: ['Position'], phaseName: 'Output' }),
    ]);
    expect(edges).toEqual([]);
  });

  it('emits a manual After edge', () => {
    const edges = deriveEdges([
      sys({ name: 'Movement' }),
      sys({ name: 'Clamp', explicitAfter: ['Movement'] }),
    ]);
    expect(summarise(edges)).toEqual([
      { kind: 'manual', source: 'Movement', target: 'Clamp', via: ['Movement'] },
    ]);
    expect(edges[0].reason).toBe('Manual edge: Clamp.After(Movement)');
  });

  it('emits a manual Before edge with reversed direction', () => {
    const edges = deriveEdges([
      sys({ name: 'Audit', explicitBefore: ['Cleanup'] }),
      sys({ name: 'Cleanup' }),
    ]);
    expect(summarise(edges)).toEqual([
      { kind: 'manual', source: 'Audit', target: 'Cleanup', via: ['Cleanup'] },
    ]);
  });

  it('emits an event-queue edge producer → consumer', () => {
    const edges = deriveEdges([
      sys({ name: 'Combat', writesEvents: ['DamageQueue'] }),
      sys({ name: 'Health', readsEvents: ['DamageQueue'] }),
    ]);
    expect(summarise(edges)).toEqual([
      { kind: 'event', source: 'Combat', target: 'Health', via: ['DamageQueue'] },
    ]);
  });

  it('emits a resource conflict edge from writer to other system', () => {
    const edges = deriveEdges([
      sys({ name: 'Physics', writesResources: ['PhysicsWorld'] }),
      sys({ name: 'Sensors', readsResources: ['PhysicsWorld'] }),
    ]);
    expect(summarise(edges)).toEqual([
      { kind: 'resource', source: 'Physics', target: 'Sensors', via: ['PhysicsWorld'] },
    ]);
  });

  it('two read-only-resource systems produce no edge', () => {
    const edges = deriveEdges([
      sys({ name: 'A', readsResources: ['shared'] }),
      sys({ name: 'B', readsResources: ['shared'] }),
    ]);
    expect(edges).toEqual([]);
  });

  it('two write-resource systems produce a single alphabetical resource edge', () => {
    const edges = deriveEdges([
      sys({ name: 'WriterA', writesResources: ['shared'] }),
      sys({ name: 'WriterB', writesResources: ['shared'] }),
    ]);
    expect(summarise(edges)).toEqual([
      { kind: 'resource', source: 'WriterA', target: 'WriterB', via: ['shared'] },
    ]);
  });

  it('merges multiple components on the same (writer, reader) pair into one edge', () => {
    const edges = deriveEdges([
      sys({ name: 'Movement', writes: ['Position', 'Velocity'] }),
      sys({ name: 'AI', readsFresh: ['Position', 'Velocity'] }),
    ]);
    expect(summarise(edges)).toEqual([
      { kind: 'fresh', source: 'Movement', target: 'AI', via: ['Position', 'Velocity'] },
    ]);
  });

  it('emits separate edges per (kind, source, target) on the same pair', () => {
    // Movement writes Position; AI reads Position fresh AND Health snapshot.
    const edges = deriveEdges([
      sys({ name: 'Movement', writes: ['Position'] }),
      sys({ name: 'AI', readsFresh: ['Position'], readsSnapshot: ['Health'] }),
      sys({ name: 'Healing', writes: ['Health'] }),
    ]);
    const summary = summarise(edges);
    // fresh: Movement → AI ; snapshot: AI → Healing
    expect(summary).toContainEqual({ kind: 'fresh', source: 'Movement', target: 'AI', via: ['Position'] });
    expect(summary).toContainEqual({ kind: 'snapshot', source: 'AI', target: 'Healing', via: ['Health'] });
  });

  it('does not emit an edge between a system and itself', () => {
    const edges = deriveEdges([
      // Pathological self-reference. Real schedulers would reject this; the deriver must not crash.
      sys({ name: 'Loopy', writes: ['X'], readsFresh: ['X'] }),
    ]);
    expect(edges).toEqual([]);
  });

  it('output is sorted by (kind, source, target) for deterministic snapshots', () => {
    const edges = deriveEdges([
      sys({ name: 'Z', writes: ['T'] }),
      sys({ name: 'A', readsFresh: ['T'] }),
      sys({ name: 'M', readsFresh: ['T'] }),
    ]);
    const summary = summarise(edges);
    expect(summary).toEqual([
      { kind: 'fresh', source: 'Z', target: 'A', via: ['T'] },
      { kind: 'fresh', source: 'Z', target: 'M', via: ['T'] },
    ]);
  });

  it('SideWrites do NOT produce an edge', () => {
    const edges = deriveEdges([
      sys({ name: 'Inventory', sideWrites: ['Inventory'] }),
      sys({ name: 'AI', readsFresh: ['Inventory'] }),
    ]);
    expect(edges).toEqual([]);
  });

  it('AdditionalReads paired with a writer produces no edge by itself', () => {
    // AdditionalReads is "I read this beyond my input view" — same staleness ambiguity as Reads.
    // No automatic edge until the user picks fresh / snapshot.
    const edges = deriveEdges([
      sys({ name: 'Movement', writes: ['Position'] }),
      sys({ name: 'AI', additionalReads: ['Position'] }),
    ]);
    expect(edges).toEqual([]);
  });
});
