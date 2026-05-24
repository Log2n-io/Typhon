import { describe, expect, it } from 'vitest';
import { SEGMENT_PULSE_MS, segmentPulseAlpha } from '@/libs/dbmap/dbMapRenderer';

// The post-reveal segment highlight pulse envelope (the renderer scales this by the L1 grid alpha + paints the
// segment's pages with it). A "brief flash": visible up front, exactly zero once the window closes.

describe('segmentPulseAlpha', () => {
  it('is visible at the start of the flash', () => {
    expect(segmentPulseAlpha(0)).toBeGreaterThan(0.3);
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
