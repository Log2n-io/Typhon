import { describe, expect, it } from 'vitest';
import {
  allocationRgb,
  contentCellRgb,
  DISABLED_RGB,
  enabledOverlayRgb,
  ENABLED_RGB,
  fillDensityRgb,
  FREE_RGB,
  USED_RGB,
} from '../dbMapColors';

// Colour resolution for the L4 content cells and the L3/occupancy fill ramp (Module 15, A6).

describe('contentCellRgb — entitySlot (A6 cluster sub-grid)', () => {
  it('lights an occupied slot (colorKey > 0) with the used colour', () => {
    expect(contentCellRgb('entitySlot', 1)).toEqual(USED_RGB);
  });

  it('darkens a free slot (colorKey 0) with the free colour', () => {
    expect(contentCellRgb('entitySlot', 0)).toEqual(FREE_RGB);
  });
});

describe('allocationRgb — occupancy used/free ramp (A6 §10.2)', () => {
  it('reads free (dark slate) at 0 and allocated (cyan) at 1', () => {
    expect(allocationRgb(0)).toEqual(FREE_RGB);
    expect(allocationRgb(1)).toEqual(USED_RGB);
  });
});

describe('enabledOverlayRgb — per-component overlay (A6 §10.1)', () => {
  it('leaves a free slot dark regardless of the enabled flag', () => {
    expect(enabledOverlayRgb(false, true)).toEqual(FREE_RGB);
    expect(enabledOverlayRgb(false, false)).toEqual(FREE_RGB);
  });

  it('greens an occupied slot whose component is enabled', () => {
    expect(enabledOverlayRgb(true, true)).toEqual(ENABLED_RGB);
  });

  it('dims an occupied slot whose component is disabled', () => {
    expect(enabledOverlayRgb(true, false)).toEqual(DISABLED_RGB);
  });
});

describe('fillDensityRgb — intra-chunk / occupancy fill ramp', () => {
  it('reads dark slate at empty and amber at full', () => {
    expect(fillDensityRgb(0)).toEqual([30, 41, 59]);
    expect(fillDensityRgb(1)).toEqual([245, 158, 11]);
  });

  it('clamps out-of-range ratios', () => {
    expect(fillDensityRgb(-1)).toEqual(fillDensityRgb(0));
    expect(fillDensityRgb(2)).toEqual(fillDensityRgb(1));
  });
});
