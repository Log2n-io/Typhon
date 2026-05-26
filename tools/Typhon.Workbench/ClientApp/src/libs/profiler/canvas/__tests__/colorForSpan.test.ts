import { describe, expect, it } from 'vitest';
import { colorForSpan, colorForChunk, durationHeatColor, SPAN_PALETTE } from '@/libs/profiler/canvas/canvasUtils';
import { categoricalColor, type Rgb } from '@/libs/color/categorical';
import { rgbCss, onColor, relativeLuminance } from '@/libs/color/contrast';

// Phase 5 — the span/chunk color routing. `curated` reproduces the legacy djb2-into-SPAN_PALETTE behaviour
// byte-for-byte; `categorical` (default) draws from the shared DS-2 scale with theme-aware lightness; `duration`
// is the sequential heat ramp under both. Chunk `name` mode keys on systemName (cross-view identity), not index.

/** The legacy per-name hash (canvasUtils' private `hashString`) replicated to pin the curated path byte-identical. */
function legacyHash(s: string): number {
  let h = 0;
  for (let i = 0; i < s.length; i++) {
    h = ((h << 5) - h + s.charCodeAt(i)) | 0;
  }
  return h >>> 0;
}

// Categorical span lightness — must match canvasUtils' CAT_SPAN_LIGHT_DARK / _LIGHT.
const LIGHT_DARK = 0.58;
const LIGHT_LIGHT = 0.42;

const span = { name: 'Foo', threadSlot: 3, depth: 2, durationUs: 1234 };

describe('colorForSpan — curated path is byte-identical to the legacy lookup', () => {
  it('name mode = SPAN_PALETTE[hash(name) % len]', () => {
    expect(colorForSpan(span, 'name', SPAN_PALETTE, 'curated', true)).toBe(SPAN_PALETTE[legacyHash('Foo') % SPAN_PALETTE.length]);
  });
  it('thread / depth modes index the supplied palette', () => {
    expect(colorForSpan(span, 'thread', SPAN_PALETTE, 'curated', true)).toBe(SPAN_PALETTE[3 % SPAN_PALETTE.length]);
    expect(colorForSpan(span, 'depth', SPAN_PALETTE, 'curated', true)).toBe(SPAN_PALETTE[2 % SPAN_PALETTE.length]);
  });
});

describe('colorForSpan — categorical path = shared scale, theme-aware lightness', () => {
  it('name mode = categoricalColor(name) at the theme lightness', () => {
    expect(colorForSpan(span, 'name', SPAN_PALETTE, 'categorical', true)).toBe(rgbCss(categoricalColor('Foo', 0.62, LIGHT_DARK)));
    expect(colorForSpan(span, 'name', SPAN_PALETTE, 'categorical', false)).toBe(rgbCss(categoricalColor('Foo', 0.62, LIGHT_LIGHT)));
  });
  it('dark and light themes differ (the SPAN_PALETTE_LIGHT rationale, generalised)', () => {
    expect(colorForSpan(span, 'name', SPAN_PALETTE, 'categorical', true))
      .not.toBe(colorForSpan(span, 'name', SPAN_PALETTE, 'categorical', false));
  });
});

describe('colorForSpan — duration heat is unaffected by the palette toggle', () => {
  it('duration mode = durationHeatColor under both palettes', () => {
    expect(colorForSpan(span, 'duration', SPAN_PALETTE, 'categorical', true)).toBe(durationHeatColor(1234));
    expect(colorForSpan(span, 'duration', SPAN_PALETTE, 'curated', true)).toBe(durationHeatColor(1234));
  });
});

describe('colorForChunk — categorical name mode keys on systemName (cross-view identity)', () => {
  const a = { systemIndex: 0, systemName: 'Physics', threadSlot: 1, durationUs: 50 };
  const b = { systemIndex: 9, systemName: 'Physics', threadSlot: 4, durationUs: 60 };
  it('same systemName → same color even at a different systemIndex', () => {
    expect(colorForChunk(a, 'name', SPAN_PALETTE, 'categorical', true)).toBe(colorForChunk(b, 'name', SPAN_PALETTE, 'categorical', true));
    expect(colorForChunk(a, 'name', SPAN_PALETTE, 'categorical', true)).toBe(rgbCss(categoricalColor('Physics', 0.62, LIGHT_DARK)));
  });
  it('curated name mode still keys on systemIndex (legacy behaviour preserved)', () => {
    expect(colorForChunk(a, 'name', SPAN_PALETTE, 'curated', true)).toBe(SPAN_PALETTE[0 % SPAN_PALETTE.length]);
    expect(colorForChunk(b, 'name', SPAN_PALETTE, 'curated', true)).toBe(SPAN_PALETTE[9 % SPAN_PALETTE.length]);
  });
});

describe('colorForSpan — AA labels over categorical bars (DS-3)', () => {
  it('onColor clears the WCAG floor over the categorical span colors at both theme lightnesses', () => {
    const contrast = (x: Rgb, y: Rgb): number => {
      const lx = relativeLuminance(x);
      const ly = relativeLuminance(y);
      return (Math.max(lx, ly) + 0.05) / (Math.min(lx, ly) + 0.05);
    };
    for (let id = 0; id < 64; id++) {
      for (const light of [LIGHT_DARK, LIGHT_LIGHT]) {
        const bg = categoricalColor(id, 0.62, light);
        expect(contrast(onColor(bg), bg)).toBeGreaterThanOrEqual(3.0);
      }
    }
  });
});
