import { afterEach, describe, expect, it } from 'vitest';
import { useDagViewStore } from '../useDagViewStore';

// Phase 3D — the ephemeral "Reveal in System DAG" focus signal. A handoff parks a system name; the canvas
// consumes it to centre that node, then clears it. It must NOT be persisted (a stale reveal would replay on reopen).

afterEach(() => {
  useDagViewStore.getState().clearPendingFocusSystem();
});

describe('useDagViewStore — reveal focus signal', () => {
  it('requestFocusSystem parks a name; clearPendingFocusSystem clears it', () => {
    expect(useDagViewStore.getState().pendingFocusSystem).toBeNull();
    useDagViewStore.getState().requestFocusSystem('Movement');
    expect(useDagViewStore.getState().pendingFocusSystem).toBe('Movement');
    useDagViewStore.getState().clearPendingFocusSystem();
    expect(useDagViewStore.getState().pendingFocusSystem).toBeNull();
  });

  it('excludes pendingFocusSystem from the persisted snapshot — only the sticky view prefs persist', () => {
    useDagViewStore.getState().requestFocusSystem('Movement');
    const { partialize } = useDagViewStore.persist.getOptions();
    expect(partialize).toBeTypeOf('function');
    const persisted = partialize!(useDagViewStore.getState()) as Record<string, unknown>;
    expect('pendingFocusSystem' in persisted).toBe(false);
    expect(Object.keys(persisted).sort()).toEqual(['hideSkipped', 'layout', 'showCrossPhaseEdges', 'statMode']);
  });
});
