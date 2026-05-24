import { describe, expect, it } from 'vitest';
import {
  allocationRgb,
  contentCellRgb,
  DISABLED_RGB,
  enabledOverlayRgb,
  ENABLED_RGB,
  fillDensityRgb,
  FREE_RGB,
  onColor,
  PAGE_TYPE_RGB,
  segmentRgb,
  USED_RGB,
  type Rgb,
} from '../dbMapColors';

// WCAG contrast ratio between two sRGB colours — for asserting the DS-3 onColor() picks the legible ink.
function contrastRatio(a: Rgb, b: Rgb): number {
  const lum = (c: Rgb) => {
    const ch = (v: number) => {
      const s = v / 255;
      return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
    };
    return 0.2126 * ch(c[0]) + 0.7152 * ch(c[1]) + 0.0722 * ch(c[2]);
  };
  const la = lum(a);
  const lb = lum(b);
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
}

describe('onColor (DS-3)', () => {
  const WHITE: Rgb = [255, 255, 255];
  const NEAR_BLACK: Rgb = [17, 24, 39];

  it('returns white over a dark background and near-black over a light one', () => {
    expect(onColor([15, 23, 42])).toEqual(WHITE);
    expect(onColor([245, 245, 245])).toEqual(NEAR_BLACK);
  });

  it('always picks the higher-contrast of the two inks for every categorical colour', () => {
    for (const bg of PAGE_TYPE_RGB) {
      const ink = onColor(bg);
      const other = ink === WHITE ? NEAR_BLACK : WHITE;
      expect(contrastRatio(ink, bg)).toBeGreaterThanOrEqual(contrastRatio(other, bg));
    }
  });

  it('keeps text legible (≥3:1 floor, optimal ink) over every categorical + segment colour', () => {
    // Two inks (white / near-black) cannot reach 4.5:1 over a ~0.19-luminance saturated cell (the two-ink
    // contrast minimum is ~4.2) — a fundamental limit, not a defect. We guarantee the WCAG UI-component /
    // large-text floor (3:1) over every cell colour, with the higher-contrast ink (the optimality test above).
    const samples: Rgb[] = [...PAGE_TYPE_RGB];
    for (let id = 0; id < 64; id++) {
      samples.push(segmentRgb(id));
    }
    for (const bg of samples) {
      expect(contrastRatio(onColor(bg), bg)).toBeGreaterThanOrEqual(3.0);
    }
  });
});

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
