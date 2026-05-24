import { beforeEach, describe, expect, it } from 'vitest';
import { useDensityStore, DENSITY_ROW_HEIGHT } from '@/stores/useDensityStore';

// Conformance suite H — DS-1 density (3 modes: compact / normal / comfortable). A density change must move the
// row-height token that virtualized lists/trees read for `estimateSize` / `rowHeight` (and the --fs-* font ramp,
// covered in CSS), so they re-measure.

beforeEach(() => {
  useDensityStore.setState({ mode: 'normal' });
});

describe('suite H — density', () => {
  it('defaults to normal', () => {
    expect(useDensityStore.getState().mode).toBe('normal');
  });

  it('cycles compact → normal → comfortable → compact', () => {
    useDensityStore.setState({ mode: 'compact' });
    const { cycle } = useDensityStore.getState();
    cycle();
    expect(useDensityStore.getState().mode).toBe('normal');
    cycle();
    expect(useDensityStore.getState().mode).toBe('comfortable');
    cycle();
    expect(useDensityStore.getState().mode).toBe('compact');
  });

  it('setMode selects a specific mode', () => {
    useDensityStore.getState().setMode('comfortable');
    expect(useDensityStore.getState().mode).toBe('comfortable');
  });

  it('row height moves with density (the value estimateSize/rowHeight reads)', () => {
    expect(DENSITY_ROW_HEIGHT.compact).toBe(22);
    expect(DENSITY_ROW_HEIGHT.normal).toBe(25);
    expect(DENSITY_ROW_HEIGHT.comfortable).toBe(28);
    expect(DENSITY_ROW_HEIGHT[useDensityStore.getState().mode]).toBe(25); // normal default
    useDensityStore.getState().setMode('compact');
    expect(DENSITY_ROW_HEIGHT[useDensityStore.getState().mode]).toBe(22);
  });

  it('a list keyed on the row height re-measures when density changes', () => {
    // Simulates a virtualizer reading the token in estimateSize: the closure value changes on a mode switch,
    // so a re-render with the new height re-measures every row.
    const estimateSize = () => DENSITY_ROW_HEIGHT[useDensityStore.getState().mode];
    expect(estimateSize()).toBe(25);
    useDensityStore.getState().setMode('comfortable');
    expect(estimateSize()).toBe(28);
  });
});
