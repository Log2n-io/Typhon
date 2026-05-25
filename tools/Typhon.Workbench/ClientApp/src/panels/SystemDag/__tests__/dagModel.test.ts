import { describe, expect, it } from 'vitest';
import type { DagDto } from '@/api/generated/model/dagDto';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { TopologyDto } from '@/api/generated/model/topologyDto';
import type { TrackDto } from '@/api/generated/model/trackDto';
import { LANE_GAP, buildDagModel, resolveNoAccessReason, toNodeData, topologyHasAnyAccess } from '../dagModel';

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
    dagId: 0,
    ...overrides,
  };
}

function topo(systems: SystemDefinitionDto[], phases: string[] = ['Input', 'Simulation', 'Output']): TopologyDto {
  return {
    systems,
    archetypes: [],
    componentTypes: [],
    phases,
    tracks: [],
    componentFamilies: { componentToFamily: {}, familyOrder: [] },
  };
}

describe('buildDagModel', () => {
  it('returns an empty model for null/empty topology', () => {
    expect(buildDagModel(null)).toEqual({ nodes: [], edges: [], lanes: [], dagGroups: [], width: 0, height: 0 });
    expect(buildDagModel(topo([]))).toEqual({ nodes: [], edges: [], lanes: [], dagGroups: [], width: 0, height: 0 });
  });

  it('places one lane per non-empty phase, in canonical order', () => {
    const t = topo([
      sys({ name: 'In1', phaseName: 'Input' }),
      sys({ name: 'Sim1', phaseName: 'Simulation' }),
      sys({ name: 'Out1', phaseName: 'Output' }),
    ]);
    const model = buildDagModel(t);
    expect(model.lanes.map((l) => l.name)).toEqual(['Input', 'Simulation', 'Output']);
    expect(model.lanes.every((l) => l.systemCount === 1)).toBe(true);
    // Lanes stack top-to-bottom.
    for (let i = 1; i < model.lanes.length; i++) {
      expect(model.lanes[i].yTop).toBeGreaterThanOrEqual(model.lanes[i - 1].yTop + model.lanes[i - 1].height + LANE_GAP - 1);
    }
  });

  it('skips phases that have no systems', () => {
    const t = topo([
      sys({ name: 'Sim1', phaseName: 'Simulation' }),
    ]);
    const model = buildDagModel(t);
    expect(model.lanes.map((l) => l.name)).toEqual(['Simulation']);
  });

  it('routes systems with unknown phase into a synthetic (unphased) lane at the end', () => {
    const t = topo([
      sys({ name: 'Sim1', phaseName: 'Simulation' }),
      sys({ name: 'Orphan', phaseName: 'NotInList' }),
      sys({ name: 'Empty', phaseName: '' }),
    ]);
    const model = buildDagModel(t);
    expect(model.lanes.map((l) => l.name)).toEqual(['Simulation', '(unphased)']);
    expect(model.lanes[1].systemCount).toBe(2);
  });

  it('emits one React-Flow node per system with topology data attached', () => {
    const t = topo([
      sys({ name: 'Movement', writes: ['Position'], type: 1, isParallel: true }),
    ]);
    const model = buildDagModel(t);
    expect(model.nodes).toHaveLength(1);
    const n = model.nodes[0];
    expect(n.id).toBe('Movement');
    expect(n.type).toBe('system');
    expect(n.data.kind).toBe('Query');
    expect(n.data.isParallel).toBe(true);
    expect(n.data.writes).toEqual(['Position']);
    expect(n.data.hasAccess).toBe(true);
  });

  it('emits intra-phase edges and drops cross-phase edges', () => {
    const t = topo([
      sys({ name: 'Movement', writes: ['Position'], phaseName: 'Simulation' }),
      sys({ name: 'AI', readsFresh: ['Position'], phaseName: 'Simulation' }),
      // Cross-phase pair — must NOT produce an edge in the model.
      sys({ name: 'Render', readsSnapshot: ['Position'], phaseName: 'Output' }),
    ]);
    const model = buildDagModel(t);
    const edgeKinds = model.edges.map((e) => `${e.data?.kind}:${e.source}->${e.target}`);
    expect(edgeKinds).toEqual(['fresh:Movement->AI']);
  });

  it('with showCrossPhaseEdges=true keeps the cross-phase edges in lane layouts', () => {
    // Same fixture as the previous test, with the user opting into cross-phase visibility.
    // Cross-phase edges always point earlier-phase → later-phase (Simulation → Output for
    // Movement → Render); kind is 'fresh' regardless of whether the reader declared Snapshot
    // because phase order forces writer-first across phases (see edgeDerivation cross-phase
    // header for rationale). Within Simulation, the fresh edge Movement → AI is unaffected.
    const t = topo([
      sys({ name: 'Movement', writes: ['Position'], phaseName: 'Simulation' }),
      sys({ name: 'AI', readsFresh: ['Position'], phaseName: 'Simulation' }),
      sys({ name: 'Render', readsSnapshot: ['Position'], phaseName: 'Output' }),
    ]);
    const model = buildDagModel(t, 'horizontal-lanes', { showCrossPhaseEdges: true });
    const edgeKinds = model.edges.map((e) => `${e.data?.kind}:${e.source}->${e.target}`).sort();
    expect(edgeKinds).toEqual([
      'fresh:Movement->AI',
      'fresh:Movement->Render',
    ]);
  });

  it('showCrossPhaseEdges defaults to false (intra-phase-only behaviour preserved)', () => {
    const t = topo([
      sys({ name: 'Movement', writes: ['Position'], phaseName: 'Simulation' }),
      sys({ name: 'Render', readsFresh: ['Position'], phaseName: 'Output' }),
    ]);
    // No options object passed → cross-phase suppressed.
    const model = buildDagModel(t, 'horizontal-lanes');
    expect(model.edges).toHaveLength(0);
  });

  it('showCrossPhaseEdges also applies to vertical-lanes layout', () => {
    const t = topo([
      sys({ name: 'Movement', writes: ['Position'], phaseName: 'Simulation' }),
      sys({ name: 'Render', readsFresh: ['Position'], phaseName: 'Output' }),
    ]);
    const off = buildDagModel(t, 'vertical-lanes');
    const on = buildDagModel(t, 'vertical-lanes', { showCrossPhaseEdges: true });
    expect(off.edges).toHaveLength(0);
    expect(on.edges.map((e) => `${e.source}->${e.target}`)).toEqual(['Movement->Render']);
  });

  it('translates dagre coordinates so each phase lane sits below its predecessor', () => {
    const t = topo([
      sys({ name: 'In1', phaseName: 'Input' }),
      sys({ name: 'Sim1', phaseName: 'Simulation' }),
    ]);
    const model = buildDagModel(t);
    const inputNode = model.nodes.find((n) => n.id === 'In1')!;
    const simNode = model.nodes.find((n) => n.id === 'Sim1')!;
    expect(simNode.position.y).toBeGreaterThan(inputNode.position.y);
  });

  it('node x-positions account for the lane label margin', () => {
    const t = topo([sys({ name: 'A' })]);
    const model = buildDagModel(t);
    expect(model.nodes[0].position.x).toBeGreaterThan(0);
  });
});

// ── Alternate layouts (introduced in #316 follow-up) ─────────────────────

describe('buildDagModel — vertical-lanes layout', () => {
  it('emits lanes with top-edge labels and stacks them horizontally', () => {
    const a = sys({ name: 'A', phaseName: 'Input' });
    const b = sys({ name: 'B', phaseName: 'Simulation' });
    const c = sys({ name: 'C', phaseName: 'Output' });
    const model = buildDagModel(topo([a, b, c]), 'vertical-lanes');
    expect(model.lanes).toHaveLength(3);
    for (const lane of model.lanes) expect(lane.labelEdge).toBe('top');
    // Lanes stack horizontally — each xLeft strictly greater than the previous.
    for (let i = 1; i < model.lanes.length; i++) {
      expect(model.lanes[i].xLeft).toBeGreaterThan(model.lanes[i - 1].xLeft);
    }
  });

  it('all lanes share the same yTop (origin)', () => {
    const model = buildDagModel(
      topo([sys({ name: 'A', phaseName: 'Input' }), sys({ name: 'B', phaseName: 'Simulation' })]),
      'vertical-lanes',
    );
    for (const lane of model.lanes) expect(lane.yTop).toBe(0);
  });
});

describe('buildDagModel — compact layout', () => {
  it('emits no lanes', () => {
    const model = buildDagModel(
      topo([sys({ name: 'A', phaseName: 'Input' }), sys({ name: 'B', phaseName: 'Simulation' })]),
      'compact',
    );
    expect(model.lanes).toEqual([]);
  });

  it('keeps cross-phase edges that the lane-based layouts would drop', () => {
    // Manual .After across phases: A in Input, B in Simulation, B.After(A).
    const a = sys({ name: 'A', phaseName: 'Input' });
    const b = sys({ name: 'B', phaseName: 'Simulation', explicitAfter: ['A'] });
    const compact = buildDagModel(topo([a, b]), 'compact');
    const horizontal = buildDagModel(topo([a, b]), 'horizontal-lanes');
    // Compact preserves the cross-phase manual edge; lane-based layout filters it out.
    expect(compact.edges.find((e) => e.source === 'A' && e.target === 'B')).toBeDefined();
    expect(horizontal.edges.find((e) => e.source === 'A' && e.target === 'B')).toBeUndefined();
  });
});

describe('buildDagModel — circular layout', () => {
  it('emits no lanes and places all systems', () => {
    const systems = ['A', 'B', 'C', 'D'].map((name) => sys({ name, phaseName: 'Simulation' }));
    const model = buildDagModel(topo(systems), 'circular');
    expect(model.lanes).toEqual([]);
    expect(model.nodes).toHaveLength(4);
  });

  it('yields a square bounding box (width === height) — invariant of the geometry', () => {
    const systems = ['A', 'B', 'C', 'D', 'E'].map((name) => sys({ name }));
    const model = buildDagModel(topo(systems), 'circular');
    expect(model.width).toBe(model.height);
    expect(model.width).toBeGreaterThan(0);
  });

  it('places nodes at distinct positions', () => {
    const systems = ['A', 'B', 'C'].map((name) => sys({ name }));
    const model = buildDagModel(topo(systems), 'circular');
    const positions = model.nodes.map((n) => `${n.position.x.toFixed(0)},${n.position.y.toFixed(0)}`);
    expect(new Set(positions).size).toBe(3);
  });
});

// ── Track → DAG hierarchy (#354 W5) ──────────────────────────────────────

function dag(id: number, name: string, phases: string[]): DagDto {
  return { id, name, phases };
}

function track(name: string, orderIndex: number, tags: string[], dags: DagDto[]): TrackDto {
  return { name, orderIndex, tags, dags };
}

function topoH(systems: SystemDefinitionDto[], tracks: TrackDto[]): TopologyDto {
  return {
    systems,
    archetypes: [],
    componentTypes: [],
    phases: [],
    tracks,
    componentFamilies: { componentToFamily: {}, familyOrder: [] },
  };
}

/** A Public track with a "World" DAG (id 0) + an engine-tagged Engine-Post track with a "Fence" DAG (id 1). */
function worldAndFenceTracks(): TrackDto[] {
  return [
    track('Public', 1, [], [dag(0, 'World', ['Input', 'Simulation', 'Render'])]),
    track('Engine-Post', 2, ['engine'], [dag(1, 'Fence', ['Default'])]),
  ];
}

describe('buildDagModel — Track → DAG hierarchy', () => {
  it('hides engine-tagged track systems by default', () => {
    const t = topoH(
      [
        sys({ name: 'Movement', phaseName: 'Simulation', dagId: 0 }),
        sys({ name: 'FencePrep', phaseName: 'Default', dagId: 1 }),
      ],
      worldAndFenceTracks(),
    );
    const model = buildDagModel(t);
    expect(model.nodes.map((n) => n.id)).toEqual(['Movement']);
    // The Fence DAG produced no group — its only system was filtered out.
    expect(model.dagGroups.map((g) => g.dagName)).toEqual(['World']);
  });

  it('reveals engine-tagged tracks when showEngineTracks is set', () => {
    const t = topoH(
      [
        sys({ name: 'Movement', phaseName: 'Simulation', dagId: 0 }),
        sys({ name: 'FencePrep', phaseName: 'Default', dagId: 1 }),
      ],
      worldAndFenceTracks(),
    );
    const model = buildDagModel(t, 'horizontal-lanes', { showEngineTracks: true });
    expect(model.nodes.map((n) => n.id).sort()).toEqual(['FencePrep', 'Movement']);
    expect(model.dagGroups.map((g) => g.dagName)).toEqual(['World', 'Fence']);
    const fence = model.dagGroups.find((g) => g.dagName === 'Fence')!;
    expect(fence.isEngine).toBe(true);
    expect(fence.trackName).toBe('Engine-Post');
    const world = model.dagGroups.find((g) => g.dagName === 'World')!;
    expect(world.isEngine).toBe(false);
  });

  it('orders lanes by track order → DAG order → DAG-local phase order', () => {
    const t = topoH(
      [
        sys({ name: 'Render1', phaseName: 'Render', dagId: 0 }),
        sys({ name: 'In1', phaseName: 'Input', dagId: 0 }),
        sys({ name: 'Sim1', phaseName: 'Simulation', dagId: 0 }),
        sys({ name: 'FencePrep', phaseName: 'Default', dagId: 1 }),
      ],
      worldAndFenceTracks(),
    );
    const model = buildDagModel(t, 'horizontal-lanes', { showEngineTracks: true });
    // World phases in DAG-local declared order, then the Fence DAG's single phase.
    expect(model.lanes.map((l) => l.name)).toEqual(['Input', 'Simulation', 'Render', 'Default']);
    expect(model.lanes.map((l) => l.dagName)).toEqual(['World', 'World', 'World', 'Fence']);
  });

  it('respects track orderIndex even when the tracks array is unsorted', () => {
    const tracks = [
      track('Engine-Post', 2, ['engine'], [dag(1, 'Fence', ['Default'])]),
      track('Public', 1, [], [dag(0, 'World', ['Simulation'])]),
    ];
    const t = topoH(
      [
        sys({ name: 'Movement', phaseName: 'Simulation', dagId: 0 }),
        sys({ name: 'FencePrep', phaseName: 'Default', dagId: 1 }),
      ],
      tracks,
    );
    const model = buildDagModel(t, 'horizontal-lanes', { showEngineTracks: true });
    // Public (orderIndex 1) before Engine-Post (orderIndex 2), regardless of array order.
    expect(model.dagGroups.map((g) => g.dagName)).toEqual(['World', 'Fence']);
  });

  it('keys lanes uniquely per DAG even when two DAGs share a phase name', () => {
    const tracks = [
      track('Public', 1, [], [
        dag(0, 'Alpha', ['Sim']),
        dag(1, 'Beta', ['Sim']),
      ]),
    ];
    const t = topoH(
      [
        sys({ name: 'A', phaseName: 'Sim', dagId: 0 }),
        sys({ name: 'B', phaseName: 'Sim', dagId: 1 }),
      ],
      tracks,
    );
    const model = buildDagModel(t);
    expect(model.lanes.map((l) => l.id)).toEqual(['0::Sim', '1::Sim']);
    expect(new Set(model.lanes.map((l) => l.id)).size).toBe(2);
  });

  it('falls back to flat phase grouping when the topology carries no tracks', () => {
    // topo() sets tracks: [] — the pre-#354 flat behaviour must be preserved, with no DAG groups.
    const model = buildDagModel(topo([
      sys({ name: 'In1', phaseName: 'Input' }),
      sys({ name: 'Sim1', phaseName: 'Simulation' }),
    ]));
    expect(model.lanes.map((l) => l.name)).toEqual(['Input', 'Simulation']);
    expect(model.dagGroups).toEqual([]);
  });
});

// ── Engine-aware "no declared access" classification (3C) ─────────────────
// Engine-internal systems (Fence DAG on the Engine-Post track) declare no RFC 07 access by design;
// distinguishing that from a genuinely old trace is what resolveNoAccessReason exists for.

describe('toNodeData — dagId passthrough', () => {
  it('carries the owning dagId so a consumer can resolve the system\'s track', () => {
    expect(toNodeData(sys({ name: 'FencePrep', dagId: 1 })).dagId).toBe(1);
    expect(toNodeData(sys({ name: 'Movement', dagId: 0 })).dagId).toBe(0);
  });
});

describe('resolveNoAccessReason', () => {
  it('classifies an engine-track system as engine-internal', () => {
    const t = topoH(
      [
        sys({ name: 'Movement', dagId: 0, writes: ['Position'] }),
        sys({ name: 'FencePrep', dagId: 1 }),
      ],
      worldAndFenceTracks(),
    );
    expect(resolveNoAccessReason(t, 1)).toBe('engine-internal');
  });

  it('engine wins even when the whole trace is access-free — never blames the trace version', () => {
    const t = topoH([sys({ name: 'FencePrep', dagId: 1 })], worldAndFenceTracks());
    expect(resolveNoAccessReason(t, 1)).toBe('engine-internal');
  });

  it('classifies a user system as declares-none when the trace carries access elsewhere', () => {
    const t = topoH(
      [
        sys({ name: 'Movement', dagId: 0, writes: ['Position'] }),
        sys({ name: 'Quiet', dagId: 0 }),
      ],
      worldAndFenceTracks(),
    );
    expect(topologyHasAnyAccess(t)).toBe(true);
    expect(resolveNoAccessReason(t, 0)).toBe('declares-none');
  });

  it('classifies as trace-empty only when NO system in the trace declares any access', () => {
    const t = topoH(
      [
        sys({ name: 'Movement', dagId: 0 }),
        sys({ name: 'Quiet', dagId: 0 }),
      ],
      worldAndFenceTracks(),
    );
    expect(topologyHasAnyAccess(t)).toBe(false);
    expect(resolveNoAccessReason(t, 0)).toBe('trace-empty');
  });

  it('counts any of the nine access lanes as "has access" (events / resources included)', () => {
    expect(topologyHasAnyAccess(topo([sys({ name: 'E', readsEvents: ['Damage'] })]))).toBe(true);
    expect(topologyHasAnyAccess(topo([sys({ name: 'R', writesResources: ['world.physics'] })]))).toBe(true);
    expect(topologyHasAnyAccess(topo([sys({ name: 'N', additionalReads: ['Hidden'] })]))).toBe(false); // not a surfaced lane
  });
});
