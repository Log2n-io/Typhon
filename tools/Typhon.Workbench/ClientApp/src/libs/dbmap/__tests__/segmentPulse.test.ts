import { describe, expect, it } from 'vitest';
import { SEGMENT_PULSE_MS, segmentPulseAlpha } from '@/libs/dbmap/dbMapRenderer';

// The post-reveal segment highlight pulse envelope (the renderer scales this by the L1 grid alpha, then paints the
// segment's pages with it as a fill AND derives the silhouette-frame alpha from it). A bold, transient flash:
// near-opaque crests up front so the accent punches through the density ramp, exactly zero once the window closes.

describe('segmentPulseAlpha', () => {
  it('is visible at the start of the flash', () => {
    expect(segmentPulseAlpha(0)).toBeGreaterThan(0.3);
  });

  it('flashes BOLD up front — an early crest is near-opaque so it reads over the density ramp', () => {
    // First oscillation crest is at t = 1/12 (sin(t·6π) = 1). Sample the early window and assert it punches high.
    let peak = 0;
    for (let ms = 0; ms < SEGMENT_PULSE_MS * 0.25; ms += 10) {
      peak = Math.max(peak, segmentPulseAlpha(ms));
    }
    expect(peak).toBeGreaterThan(0.85);
  });

  it('is exactly zero once the pulse window has elapsed (no lingering tint)', () => {
    expect(segmentPulseAlpha(SEGMENT_PULSE_MS)).toBe(0);
    expect(segmentPulseAlpha(SEGMENT_PULSE_MS + 500)).toBe(0);
  });

  it('treats negative elapsed (clock skew) as no pulse', () => {
    expect(segmentPulseAlpha(-10)).toBe(0);
  });

  it('stays within [0, 1] across the whole window', () => {
    for (let ms = 0; ms < SEGMENT_PULSE_MS; ms += 50) {
      const a = segmentPulseAlpha(ms);
      expect(a).toBeGreaterThanOrEqual(0);
      expect(a).toBeLessThanOrEqual(1);
    }
  });

  it('trends downward overall (fades out) — late is dimmer than early', () => {
    expect(segmentPulseAlpha(SEGMENT_PULSE_MS * 0.9)).toBeLessThan(segmentPulseAlpha(0));
  });
});
