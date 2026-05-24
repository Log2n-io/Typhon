import { describe, expect, it } from 'vitest';
import { categoricalColor, categoricalHue, hashId, type Rgb } from '@/libs/color/categorical';
import { onColor, segmentRgb } from '@/libs/dbmap/dbMapColors';

// Conformance suite G — DS-2/3 color. The shared categorical (stable hue-per-object) util + the onColor AA
// pairing. onColor's own contrast laws live in dbMapColors.test; here we prove determinism, cross-view
// stability, the DS-2 consolidation (the File Map's segment hue now IS the shared util), and AA over it.

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

describe('suite G — categoricalColor (DS-2 stable hue-per-object)', () => {
  it('is deterministic: the same id always yields the same color', () => {
    expect(categoricalColor('Position')).toEqual(categoricalColor('Position'));
    expect(categoricalColor(42)).toEqual(categoricalColor(42));
  });

  it('gives distinct ids distinct hues (cross-view identity, not collisions)', () => {
    expect(categoricalHue('Position')).not.toBe(categoricalHue('Velocity'));
    const hues = new Set<number>();
    for (const id of ['Position', 'Velocity', 'Health', 'Sprite', 'Transform', 'Name']) {
      hues.add(Math.round(categoricalHue(id)));
    }
    expect(hues.size).toBe(6); // no collisions among a typical component set
  });

  it('hashId maps a number to itself and a string deterministically', () => {
    expect(hashId(7)).toBe(7);
    expect(hashId('abc')).toBe(hashId('abc'));
    expect(hashId('abc')).not.toBe(hashId('abd'));
  });

  it('produces a hue in [0, 360)', () => {
    for (const id of [0, 1, 5, 99, 'x', 'LongComponentTypeName']) {
      const h = categoricalHue(id);
      expect(h).toBeGreaterThanOrEqual(0);
      expect(h).toBeLessThan(360);
    }
  });

  it('DS-2 consolidation: the File Map segment hue IS the shared util (no parallel palette)', () => {
    for (let id = 0; id < 32; id++) {
      expect(segmentRgb(id)).toEqual(categoricalColor(id));
    }
  });
});

describe('suite G — onColor over categorical backgrounds (DS-3 AA pairing)', () => {
  it('picks an ink clearing the WCAG floor over every categorical color', () => {
    for (let id = 0; id < 64; id++) {
      const bg = categoricalColor(id);
      expect(contrastRatio(onColor(bg), bg)).toBeGreaterThanOrEqual(3.0);
    }
  });
});
