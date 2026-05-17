/**
 * Colour helpers shared across canvas / SVG / HTML rendering. Kept minimal and dependency-free
 * so any layer (profiler canvas, panel SVG, plain DOM) can import without dragging in renderer
 * concepts.
 */

/**
 * WCAG 2 relative-luminance (Y) of an sRGB hex colour. Input must be `#rrggbb` or `#rgb`.
 * Returns 0..1; > 0.5 is "perceptually light, dark text wins" by the threshold convention.
 *
 * Lifted from `libs/profiler/canvas/timeArea.ts` so SVG / HTML callers can use the same maths
 * the canvas-based span renderer uses for its bar labels.
 */
export function relativeLuminance(hex: string): number {
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
 * Pick a high-contrast text colour for a given background `barHex`. Defaults: white on dark
 * backgrounds, black on light. Override `light` / `dark` to project-specific tones (e.g. a
 * theme's ink token instead of pure black/white).
 */
export function pickTextColorFor(barHex: string, light: string = '#000', dark: string = '#fff'): string {
  return relativeLuminance(barHex) > 0.5 ? light : dark;
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
