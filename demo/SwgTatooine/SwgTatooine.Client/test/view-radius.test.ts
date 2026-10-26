import { beforeEach, describe, expect, it } from 'vitest';
import { useUi } from '../src/state/ui-store';

/**
 * The view radius must never exceed what the server honours.
 *
 * The defect: the server shrinks an oversized `ClientRegion` about its centroid without a word, and its ceiling is on no
 * wire the client can read (Typhon #1075) — so the slider went to 4000 m, the server served 1500, and the HUD reported
 * 4000. The demo's own server now publishes its configuration at `/typhon/demo.json`; this is the clamp that uses it.
 */
describe('the view radius against a server ceiling', () => {
  beforeEach(() => {
    useUi.setState({ viewRadius: 1500, maxViewRadius: null });
  });

  it('accepts anything while the ceiling is unknown', () => {
    useUi.getState().setViewRadius(4000);
    expect(useUi.getState().viewRadius).toBe(4000);
  });

  it('never exceeds a known ceiling, however it is asked', () => {
    useUi.getState().setMaxViewRadius(1500);
    useUi.getState().setViewRadius(4000);
    expect(useUi.getState().viewRadius).toBe(1500);

    useUi.getState().setViewRadius(750);
    expect(useUi.getState().viewRadius, 'below the ceiling is untouched').toBe(750);
  });

  it('pulls a radius already above the ceiling down when the ceiling arrives', () => {
    // The order the live client actually runs in: the store starts at its default and the fetch lands a moment later.
    useUi.setState({ viewRadius: 4000, maxViewRadius: null });
    useUi.getState().setMaxViewRadius(1500);
    expect(useUi.getState().viewRadius).toBe(1500);
  });

  it('leaves the radius alone when the server has no client-driven region at all', () => {
    // 0 is a real answer — a whole-world camera — and not the same as unknown. The toolbar hides the slider rather than
    // offering a radius of zero, so the value it keeps does not matter, but it must not be clamped to 0 and stick there
    // if a later session does have a region.
    useUi.setState({ viewRadius: 1500, maxViewRadius: null });
    useUi.getState().setMaxViewRadius(0);
    expect(useUi.getState().maxViewRadius).toBe(0);
    expect(useUi.getState().viewRadius).toBe(1500);
  });
});
