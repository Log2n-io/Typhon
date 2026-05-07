import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { ResourceNodeDto } from '@/api/generated/model/resourceNodeDto';
import { installSelectionBridges } from '../selectionBridges';
import { useProfilerSelectionStore, type ProfilerSelection } from '../useProfilerSelectionStore';
import { useProfilerViewStore } from '../useProfilerViewStore';
import { useSchemaInspectorStore } from '../useSchemaInspectorStore';
import { useSelectedResourceStore, type SelectedResource } from '../useSelectedResourceStore';
import { useSelectionStore } from '../useSelectionStore';

let stopBridges: () => void;

beforeEach(() => {
  useSelectionStore.getState().clear();
  useProfilerSelectionStore.setState({ selected: null, touchedAt: 0 });
  useProfilerViewStore.getState().setViewRange({ startUs: 0, endUs: 0 });
  useSelectedResourceStore.setState({ selected: null, touchedAt: 0 });
  useSchemaInspectorStore.getState().reset();
  stopBridges = installSelectionBridges();
});

afterEach(() => {
  stopBridges();
});

describe('profiler viewRange ↔ selection.time', () => {
  it('publishes profiler viewRange changes to selection.time', () => {
    useProfilerViewStore.getState().setViewRange({ startUs: 1000, endUs: 5000 });
    expect(useSelectionStore.getState().time).toEqual({ start: 1000, end: 5000 });
  });

  it('does NOT publish the {0,0} sentinel as a clear when unified.time is null', () => {
    // Sentinel arrives on every metadata reset in trace mode. With no cross-panel time set,
    // it stays profiler-internal — unified.time stays null, no URL write.
    useProfilerViewStore.getState().setViewRange({ startUs: 0, endUs: 0 });
    expect(useSelectionStore.getState().time).toBeNull();
  });

  it('reasserts a deep-linked unified.time when profiler resets to {0,0}', () => {
    // C1: ProfilerPanel writes {0,0} on metadata arrival. With a URL deep-linked range in the
    // unified store, the bridge must override the reset by pushing the range back into viewRange.
    useSelectionStore.getState().setTime({ start: 120_000, end: 134_000 });
    expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 120_000, endUs: 134_000 });

    // Simulate the metadata-arrival reset in ProfilerPanel.
    useProfilerViewStore.getState().setViewRange({ startUs: 0, endUs: 0 });

    // Bridge must have re-pushed the unified.time back into viewRange.
    expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 120_000, endUs: 134_000 });
    // And unified.time must be unchanged (sentinel does not clear).
    expect(useSelectionStore.getState().time).toEqual({ start: 120_000, end: 134_000 });
  });

  it('writes selection.time changes back into profiler viewRange', () => {
    useSelectionStore.getState().setTime({ start: 2000, end: 8000 });
    expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 2000, endUs: 8000 });
  });

  it('does not feedback-loop when both stores agree', () => {
    let viewWrites = 0;
    let selWrites = 0;
    const offV = useProfilerViewStore.subscribe((s, p) => {
      if (s.viewRange !== p.viewRange) viewWrites++;
    });
    const offS = useSelectionStore.subscribe((s, p) => {
      if (s.time !== p.time) selWrites++;
    });

    useProfilerViewStore.getState().setViewRange({ startUs: 100, endUs: 200 });
    expect(viewWrites).toBe(1);
    expect(selWrites).toBe(1);

    useSelectionStore.getState().setTime({ start: 100, end: 200 });
    // Equal value; both stores stay quiet.
    expect(viewWrites).toBe(1);
    expect(selWrites).toBe(1);

    offV();
    offS();
  });

  it('selection.time = null does not clobber profiler viewRange', () => {
    useProfilerViewStore.getState().setViewRange({ startUs: 1000, endUs: 5000 });
    useSelectionStore.getState().setTime(null);
    // Bridge ignores null in the unified→profiler direction.
    expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 1000, endUs: 5000 });
  });
});

describe('profiler selection ↔ selection.focusTick', () => {
  it('mirrors a tick selection to focusTick', () => {
    const sel: ProfilerSelection = { kind: 'tick', tickNumber: 42 };
    useProfilerSelectionStore.getState().setSelected(sel);
    expect(useSelectionStore.getState().focusTick).toBe(42);
  });

  it('mirrors a phase selection to focusTick', () => {
    const sel: ProfilerSelection = {
      kind: 'phase',
      tickNumber: 7,
      phase: { kind: 'phase', phaseId: 'WriteTickFence', startUs: 0, endUs: 100 } as never,
    };
    useProfilerSelectionStore.getState().setSelected(sel);
    expect(useSelectionStore.getState().focusTick).toBe(7);
  });

  it('does not move focusTick for span/chunk/marker selections', () => {
    const sel: ProfilerSelection = {
      kind: 'span',
      span: { name: 'Tx.Commit', startUs: 0, endUs: 100, threadSlot: 0, depth: 0 } as never,
    };
    useProfilerSelectionStore.getState().setSelected(sel);
    expect(useSelectionStore.getState().focusTick).toBeNull();
  });
});

describe('selected resource ↔ selection.resource', () => {
  it('mirrors resourceId to selection.resource', () => {
    const raw: ResourceNodeDto = {
      id: 'storage/paged-mmf',
      name: 'PagedMMF',
      type: 'Segment',
      entityCount: null,
      children: [],
    };
    const sample: SelectedResource = {
      resourceId: 'storage/paged-mmf',
      kind: 'Segment',
      name: 'PagedMMF',
      path: ['storage', 'paged-mmf'],
      raw,
    };
    useSelectedResourceStore.getState().setSelected(sample);
    expect(useSelectionStore.getState().resource).toBe('storage/paged-mmf');
  });

  it('clears selection.resource when the per-panel store clears', () => {
    useSelectedResourceStore.setState({
      selected: {
        resourceId: 'x',
        kind: 'k',
        name: 'n',
        path: ['x'],
        raw: { id: 'x', name: 'n', type: 'k', entityCount: null, children: [] } as ResourceNodeDto,
      },
      touchedAt: 1,
    });
    expect(useSelectionStore.getState().resource).toBe('x');
    useSelectedResourceStore.getState().clear();
    expect(useSelectionStore.getState().resource).toBeNull();
  });
});

describe('schema inspector ↔ selection.component', () => {
  it('mirrors component selection both ways', () => {
    useSchemaInspectorStore.getState().selectComponent('Position');
    expect(useSelectionStore.getState().component).toBe('Position');

    useSelectionStore.getState().setComponent('Velocity');
    expect(useSchemaInspectorStore.getState().selectedComponentType).toBe('Velocity');
  });

  it('clearing the unified slot clears the legacy store', () => {
    useSchemaInspectorStore.getState().selectComponent('Position');
    useSelectionStore.getState().setComponent(null);
    expect(useSchemaInspectorStore.getState().selectedComponentType).toBeNull();
  });
});
