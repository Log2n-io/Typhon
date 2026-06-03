import { describe, expect, it } from 'vitest';
import { initialL0Camera, shouldFitViewport } from '@/libs/dbmap/initialFit';
import { fitToRect, worldToScreenX, worldToScreenY } from '@/libs/dbmap/camera';
import { L0_FADE_FRACTION } from '@/libs/dbmap/dbMapLod';

// The one-time fit-to-file guard. The regression this protects: opening the File Map as an inactive dockview
// tab (0×0) defers the fit; it must then fire when the panel gets real dimensions on activation — not stay
// at the default camera with the file 90% off the top-left.

const base = { hasData: true, alreadyFitted: false, flying: false, width: 800, height: 600 };

describe('shouldFitViewport', () => {
  it('fits once data + real dimensions are both present (the activate-from-hidden case)', () => {
    expect(shouldFitViewport(base)).toBe(true);
  });

  it('does not fit while the surface is 0×0 (inactive/hidden panel)', () => {
    expect(shouldFitViewport({ ...base, width: 0, height: 0 })).toBe(false);
    expect(shouldFitViewport({ ...base, width: 800, height: 0 })).toBe(false);
    expect(shouldFitViewport({ ...base, width: 0, height: 600 })).toBe(false);
  });

  it('does not re-fit once already fitted (a resize/refresh preserves the user framing)', () => {
    expect(shouldFitViewport({ ...base, alreadyFitted: true })).toBe(false);
  });

  it('does not fit while a fly-to (cross-link reveal) owns the camera', () => {
    expect(shouldFitViewport({ ...base, flying: true })).toBe(false);
  });

  it('does not fit before any data is decoded', () => {
    expect(shouldFitViewport({ ...base, hasData: false })).toBe(false);
  });
});

// The initial camera opens on the LARGEST pure-L0 composition view — just below the L1 page-grid crossfade.
// The L0→L1 crossfade completes at `L0_FADE_FRACTION · fit` (dbMapRenderer.l1AlphaForScale), so framing the file
// at exactly that scale is the biggest L0 can be without any L1 fading in.
describe('initialL0Camera', () => {
  const world = { x: 0, y: 0, w: 256, h: 256 };

  it('frames the file at L0_FADE_FRACTION of the fit scale (largest pure-L0 zoom, not fit→L1)', () => {
    const cam = initialL0Camera(world, 800, 600, 0);
    const fit = fitToRect(world, 800, 600, 0);
    expect(cam.scale).toBeCloseTo(fit.scale * L0_FADE_FRACTION, 10);
    expect(cam.scale).toBeLessThan(fit.scale); // strictly below fit — fit would land on L1
  });

  it('centres the file in the viewport', () => {
    const cam = initialL0Camera(world, 800, 600, 0);
    expect(worldToScreenX(cam, world.x + world.w / 2)).toBeCloseTo(400, 6);
    expect(worldToScreenY(cam, world.y + world.h / 2)).toBeCloseTo(300, 6);
  });

  it('handles a non-origin world rect (centres on its midpoint)', () => {
    const offset = { x: 100, y: 50, w: 400, h: 200 };
    const cam = initialL0Camera(offset, 1000, 800, 16);
    expect(worldToScreenX(cam, offset.x + offset.w / 2)).toBeCloseTo(500, 6);
    expect(worldToScreenY(cam, offset.y + offset.h / 2)).toBeCloseTo(400, 6);
  });
});
