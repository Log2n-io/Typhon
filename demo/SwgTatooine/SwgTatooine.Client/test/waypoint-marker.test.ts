import { describe, expect, it } from 'vitest';
import { EDGE_MARGIN_PX, edgeArrow } from '../src/render/waypoint-marker';

/** The active waypoint's arrow at the screen's edge (client.md C38, U5): none on screen; at the edge towards it off screen; by its bearing behind. */

describe('the edge arrow', () => {
  const W = 1000;
  const H = 600;

  it('is not drawn while the waypoint is on screen', () => {
    expect(edgeArrow(500, 300, 10, W, H, 0)).toBeNull();
    expect(edgeArrow(EDGE_MARGIN_PX + 1, H - EDGE_MARGIN_PX - 1, 10, W, H, 0)).toBeNull();
  });

  it('stands inside the edge towards a waypoint off screen, pointing at it', () => {
    const right = edgeArrow(3000, 300, 10, W, H, 0);
    expect(right?.x).toBeCloseTo(W - EDGE_MARGIN_PX);
    expect(right?.y).toBeCloseTo(300);
    expect(right?.angle).toBeCloseTo(Math.PI / 2);
    const up = edgeArrow(500, -2000, 10, W, H, 0);
    expect(up?.y).toBeCloseTo(EDGE_MARGIN_PX);
    expect(up?.angle).toBeCloseTo(0);
  });

  it('goes by its bearing when it is behind the camera: behind and to the left is the bottom left', () => {
    const behindLeft = edgeArrow(Number.NaN, Number.NaN, -5, W, H, (-3 * Math.PI) / 4);
    expect(behindLeft?.x).toBeLessThan(W / 2);
    expect(behindLeft?.y).toBeCloseTo(H - EDGE_MARGIN_PX);
    expect(behindLeft?.angle).toBeCloseTo((-3 * Math.PI) / 4);
    const behind = edgeArrow(200, 300, -1, W, H, Math.PI);
    expect(behind?.x).toBeCloseTo(W / 2);
    expect(behind?.y).toBeCloseTo(H - EDGE_MARGIN_PX);
  });
});
