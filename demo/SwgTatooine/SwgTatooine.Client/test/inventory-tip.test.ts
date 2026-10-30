import { describe, expect, it } from 'vitest';

import { tipPosition } from '../src/ui/InventoryWindow';

/** A 40 px slot. */
const slot = (left: number, top: number) => ({ left, top, width: 40, bottom: top + 40 });

describe('the inventory tooltip', () => {
  it('sits centred above its slot when there is room', () => {
    expect(tipPosition(slot(500, 300), 120, 20, 1920)).toEqual({ left: 460, top: 276 });
  });

  it('is pushed inside the viewport at the left edge instead of being cut (the clipped "…and · Mk I · ×30")', () => {
    expect(tipPosition(slot(10, 300), 120, 20, 1920).left).toBe(4);
  });

  it('is pushed inside the viewport at the right edge', () => {
    expect(tipPosition(slot(1890, 300), 120, 20, 1920).left).toBe(1920 - 120 - 4);
  });

  it('goes below the slot when the slot is at the top of the window', () => {
    expect(tipPosition(slot(500, 10), 120, 20, 1920).top).toBe(54);
  });
});
