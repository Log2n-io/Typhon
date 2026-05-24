// DS-2 categorical color — the shared, deterministic "stable hue-per-object" util (design-system.md §DS-2).
//
// A given object id (component / system / segment / kind) maps to the SAME hue in every view — File Map,
// Data Flow, Access Matrix, System DAG, Query Analyzer — so color is a cross-view *identity*, not decoration.
// This is the single source the per-panel palettes consolidate onto (e.g. dbMapColors' segment hue) rather
// than each inventing its own (DS-2: "rationalize = subtract"). Pure → unit-tested (conformance suite G).

export type Rgb = readonly [number, number, number];

/**
 * Deterministic 32-bit hash of an object id, stable across views and sessions. Numbers map to themselves (so a
 * numeric id keeps the legacy `id * golden-angle` hue exactly); strings hash via FNV-1a.
 */
export function hashId(id: string | number): number {
  if (typeof id === 'number') {
    return id >>> 0;
  }
  let h = 0x811c9dc5;
  for (let i = 0; i < id.length; i++) {
    h ^= id.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return h >>> 0;
}

/**
 * Golden-angle hue (0..360) for an id. The 137.508° step keeps successive ids maximally separated on the wheel,
 * so adjacent objects stay visually distinct without a fixed (and quickly-exhausted) palette.
 */
export function categoricalHue(id: string | number): number {
  return (hashId(id) * 137.508) % 360;
}

/** Default saturation / lightness for categorical swatches — matches the File Map's long-standing segment hue. */
const CAT_SAT = 0.62;
const CAT_LIGHT = 0.58;

/**
 * DS-2 categorical color: a deterministic, stable [r,g,b] per object id. Same id → same color everywhere.
 * `sat`/`light` default to the shared categorical swatch; callers that need a lightness ramp (e.g. the File
 * Map's rank-shaded segment encoding) take {@link categoricalHue} and build their own HSL.
 */
export function categoricalColor(id: string | number, sat = CAT_SAT, light = CAT_LIGHT): Rgb {
  return hslToRgb(categoricalHue(id) / 360, sat, light);
}

/** HSL (h,s,l in 0..1) → sRGB [0..255]. The one HSL→RGB conversion the data-viz palettes share. */
export function hslToRgb(h: number, s: number, l: number): Rgb {
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
