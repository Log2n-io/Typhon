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
