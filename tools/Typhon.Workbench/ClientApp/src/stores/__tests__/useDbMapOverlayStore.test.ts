import { beforeEach, describe, expect, it } from 'vitest';
import { useDbMapOverlayStore } from '../useDbMapOverlayStore';

describe('useDbMapOverlayStore', () => {
  beforeEach(() => {
    useDbMapOverlayStore.getState().clear();
  });

  it('starts with no overlay (plain occupancy colouring)', () => {
    const s = useDbMapOverlayStore.getState();
    expect(s.segmentId).toBeNull();
    expect(s.componentSlot).toBeNull();
    expect(s.componentName).toBe('');
  });

  it('records the selected segment, slot, and component name', () => {
    useDbMapOverlayStore.getState().setOverlay(37, 1, 'Game.Velocity');
    const s = useDbMapOverlayStore.getState();
    expect(s.segmentId).toBe(37);
    expect(s.componentSlot).toBe(1);
    expect(s.componentName).toBe('Game.Velocity');
  });

  it('clears back to occupancy colouring', () => {
    useDbMapOverlayStore.getState().setOverlay(37, 1, 'Game.Velocity');
    useDbMapOverlayStore.getState().clear();
    const s = useDbMapOverlayStore.getState();
    expect(s.segmentId).toBeNull();
    expect(s.componentSlot).toBeNull();
  });
});
