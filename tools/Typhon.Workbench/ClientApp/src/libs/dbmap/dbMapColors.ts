// Colour resolution for the Database File Map coarse encodings (Module 15, §4.2).
//
// Colours are produced as [r,g,b] tuples so the renderer can write them straight into ImageData when painting
// the offscreen Hilbert image — far cheaper than parsing a CSS colour string per page.

import { DbPageType, NO_SEGMENT, type DbMapEncoding } from './types';

export type Rgb = readonly [number, number, number];

/** Categorical page-type palette, indexed by `DbPageType` ordinal. Identity colours (theme-independent). */
export const PAGE_TYPE_RGB: readonly Rgb[] = [
  [107, 114, 128], // Unknown   — gray
  [30, 41, 59], //    Free      — dark slate
  [245, 158, 11], //   Root      — amber
  [139, 92, 246], //   Occupancy — violet
  [59, 130, 246], //   Component — blue
  [6, 182, 212], //    Revision  — cyan
  [16, 185, 129], //   Index     — green
  [236, 72, 153], //   Cluster   — pink
  [249, 115, 22], //   VSBS      — orange
  [234, 179, 8], //    String    — yellow
];

/** Free / used binary encoding. */
export const FREE_RGB: Rgb = [30, 41, 59];
export const USED_RGB: Rgb = [56, 189, 248];

/** Inert Hilbert-tail / no-data background. */
export const TAIL_RGB: Rgb = [15, 23, 42];

/** Stable per-segment colour — a golden-angle hue walk keeps neighbouring segment ids visually distinct. */
export function segmentRgb(segmentId: number): Rgb {
  if (segmentId === NO_SEGMENT) {
    return TAIL_RGB;
  }
  const hue = (segmentId * 137.508) % 360;
  return hslToRgb(hue / 360, 0.62, 0.58);
}

/** Resolves the [r,g,b] for one page under the active encoding. */
export function pageColorRgb(encoding: DbMapEncoding, type: number, segmentId: number): Rgb {
  switch (encoding) {
    case 'segment':
      return segmentId === NO_SEGMENT ? PAGE_TYPE_RGB[DbPageType.Free] : segmentRgb(segmentId);
    case 'freeUsed':
      return type === DbPageType.Free ? FREE_RGB : USED_RGB;
    case 'pageType':
    default:
      return PAGE_TYPE_RGB[type] ?? PAGE_TYPE_RGB[DbPageType.Unknown];
  }
}

/** CSS `rgb(...)` string — for DOM legend swatches. */
export function rgbCss(rgb: Rgb): string {
  return `rgb(${rgb[0]}, ${rgb[1]}, ${rgb[2]})`;
}

// ── A2 detail-tier ramps (Module 15, §4.2) ─────────────────────────────────────────────────────────────────

function lerpRgb(a: Rgb, b: Rgb, t: number): Rgb {
  const tt = t < 0 ? 0 : t > 1 ? 1 : t;
  return [
    Math.round(a[0] + (b[0] - a[0]) * tt),
    Math.round(a[1] + (b[1] - a[1]) * tt),
    Math.round(a[2] + (b[2] - a[2]) * tt),
  ];
}

/** Fill-density heatmap — empty (dark) → half (blue) → full (amber). `ratio` is 0..1. */
export function fillDensityRgb(ratio: number): Rgb {
  return ratio < 0.5
    ? lerpRgb([30, 41, 59], [59, 130, 246], ratio * 2)
    : lerpRgb([59, 130, 246], [245, 158, 11], (ratio - 0.5) * 2);
}

/** Write-age ramp — cold (old) blue → hot (newest) red. `ratio` is 0..1, relative to the region's max revision. */
export function writeAgeRgb(ratio: number): Rgb {
  return lerpRgb([37, 99, 235], [239, 68, 68], ratio);
}

/** Entropy ramp (A3, §4.2) — low (structured, dark) → mid (teal) → high (random/encrypted, red). `ratio` is 0..1. */
export function entropyRgb(ratio: number): Rgb {
  return ratio < 0.5
    ? lerpRgb([30, 41, 59], [20, 184, 166], ratio * 2)
    : lerpRgb([20, 184, 166], [239, 68, 68], (ratio - 0.5) * 2);
}

/** Byte-class categorical palette (A3, §4.2) — 0 zero · 1 0xFF · 2 ASCII · 3 binary. */
export const BYTE_CLASS_RGB: readonly Rgb[] = [
  [30, 41, 59], //   zero   — dark slate
  [148, 163, 184], // 0xFF   — light slate
  [234, 179, 8], //   ASCII  — yellow
  [59, 130, 246], //  binary — blue
];

/** CRC-status categorical colour — indexed by `DbCrcStatus` ordinal. */
export const CRC_RGB: readonly Rgb[] = [
  [107, 114, 128], // Unverified — gray
  [16, 185, 129], //  Verified   — green
  [239, 68, 68], //   Failed     — red
];

/** Cache-residency categorical colour — indexed by `DbResidency` ordinal. */
export const RESIDENCY_RGB: readonly Rgb[] = [
  [71, 85, 105], //  OnDiskOnly    — slate
  [34, 197, 94], //  ResidentClean — green
  [234, 179, 8], //  ResidentDirty — yellow
];

/** Stable colour for one L4 content cell — field id / directory entry / byte class — colored by semantics. */
export function contentCellRgb(kind: string, colorKey: number): Rgb {
  if (kind === 'byteRun') {
    // 0 zero · 1 0xFF · 2 ascii · 3 binary
    return BYTE_CLASS_RGB[colorKey] ?? [107, 114, 128];
  }
  if (kind === 'entityPk') {
    return [148, 163, 184];
  }
  if (colorKey < 0) {
    return [107, 114, 128];
  }
  // Field / directory entry — golden-angle hue walk keeps adjacent keys distinct.
  const hue = (colorKey * 137.508) % 360;
  return hslToRgb(hue / 360, 0.6, 0.6);
}

function hslToRgb(h: number, s: number, l: number): Rgb {
  if (s === 0) {
    const v = Math.round(l * 255);
    return [v, v, v];
  }
  const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
  const p = 2 * l - q;
  return [
    Math.round(hueToChannel(p, q, h + 1 / 3) * 255),
    Math.round(hueToChannel(p, q, h) * 255),
    Math.round(hueToChannel(p, q, h - 1 / 3) * 255),
  ];
}

function hueToChannel(p: number, q: number, t: number): number {
  let tt = t;
  if (tt < 0) tt += 1;
  if (tt > 1) tt -= 1;
  if (tt < 1 / 6) return p + (q - p) * 6 * tt;
  if (tt < 1 / 2) return q;
  if (tt < 2 / 3) return p + (q - p) * (2 / 3 - tt) * 6;
  return p;
}
