import { describe, expect, it } from 'vitest';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { TopologyDto } from '@/api/generated/model/topologyDto';
import { LANE_GAP, buildDagModel } from '../dagModel';

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

function topo(systems: SystemDefinitionDto[], phases: string[] = ['Input', 'Simulation', 'Output']): TopologyDto {
  return {
    systems,
    archetypes: [],
    componentTypes: [],
    phases,
    componentFamilies: { componentToFamily: {}, familyOrder: [] },
  };
}

describe('buildDagModel', () => {
  it('returns an empty model for null/empty topology', () => {
    expect(buildDagModel(null)).toEqual({ nodes: [], edges: [], lanes: [], width: 0, height: 0 });
    expect(buildDagModel(topo([]))).toEqual({ nodes: [], edges: [], lanes: [], width: 0, height: 0 });
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
