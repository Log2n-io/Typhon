import { beforeEach, describe, expect, it } from 'vitest';
import { getOverride, useNodePositionsStore } from '../useNodePositionsStore';

beforeEach(() => {
  useNodePositionsStore.setState({ overrides: {} });
});

describe('useNodePositionsStore', () => {
  it('round-trips a single override', () => {
    useNodePositionsStore.getState().setOverride('horizontal-lanes', 'MoveAll', { x: 100, y: 200 });
    expect(useNodePositionsStore.getState().overrides['horizontal-lanes|MoveAll']).toEqual({ x: 100, y: 200 });
  });

  it('isolates overrides by layout — same system, different layouts, separate slots', () => {
    const { setOverride, overrides } = useNodePositionsStore.getState();
    setOverride('horizontal-lanes', 'MoveAll', { x: 1, y: 2 });
    setOverride('circular', 'MoveAll', { x: 3, y: 4 });
    const next = useNodePositionsStore.getState().overrides;
    expect(next['horizontal-lanes|MoveAll']).toEqual({ x: 1, y: 2 });
    expect(next['circular|MoveAll']).toEqual({ x: 3, y: 4 });
    // Sanity: starting state was empty.
    expect(Object.keys(overrides).length).toBe(0);
  });

  it('clearLayout drops only the targeted layout', () => {
    const { setOverride, clearLayout } = useNodePositionsStore.getState();
    setOverride('horizontal-lanes', 'A', { x: 1, y: 1 });
    setOverride('horizontal-lanes', 'B', { x: 2, y: 2 });
    setOverride('circular', 'A', { x: 3, y: 3 });
    clearLayout('horizontal-lanes');
    const next = useNodePositionsStore.getState().overrides;
    expect(next['horizontal-lanes|A']).toBeUndefined();
    expect(next['horizontal-lanes|B']).toBeUndefined();
    expect(next['circular|A']).toEqual({ x: 3, y: 3 });
  });

  it('clearAll empties the map', () => {
    const { setOverride, clearAll } = useNodePositionsStore.getState();
    setOverride('horizontal-lanes', 'A', { x: 1, y: 1 });
    setOverride('circular', 'B', { x: 2, y: 2 });
    clearAll();
    expect(Object.keys(useNodePositionsStore.getState().overrides).length).toBe(0);
  });

  it('countForLayout returns the number of overrides for that layout only', () => {
    const { setOverride, countForLayout } = useNodePositionsStore.getState();
    setOverride('horizontal-lanes', 'A', { x: 1, y: 1 });
    setOverride('horizontal-lanes', 'B', { x: 2, y: 2 });
    setOverride('circular', 'A', { x: 3, y: 3 });
    expect(countForLayout('horizontal-lanes')).toBe(2);
    expect(countForLayout('circular')).toBe(1);
    expect(countForLayout('compact')).toBe(0);
  });

  it('getOverride helper resolves only the (layout, system) pair', () => {
    useNodePositionsStore.getState().setOverride('horizontal-lanes', 'MoveAll', { x: 7, y: 8 });
    const { overrides } = useNodePositionsStore.getState();
    expect(getOverride(overrides, 'horizontal-lanes', 'MoveAll')).toEqual({ x: 7, y: 8 });
    expect(getOverride(overrides, 'horizontal-lanes', 'OtherSystem')).toBeUndefined();
    expect(getOverride(overrides, 'circular', 'MoveAll')).toBeUndefined();
  });
});
