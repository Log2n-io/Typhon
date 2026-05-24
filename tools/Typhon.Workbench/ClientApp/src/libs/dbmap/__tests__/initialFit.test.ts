import { describe, expect, it } from 'vitest';
import { shouldFitViewport } from '@/libs/dbmap/initialFit';

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
