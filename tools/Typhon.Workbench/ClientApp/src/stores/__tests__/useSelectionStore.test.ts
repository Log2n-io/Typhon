import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useSelectionStore } from '../useSelectionStore';

beforeEach(() => {
  useSelectionStore.getState().clear();
});

describe('useSelectionStore', () => {
  it('starts with every slot null', () => {
    const s = useSelectionStore.getState();
    expect(s.time).toBeNull();
    expect(s.focusTick).toBeNull();
    expect(s.system).toBeNull();
    expect(s.component).toBeNull();
    expect(s.queue).toBeNull();
    expect(s.resource).toBeNull();
    expect(s.entity).toBeNull();
    expect(s.worker).toBeNull();
  });

  it('per-slot setters update only their own slot', () => {
    const s = useSelectionStore.getState();
    s.setSystem('AI');
    s.setQueue('Damage');
    expect(useSelectionStore.getState().system).toBe('AI');
    expect(useSelectionStore.getState().queue).toBe('Damage');
    expect(useSelectionStore.getState().component).toBeNull();
  });

  it('setTime stores the range', () => {
    useSelectionStore.getState().setTime({ start: 120_000, end: 134_000 });
    expect(useSelectionStore.getState().time).toEqual({ start: 120_000, end: 134_000 });
  });

  it('selector subscribers only fire when their slot changes', () => {
    const systemListener = vi.fn();
    const queueListener = vi.fn();
    const unsubSystem = useSelectionStore.subscribe((s, prev) => {
      if (s.system !== prev.system) systemListener(s.system);
    });
    const unsubQueue = useSelectionStore.subscribe((s, prev) => {
      if (s.queue !== prev.queue) queueListener(s.queue);
    });

    useSelectionStore.getState().setSystem('AI');
    expect(systemListener).toHaveBeenCalledTimes(1);
    expect(queueListener).not.toHaveBeenCalled();

    useSelectionStore.getState().setQueue('Damage');
    expect(systemListener).toHaveBeenCalledTimes(1);
    expect(queueListener).toHaveBeenCalledTimes(1);

    // Setting the same value is a no-op for selectors (zustand shallow eq on the slot).
    useSelectionStore.getState().setSystem('AI');
    expect(systemListener).toHaveBeenCalledTimes(1);

    unsubSystem();
    unsubQueue();
  });

  it('clear resets every slot to null', () => {
    const s = useSelectionStore.getState();
    s.setSystem('AI');
    s.setComponent('Position');
    s.setQueue('Damage');
    s.setResource('storage/paged-mmf');
    s.setEntity('e-42');
    s.setWorker(3);
    s.setTime({ start: 0, end: 1000 });
    s.setFocusTick(7);

    s.clear();

    const after = useSelectionStore.getState();
    expect(after.system).toBeNull();
    expect(after.component).toBeNull();
    expect(after.queue).toBeNull();
    expect(after.resource).toBeNull();
    expect(after.entity).toBeNull();
    expect(after.worker).toBeNull();
    expect(after.time).toBeNull();
    expect(after.focusTick).toBeNull();
  });
});
