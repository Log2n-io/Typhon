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
