import { afterEach, describe, expect, it, vi } from 'vitest';
import { installDevMeasureBufferGuard } from '@/lib/devMeasureBufferGuard';

/**
 * The guard's whole job is that the sweep actually fires and actually clears. Both halves failed silently in the version
 * of this problem that shipped: React's dev build wrote ~830 `performance.measure` entries a second, nothing cleared them,
 * and the tab died at about two minutes with the measure call itself out of memory.
 */
describe('devMeasureBufferGuard', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('clears the measure buffer on each sweep, and only the measure buffer', () => {
    vi.useFakeTimers();
    const clearMeasures = vi.spyOn(performance, 'clearMeasures').mockImplementation(() => {});
    const clearMarks = vi.spyOn(performance, 'clearMarks').mockImplementation(() => {});

    const stop = installDevMeasureBufferGuard();
    expect(clearMeasures).not.toHaveBeenCalled();

    vi.advanceTimersByTime(30_000);
    expect(clearMeasures).toHaveBeenCalledTimes(1);

    vi.advanceTimersByTime(90_000);
    expect(clearMeasures).toHaveBeenCalledTimes(4);

    // Marks are deliberately left alone: nothing accumulates them at render rate, and clearing them would disturb a
    // profiling session a developer set up by hand.
    expect(clearMarks).not.toHaveBeenCalled();

    stop();
    vi.advanceTimersByTime(60_000);
    expect(clearMeasures).toHaveBeenCalledTimes(4);
  });

  it('survives a UA that refuses the call rather than taking the app down', () => {
    vi.useFakeTimers();
    vi.spyOn(performance, 'clearMeasures').mockImplementation(() => {
      throw new Error('nope');
    });

    const stop = installDevMeasureBufferGuard();
    expect(() => vi.advanceTimersByTime(60_000)).not.toThrow();
    stop();
  });

  it('really does clear, against the live performance timeline', () => {
    // The spy test above proves the timer wiring; this one proves the call has the effect claimed, with no mock in the
    // way. Worth separating because a guard that fires faithfully and clears nothing is the same bug.
    performance.measure('guard-test-a');
    performance.measure('guard-test-b');
    expect(performance.getEntriesByType('measure').length).toBeGreaterThanOrEqual(2);

    vi.useFakeTimers();
    const stop = installDevMeasureBufferGuard();
    vi.advanceTimersByTime(30_000);
    stop();

    expect(performance.getEntriesByType('measure')).toHaveLength(0);
  });
});
