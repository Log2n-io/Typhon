import { describe, expect, it } from 'vitest';
import { resolveInitialViewport, type SavedViewport } from '../initialViewport';

/** A minimal tick-summary shape — only the two fields resolveInitialViewport reads. */
const tick = (startUs: number, durationUs: number) => ({ startUs, durationUs });

describe('resolveInitialViewport', () => {
  it('returns null when the trace carries no ticks', () => {
    expect(resolveInitialViewport({ fingerprint: 'fp', tickSummaries: [] }, null)).toBeNull();
    expect(resolveInitialViewport({ fingerprint: 'fp', tickSummaries: null }, null)).toBeNull();
  });

  it('falls back to the first tick when there is no saved viewport', () => {
    const r = resolveInitialViewport(
      { fingerprint: 'fp', tickSummaries: [tick(1000, 50), tick(2000, 50)] },
      null,
    );
    expect(r).toEqual({ startUs: 1000, endUs: 1050 });
  });

  it('restores the saved viewport when its fingerprint matches the trace', () => {
    const saved: SavedViewport = { fingerprint: 'fp-A', startUs: 7000, endUs: 9000 };
    const r = resolveInitialViewport({ fingerprint: 'fp-A', tickSummaries: [tick(0, 100)] }, saved);
    expect(r).toEqual({ startUs: 7000, endUs: 9000 });
  });

  it('falls back to the first tick when the fingerprint differs (re-profiled file)', () => {
    const saved: SavedViewport = { fingerprint: 'fp-OLD', startUs: 7000, endUs: 9000 };
    const r = resolveInitialViewport({ fingerprint: 'fp-NEW', tickSummaries: [tick(500, 30)] }, saved);
    expect(r).toEqual({ startUs: 500, endUs: 530 });
  });

  it('ignores a saved viewport when the trace metadata carries no fingerprint', () => {
    const saved: SavedViewport = { fingerprint: 'fp', startUs: 7000, endUs: 9000 };
    expect(
      resolveInitialViewport({ fingerprint: '', tickSummaries: [tick(500, 30)] }, saved),
    ).toEqual({ startUs: 500, endUs: 530 });
    expect(
      resolveInitialViewport({ fingerprint: null, tickSummaries: [tick(500, 30)] }, saved),
    ).toEqual({ startUs: 500, endUs: 530 });
  });

  it('rejects a degenerate saved viewport and falls back to the first tick', () => {
    const saved: SavedViewport = { fingerprint: 'fp', startUs: 9000, endUs: 9000 };
    const r = resolveInitialViewport({ fingerprint: 'fp', tickSummaries: [tick(500, 30)] }, saved);
    expect(r).toEqual({ startUs: 500, endUs: 530 });
  });

  it('applies a 1µs floor when the first tick has zero duration', () => {
    const r = resolveInitialViewport({ fingerprint: 'fp', tickSummaries: [tick(500, 0)] }, null);
    expect(r).toEqual({ startUs: 500, endUs: 501 });
  });

  it('coerces string-typed tick fields (long-serialised wire values)', () => {
    const r = resolveInitialViewport(
      { fingerprint: 'fp', tickSummaries: [{ startUs: '1000', durationUs: '40' }] },
      null,
    );
    expect(r).toEqual({ startUs: 1000, endUs: 1040 });
  });
});
