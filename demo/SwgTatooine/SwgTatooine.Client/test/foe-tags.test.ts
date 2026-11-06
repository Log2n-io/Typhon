import { describe, expect, it } from 'vitest';
import { FOE_TAG_FADE_FROM_M, FOE_TAG_MAX, FOE_TAG_RANGE_M, foeTagOpacity, hostilityOf, NearestPicker } from '../src/data/foe-tags';

describe('a potential enemy', () => {
  it('is aggressive with an aggro radius, passive without one or with the field absent', () => {
    expect(hostilityOf(24)).toBe('aggressive');
    expect(hostilityOf(0)).toBe('passive');
    expect(hostilityOf(Number.NaN)).toBe('passive');
  });
});

describe('the nearest picker', () => {
  it('keeps the nearest few, nearest first, whatever the order they come in', () => {
    const picker = new NearestPicker(3);
    for (const [id, d2] of [
      [1, 50],
      [2, 10],
      [3, 90],
      [4, 5],
      [5, 70],
      [6, 20],
    ]) {
      picker.offer(id, d2);
    }

    expect(picker.count).toBe(3);
    expect([picker.idAt(0), picker.idAt(1), picker.idAt(2)]).toEqual([4, 2, 6]);
  });

  it('forgets on reset, and holds fewer than its cap', () => {
    const picker = new NearestPicker(FOE_TAG_MAX);
    picker.offer(7, 1);
    picker.reset();
    picker.offer(8, 4);
    picker.offer(9, 2);
    expect(picker.count).toBe(2);
    expect([picker.idAt(0), picker.idAt(1)]).toEqual([9, 8]);
  });
});

describe("a foe tag's fade", () => {
  it('is whole up to 75 m, eases out to nothing at the range, and never fades one hunting the avatar', () => {
    expect(foeTagOpacity(10, false)).toBe(1);
    expect(foeTagOpacity(FOE_TAG_FADE_FROM_M, false)).toBe(1);
    expect(foeTagOpacity((FOE_TAG_FADE_FROM_M + FOE_TAG_RANGE_M) / 2, false)).toBeCloseTo(0.5, 5);
    expect(foeTagOpacity(FOE_TAG_RANGE_M, false)).toBe(0);
    expect(foeTagOpacity(FOE_TAG_RANGE_M + 50, false)).toBe(0);
    expect(foeTagOpacity(FOE_TAG_RANGE_M, true)).toBe(1);
    let last = 1;
    for (let d = FOE_TAG_FADE_FROM_M; d <= FOE_TAG_RANGE_M; d += 5) {
      const o = foeTagOpacity(d, false);
      expect(o).toBeLessThanOrEqual(last);
      last = o;
    }
  });
});
