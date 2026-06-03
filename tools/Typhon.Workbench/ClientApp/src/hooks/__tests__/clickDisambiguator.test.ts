import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createClickDisambiguator } from '../clickDisambiguator';

// The single-click action is deferred so a double-click can cancel it: a double-click on an archetype opens its
// inspector and must NOT toggle the node, while a lone single click still toggles it (after the window).

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

describe('createClickDisambiguator', () => {
  it('runs the deferred single-click action after the delay (a real single click toggles)', () => {
    const g = createClickDisambiguator(250);
    const action = vi.fn();
    g.onSingle(action);
    expect(action).not.toHaveBeenCalled(); // deferred — nothing yet
    vi.advanceTimersByTime(250);
    expect(action).toHaveBeenCalledTimes(1);
  });

  it('cancel() before the delay drops the pending action (double-click suppresses the toggle)', () => {
    const g = createClickDisambiguator(250);
    const action = vi.fn();
    g.onSingle(action); // first click of a double-click schedules the toggle…
    g.cancel(); // …the dblclick handler cancels it
    vi.advanceTimersByTime(1000);
    expect(action).not.toHaveBeenCalled();
  });

  it('a second onSingle inside the window replaces the first (only one toggle fires)', () => {
    const g = createClickDisambiguator(250);
    const first = vi.fn();
    const second = vi.fn();
    g.onSingle(first);
    vi.advanceTimersByTime(100);
    g.onSingle(second); // re-clicked before the first fired
    vi.advanceTimersByTime(250);
    expect(first).not.toHaveBeenCalled();
    expect(second).toHaveBeenCalledTimes(1);
  });

  it('cancel() is a no-op when nothing is pending', () => {
    const g = createClickDisambiguator(250);
    expect(() => g.cancel()).not.toThrow();
  });
});
