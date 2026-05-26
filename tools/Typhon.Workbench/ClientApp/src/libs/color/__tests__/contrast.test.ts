import { describe, expect, it } from 'vitest';
import {
  onColor,
  onColorCss,
  rgbCss,
  rgbToHex,
  relativeLuminance,
  relativeLuminanceHex,
  lighten,
  darken,
  pickTextColorFor,
} from '@/libs/color/contrast';
import type { Rgb } from '@/libs/color/categorical';

// DS-3 contrast base — the single home for onColor/luminance + the hex mix helpers (consolidated from the old
// libs/colors.ts + the private copies in dbMapColors.ts). These laws used to live in dbMapColors.test.

function contrastRatio(a: Rgb, b: Rgb): number {
  const la = relativeLuminance(a);
  const lb = relativeLuminance(b);
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
}

describe('contrast — onColor (DS-3 ink over a dynamic background)', () => {
  it('picks white over a dark bg, near-black over a light bg', () => {
    expect(onColor([15, 23, 42])).toEqual([255, 255, 255]);
    expect(onColor([245, 245, 245])).toEqual([17, 24, 39]);
  });

  it('always clears the WCAG UI floor (≥3:1) and is the optimal of the two inks', () => {
    const bgs: Rgb[] = [[0, 0, 0], [255, 255, 255], [128, 128, 128], [200, 50, 50], [20, 120, 200], [240, 230, 60]];
    for (const bg of bgs) {
      const ink = onColor(bg);
      const other: Rgb = ink[0] === 255 ? [17, 24, 39] : [255, 255, 255];
      expect(contrastRatio(ink, bg)).toBeGreaterThanOrEqual(3.0);
      expect(contrastRatio(ink, bg)).toBeGreaterThanOrEqual(contrastRatio(other, bg));
    }
  });

  it('rgbCss / onColorCss format as CSS rgb()', () => {
    expect(rgbCss([1, 2, 3])).toBe('rgb(1, 2, 3)');
    expect(onColorCss([0, 0, 0])).toBe('rgb(255, 255, 255)');
  });
});

describe('contrast — hex helpers', () => {
  it('lighten/darken bounds: 0 = identity, 1 = white/black', () => {
    expect(lighten('#3366cc', 0)).toBe('#3366cc');
    expect(lighten('#3366cc', 1)).toBe('#ffffff');
    expect(darken('#3366cc', 0)).toBe('#3366cc');
    expect(darken('#3366cc', 1)).toBe('#000000');
  });

  it('pickTextColorFor: dark ink on a light bg, light ink on a dark bg', () => {
    expect(pickTextColorFor('#ffffff')).toBe('#000');
    expect(pickTextColorFor('#000000')).toBe('#fff');
  });

  it('rgbToHex formats a triple as #rrggbb', () => {
    expect(rgbToHex([255, 0, 128])).toBe('#ff0080');
    expect(rgbToHex([1, 2, 3])).toBe('#010203');
  });

  it('relativeLuminance (Rgb) and relativeLuminanceHex agree at grayscale', () => {
    for (const v of [0, 64, 128, 200, 255]) {
      expect(relativeLuminanceHex(rgbToHex([v, v, v]))).toBeCloseTo(relativeLuminance([v, v, v]), 6);
    }
  });
});
