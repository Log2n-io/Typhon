// DS-3 contrast + color-mix utilities — the ONE source for "legible ink over a dynamic background" and the small
// hex/Rgb conversions the data-viz palettes share (design-system.md §DS-3). Consolidated here so there is a single
// luminance/onColor implementation instead of the former parallel copies in `libs/colors.ts` (hex) and
// `libs/dbmap/dbMapColors.ts` (Rgb). `categorical.ts` owns hue *generation*; this file owns hue *legibility*.
//
// Two numeric representations coexist on purpose, with distinct names rather than one lossy signature:
//   • Rgb (`[r,g,b]` 0..255) — for canvas ImageData / DOM swatches (File Map, timeline): onColor / rgbCss / rgbToHex.
//   • hex (`#rrggbb`)        — for SVG / DOM string consumers (Critical Path, palettes): lighten / darken / pickTextColorFor.
// Callers pick the path that matches the data they already hold; no representation churn at the call site.

import type { Rgb } from './categorical';

// ── Rgb path ─────────────────────────────────────────────────────────────────────────────────────────────

/** WCAG relative luminance of an sRGB colour (0 = black, 1 = white). */
export function relativeLuminance(rgb: Rgb): number {
  const channel = (c: number): number => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  };
  return 0.2126 * channel(rgb[0]) + 0.7152 * channel(rgb[1]) + 0.0722 * channel(rgb[2]);
}

const ON_WHITE: Rgb = [255, 255, 255];
const ON_BLACK: Rgb = [17, 24, 39]; // near-black (slate-900) — softer than pure #000 over coloured cells

/**
 * DS-3: legible text colour over a dynamic background. Picks white or near-black — whichever has the higher
 * WCAG contrast against <paramref name="bg"/> — so a label drawn over any data-driven colour stays readable.
 * Two inks guarantee at least the WCAG UI/large-text floor (≥3:1) over every cell colour, and AA-normal
 * (≥4.5:1) wherever the background luminance allows.
 */
export function onColor(bg: Rgb): Rgb {
  const lum = relativeLuminance(bg);
  const contrastWhite = 1.05 / (lum + 0.05);
  const contrastBlack = (lum + 0.05) / (relativeLuminance(ON_BLACK) + 0.05);
  return contrastWhite >= contrastBlack ? ON_WHITE : ON_BLACK;
}

/** {@link onColor} as a CSS string — for canvas `fillStyle` / DOM text over a coloured cell. */
export function onColorCss(bg: Rgb): string {
  return rgbCss(onColor(bg));
}

/** CSS `rgb(...)` string — for DOM legend swatches / canvas fills. */
export function rgbCss(rgb: Rgb): string {
  return `rgb(${rgb[0]}, ${rgb[1]}, ${rgb[2]})`;
}

/** `[r,g,b]` → `#rrggbb`. Bridges the Rgb categorical scale into the hex `PaletteColor` shape the SVG views expect. */
export function rgbToHex(rgb: Rgb): string {
  return toHex(rgb[0], rgb[1], rgb[2]);
}

// ── hex path ─────────────────────────────────────────────────────────────────────────────────────────────

/**
 * WCAG 2 relative-luminance (Y) of an sRGB hex colour. Input must be `#rrggbb` or `#rgb`. Returns 0..1; > 0.5 is
 * "perceptually light, dark text wins" by the threshold convention. The hex sibling of {@link relativeLuminance}.
 */
export function relativeLuminanceHex(hex: string): number {
  let r = 0;
  let g = 0;
  let b = 0;
  if (hex.length === 7) {
    r = parseInt(hex.slice(1, 3), 16) / 255;
    g = parseInt(hex.slice(3, 5), 16) / 255;
    b = parseInt(hex.slice(5, 7), 16) / 255;
  } else if (hex.length === 4) {
    r = parseInt(hex[1] + hex[1], 16) / 255;
    g = parseInt(hex[2] + hex[2], 16) / 255;
    b = parseInt(hex[3] + hex[3], 16) / 255;
  }
  const lin = (c: number): number => (c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4));
  return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
}

/**
 * Pick a high-contrast text colour for a given background `barHex`. Defaults: white on dark backgrounds, black on
 * light. Override `light` / `dark` to project-specific tones (e.g. a theme's ink token instead of pure black/white).
 */
export function pickTextColorFor(barHex: string, light: string = '#000', dark: string = '#fff'): string {
  return relativeLuminanceHex(barHex) > 0.5 ? light : dark;
}

/** Parse a `#rrggbb` / `#rgb` hex string to an sRGB `[r, g, b]` triple (0..255). */
function parseHex(hex: string): [number, number, number] {
  if (hex.length === 7) {
    return [parseInt(hex.slice(1, 3), 16), parseInt(hex.slice(3, 5), 16), parseInt(hex.slice(5, 7), 16)];
  }
  if (hex.length === 4) {
    return [parseInt(hex[1] + hex[1], 16), parseInt(hex[2] + hex[2], 16), parseInt(hex[3] + hex[3], 16)];
  }
  return [0, 0, 0];
}

/** Clamp a channel to 0..255 and format the `[r, g, b]` triple back to `#rrggbb`. */
function toHex(r: number, g: number, b: number): string {
  const c = (v: number): string => Math.round(Math.max(0, Math.min(255, v))).toString(16).padStart(2, '0');
  return `#${c(r)}${c(g)}${c(b)}`;
}

/** Mix `hex` toward white by `amount` (0 = unchanged, 1 = white). */
export function lighten(hex: string, amount: number): string {
  const [r, g, b] = parseHex(hex);
  return toHex(r + (255 - r) * amount, g + (255 - g) * amount, b + (255 - b) * amount);
}

/** Mix `hex` toward black by `amount` (0 = unchanged, 1 = black). */
export function darken(hex: string, amount: number): string {
  const [r, g, b] = parseHex(hex);
  return toHex(r * (1 - amount), g * (1 - amount), b * (1 - amount));
}
