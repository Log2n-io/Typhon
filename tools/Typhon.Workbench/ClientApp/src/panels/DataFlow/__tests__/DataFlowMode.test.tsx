// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { TopologyDto } from '@/api/generated/model/topologyDto';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useDataFlowViewStore } from '../useDataFlowViewStore';
import DataFlowToolbar from '../DataFlowToolbar';
import DataFlowMatrix from '../DataFlowMatrix';

/**
 * Stage 3 Phase 3 (3A) — the Data Flow ⊕ Access Matrix consolidation. These cover the merge without touching the
 * uPlot Timeline (canvas, not jsdom-mountable): the unified store, the toolbar's Timeline/Matrix toggle + its
 * mode-conditional controls, and that the Matrix mode reads/writes the *shared bus* `system` slot — which is what
 * makes selection survive a mode toggle (both modes read the same slot).
 */

function topo(overrides: Partial<TopologyDto> = {}): TopologyDto {
  return {
    systems: [],
    archetypes: [],
    componentTypes: [],
    phases: [],
    tracks: [],
    componentFamilies: { componentToFamily: {}, familyOrder: [] },
    ...overrides,
  };
}

function sys(name: string, index: number, extras: Partial<SystemDefinitionDto> = {}): SystemDefinitionDto {
  return {
    index, name, type: 0, priority: 0, isParallel: true, tierFilter: 0,
    predecessors: [], successors: [], phaseName: 'Sim', isExclusivePhase: false,
    reads: [], readsFresh: [], readsSnapshot: [], additionalReads: [],
    writes: [], sideWrites: [], writesEvents: [], readsEvents: [],
    writesResources: [], readsResources: [], explicitAfter: [], explicitBefore: [],
    dagId: 0,
    ...extras,
  };
}

const SAMPLE_TOPOLOGY = topo({
  phases: ['Sim'],
  componentTypes: [{ componentTypeId: 1, name: 'Position' }],
  systems: [sys('Phys', 1, { writes: ['Position'] }), sys('AI', 2, { reads: ['Position'] })],
});

beforeEach(() => {
  // Reset the persisted view store to defaults so a prior test's mode/sort doesn't leak.
  useDataFlowViewStore.setState({ mode: 'timeline', rowSort: 'topology', colSort: 'phase-then-dependency' });
});
afterEach(() => {
  cleanup();
  useSelectionStore.getState().clear();
});

describe('useDataFlowViewStore — unified mode + matrix sort (3A.1)', () => {
  it('defaults to Timeline mode with the matrix sort defaults folded in', () => {
    const s = useDataFlowViewStore.getState();
    expect(s.mode).toBe('timeline');
    expect(s.rowSort).toBe('topology');
    expect(s.colSort).toBe('phase-then-dependency');
  });

  it('setMode toggles the view mode', () => {
    useDataFlowViewStore.getState().setMode('matrix');
    expect(useDataFlowViewStore.getState().mode).toBe('matrix');
    useDataFlowViewStore.getState().setMode('timeline');
    expect(useDataFlowViewStore.getState().mode).toBe('timeline');
  });

  it('carries the matrix row/column sort setters', () => {
    useDataFlowViewStore.getState().setRowSort('cluster');
    useDataFlowViewStore.getState().setColSort('cluster');
    expect(useDataFlowViewStore.getState().rowSort).toBe('cluster');
    expect(useDataFlowViewStore.getState().colSort).toBe('cluster');
  });
});

describe('DataFlowToolbar — Timeline/Matrix toggle (3A.3)', () => {
  it('switches mode and swaps mode-specific controls', () => {
    render(<DataFlowToolbar />);
    // Timeline mode (default): X-axis + Aggregate present; matrix sorts absent.
    expect(screen.getByText('X-axis')).toBeTruthy();
    expect(screen.queryByText('Rows')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Matrix' }));

    expect(useDataFlowViewStore.getState().mode).toBe('matrix');
    // Matrix mode: row/column sort appear; the timeline-only X-axis control is gone.
    expect(screen.getByText('Rows')).toBeTruthy();
    expect(screen.getByText('Columns')).toBeTruthy();
    expect(screen.queryByText('X-axis')).toBeNull();
  });
});

describe('DataFlowMatrix — reads & writes the shared bus (3A.2: selection survives the toggle)', () => {
  it('highlights the bus-selected system (so a Timeline selection is still lit in Matrix)', () => {
    useSelectionStore.getState().setSystem('Phys');
    render(<DataFlowMatrix topology={SAMPLE_TOPOLOGY} granularityLevel="L3" touchSlice={[]} />);
    expect(screen.getByTestId('access-matrix-system-Phys').getAttribute('aria-pressed')).toBe('true');
    expect(screen.getByTestId('access-matrix-system-AI').getAttribute('aria-pressed')).toBe('false');
  });

  it('clicking a system writes the bus (so switching back to Timeline keeps it)', () => {
    render(<DataFlowMatrix topology={SAMPLE_TOPOLOGY} granularityLevel="L3" touchSlice={[]} />);
    fireEvent.click(screen.getByTestId('access-matrix-system-AI'));
    expect(useSelectionStore.getState().system).toBe('AI');
  });
});
