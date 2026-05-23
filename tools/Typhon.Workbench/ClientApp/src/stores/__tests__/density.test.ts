import { beforeEach, describe, expect, it } from 'vitest';
import { useDensityStore, DENSITY_ROW_HEIGHT } from '@/stores/useDensityStore';

// Conformance suite H — DS-1 density. A density change must move the row-height token that virtualized
// lists/trees read for `estimateSize` / `rowHeight`, so they re-measure.

beforeEach(() => {
  useDensityStore.setState({ mode: 'compact' });
});

describe('suite H — density', () => {
  it('toggles between compact and comfortable', () => {
    expect(useDensityStore.getState().mode).toBe('compact');
    useDensityStore.getState().toggle();
    expect(useDensityStore.getState().mode).toBe('comfortable');
    useDensityStore.getState().toggle();
    expect(useDensityStore.getState().mode).toBe('compact');
  });

  it('row height moves with density (the value estimateSize/rowHeight reads)', () => {
    expect(DENSITY_ROW_HEIGHT.compact).toBe(22);
    expect(DENSITY_ROW_HEIGHT.comfortable).toBe(28);
    expect(DENSITY_ROW_HEIGHT[useDensityStore.getState().mode]).toBe(22);
    useDensityStore.getState().setMode('comfortable');
    expect(DENSITY_ROW_HEIGHT[useDensityStore.getState().mode]).toBe(28);
  });

  it('a list keyed on the row height re-measures when density changes', () => {
    // Simulates a virtualizer reading the token in estimateSize: the closure value changes on toggle,
    // so a re-render with the new height re-measures every row.
    const estimateSize = () => DENSITY_ROW_HEIGHT[useDensityStore.getState().mode];
    expect(estimateSize()).toBe(22);
    useDensityStore.getState().toggle();
    expect(estimateSize()).toBe(28);
  });
});
