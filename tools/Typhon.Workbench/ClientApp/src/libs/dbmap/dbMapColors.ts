// Colour resolution for the Database File Map coarse encodings (Module 15, §4.2).
//
// Colours are produced as [r,g,b] tuples so the renderer can write them straight into ImageData when painting
// the offscreen Hilbert image — far cheaper than parsing a CSS colour string per page.

import { DbPageType, NO_SEGMENT, type DbMapEncoding } from './types';
import { categoricalColor, categoricalHue, hslToRgb, type Rgb } from '@/libs/color/categorical';
import { onColor, onColorCss, rgbCss } from '@/libs/color/contrast';

// `Rgb` lives in the shared color base now (DS-2 consolidation); re-export so the renderer/panels that import
// it from here keep working unchanged.
export type { Rgb };

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
  [20, 184, 166], //   Spatial   — teal
  [132, 204, 22], //   EntityMap — lime
  [100, 116, 139], //  System    — slate
];

/** Free / used binary encoding. */
export const FREE_RGB: Rgb = [30, 41, 59];
export const USED_RGB: Rgb = [56, 189, 248];

/** Structural (non-data) chunk fill — a hashmap meta / directory or index directory chunk. Distinct from free so it never reads as data or empty (A6). */
export const STRUCT_RGB: Rgb = [51, 65, 85];

/** B-tree internal-node accent (A6). Leaf nodes take the page colour; internal nodes (the sparse skeleton) take this amber so the tree shape reads at a glance. */
export const INDEX_INTERNAL_RGB: Rgb = [245, 158, 11];

/** Inert Hilbert-tail / no-data background. */
export const TAIL_RGB: Rgb = [15, 23, 42];

/** Stable per-segment colour — the shared DS-2 categorical hue ({@link categoricalColor}), so a segment reads the same here as in any other view. */
export function segmentRgb(segmentId: number): Rgb {
  if (segmentId === NO_SEGMENT) {
    return TAIL_RGB;
  }
  return categoricalColor(segmentId);
}

/**
 * Lightness band for the rank-shaded segment encoding — rank 0 (first page of a segment) → darkest, rank 1 (last) →
 * lightest. Wide on purpose so a large segment shows a clear start→end gradient; small segments don't reach the
 * extremes because the caller scales the band by a per-segment spread factor (see the renderer).
 */
const SEG_RANK_L_MIN = 0.18;
const SEG_RANK_L_MAX = 0.92;

/**
 * Owning-segment colour shaded by the page's rank within its segment: the same golden-angle hue as {@link segmentRgb}
 * (so the segment stays identifiable) but with lightness ramped by rank to make page order legible despite the Hilbert
 * layout. The ramp is centred on the segment's base lightness and its half-width is scaled by `spread` (0..1) so a
 * few-page segment only uses a narrow band (the caller passes a smaller `spread` for small segments — see the renderer's
 * per-segment spread factor). `rankFraction` is 0..1 (the persisted 0–255 rank ÷ 255); `spread` 1 = full range. Unowned
 * pages fall back to the Free colour, matching {@link pageColorRgb}'s `segment` case.
 */
export function segmentRgbRanked(segmentId: number, rankFraction: number, spread = 1): Rgb {
  if (segmentId === NO_SEGMENT) {
    return PAGE_TYPE_RGB[DbPageType.Free];
  }
  const hue = categoricalHue(segmentId);
  const t = rankFraction < 0 ? 0 : rankFraction > 1 ? 1 : rankFraction;
  const s = spread < 0 ? 0 : spread > 1 ? 1 : spread;
  const center = (SEG_RANK_L_MIN + SEG_RANK_L_MAX) / 2;
  const halfRange = ((SEG_RANK_L_MAX - SEG_RANK_L_MIN) / 2) * s;
  return hslToRgb(hue / 360, 0.62, center + (t * 2 - 1) * halfRange);
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

// `onColor` / `onColorCss` / `rgbCss` now live in the shared contrast base (`@/libs/color/contrast`, DS-3
// consolidation — one luminance/ink implementation, no parallel copies). Re-export them so the File Map renderer
// + panels that import these from here keep working unchanged.
export { onColor, onColorCss, rgbCss };

// ── A2 detail-tier ramps (Module 15, §4.2) ─────────────────────────────────────────────────────────────────

function lerpRgb(a: Rgb, b: Rgb, t: number): Rgb {
  const tt = t < 0 ? 0 : t > 1 ? 1 : t;
  return [
    Math.round(a[0] + (b[0] - a[0]) * tt),
    Math.round(a[1] + (b[1] - a[1]) * tt),
    Math.round(a[2] + (b[2] - a[2]) * tt),
  ];
}

/** Allocation ramp — free (dark slate) → allocated (cyan), the file's used/free palette. `ratio` is 0..1 (allocated fraction). */
export function allocationRgb(ratio: number): Rgb {
  return lerpRgb(FREE_RGB, USED_RGB, ratio);
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
  if (kind === 'componentHeader') {
    // L5 cluster-entity decode (file-map §10 Q4 override): the per-component header band that groups an entity's field
    // cells. Structural slate — the same non-data colour the directory / hashmap-meta chunks use — so it reads as a
    // label row, distinct from the categorical field cells below it.
    return STRUCT_RGB;
  }
  if (kind === 'entitySlot') {
    // Cluster entity sub-grid (A6): a slot is lit (occupied) or dark (free). Uses the File Map's universal
    // allocated/free language — USED_RGB / FREE_RGB — the same pair as the page-level Free/Used encoding and the
    // occupancy-page region map (allocationRgb). Keeping it identical makes "cyan = occupied, dark = free" read the
    // same wherever allocation is shown; a unique slot colour would only fragment that vocabulary.
    return colorKey > 0 ? USED_RGB : FREE_RGB;
  }
  if (colorKey < 0) {
    return [107, 114, 128];
  }
  // Field / directory entry — the shared categorical hue, a touch lighter than the default swatch.
  return categoricalColor(colorKey, 0.6, 0.6);
}

/** Component-enabled overlay colour for a cluster entity slot (A6 §10.1). */
export const ENABLED_RGB: Rgb = [34, 197, 94]; //  enabled  — green
export const DISABLED_RGB: Rgb = [120, 53, 53]; // occupied but component disabled — muted red

/**
 * Colour for one cluster entity slot under the per-component overlay: free slots stay dark; an occupied slot is
 * green when the selected component is enabled for its entity, muted-red when it is occupied but the component is
 * disabled. Makes per-component enable/disable distribution legible across the whole cluster segment.
 */
export function enabledOverlayRgb(occupied: boolean, enabled: boolean): Rgb {
  if (!occupied) {
    return FREE_RGB;
  }
  return enabled ? ENABLED_RGB : DISABLED_RGB;
}
