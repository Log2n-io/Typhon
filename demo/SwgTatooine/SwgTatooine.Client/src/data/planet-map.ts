/**
 * The planet map's geometry (Loïc, 2026-11-02): how a planet coordinate lands on the map's canvas, zooming about the cursor, panning,
 * and the relief drawn under the markers. Pure, so the tests hold the arithmetic and the window (`ui/PlanetMap.tsx`) only draws.
 *
 * <b>North is up</b>: +x is to the right and +z up, the way the 3D view faces at yaw 0 (`relativeBearing`'s convention), so a marker's
 * arrow turned by the camera's yaw points where the player looks.
 */

/** What the map shows: the planet point at the canvas centre, and how many canvas pixels a metre is. */
export interface MapView {
  readonly cx: number;
  readonly cz: number;
  readonly pxPerM: number;
}

/** The closest the map zooms: a few metres a pixel shows a city's streets on a 16 km planet. */
export const MAX_PX_PER_M = 0.5;

/** The view that fits the whole planet in a square canvas of `sizePx`, its edge `halfM` from the centre. */
export function fitView(sizePx: number, halfM: number): MapView {
  return { cx: 0, cz: 0, pxPerM: sizePx / (2 * halfM) };
}

/** A planet point on the canvas: x right, z up. */
export function toCanvas(
  view: MapView,
  sizePx: number,
  x: number,
  z: number,
): { readonly px: number; readonly py: number } {
  return { px: sizePx / 2 + (x - view.cx) * view.pxPerM, py: sizePx / 2 - (z - view.cz) * view.pxPerM };
}

/** The planet point under a canvas pixel. */
export function toPlanet(
  view: MapView,
  sizePx: number,
  px: number,
  py: number,
): { readonly x: number; readonly z: number } {
  return { x: view.cx + (px - sizePx / 2) / view.pxPerM, z: view.cz - (py - sizePx / 2) / view.pxPerM };
}

/**
 * Zoomed by `factor` about the canvas pixel under the cursor: the planet point there stays under it. Never out past the whole planet
 * nor in past {@link MAX_PX_PER_M}; the result is kept on the planet ({@link clampView}).
 */
export function zoomAt(view: MapView, sizePx: number, halfM: number, px: number, py: number, factor: number): MapView {
  const min = fitView(sizePx, halfM).pxPerM;
  const pxPerM = Math.min(MAX_PX_PER_M, Math.max(min, view.pxPerM * factor));
  const anchor = toPlanet(view, sizePx, px, py);
  const zoomed = {
    cx: anchor.x - (px - sizePx / 2) / pxPerM,
    cz: anchor.z + (py - sizePx / 2) / pxPerM,
    pxPerM,
  };
  return clampView(zoomed, sizePx, halfM);
}

/** Moved by a drag of `dx`, `dy` canvas pixels: the map follows the pointer. */
export function panBy(view: MapView, sizePx: number, halfM: number, dx: number, dy: number): MapView {
  return clampView(
    { cx: view.cx - dx / view.pxPerM, cz: view.cz + dy / view.pxPerM, pxPerM: view.pxPerM },
    sizePx,
    halfM,
  );
}

/** Kept so the canvas never shows past the planet's edge, as far as its zoom allows: a whole-planet view sits centred. */
export function clampView(view: MapView, sizePx: number, halfM: number): MapView {
  const halfShownM = sizePx / 2 / view.pxPerM;
  const room = Math.max(0, halfM - halfShownM);
  return {
    cx: Math.min(room, Math.max(-room, view.cx)),
    cz: Math.min(room, Math.max(-room, view.cz)),
    pxPerM: view.pxPerM,
  };
}

/** The relief's source: a square field of heights, row-major with z outer, as the terrain bake holds it. */
export interface ReliefSource {
  readonly height: Float32Array;
  readonly posts: number;
}

/**
 * The relief as RGBA pixels, `size × size`, north up: sand tinted by height, shaded by a light from the north-west. Rows of the field
 * run with z, so the image's first row is the field's last (the north edge). Sampled by nearest post, `posts / size` apart: the
 * field's 4 096 posts make a 1 024-pixel image in a few tens of milliseconds, once.
 */
export function reliefPixels(source: ReliefSource, size: number): Uint8ClampedArray<ArrayBuffer> {
  const { height, posts } = source;
  const out = new Uint8ClampedArray(size * size * 4);
  const step = posts / size;
  let min = Infinity;
  let max = -Infinity;
  for (let i = 0; i < height.length; i += 97) {
    const h = height[i];
    min = Math.min(min, h);
    max = Math.max(max, h);
  }

  const span = Math.max(1, max - min);
  const at = (col: number, row: number): number => {
    const c = Math.min(posts - 1, Math.max(0, Math.floor(col * step)));
    const r = Math.min(posts - 1, Math.max(0, Math.floor(row * step)));
    return height[r * posts + c];
  };

  for (let y = 0; y < size; y++) {
    const row = size - 1 - y;
    for (let x = 0; x < size; x++) {
      const h = at(x, row);
      // The slope towards the light: north-west is -x and +z, which is up the rows.
      const shade = (at(x - 1, row) - at(x + 1, row) + at(x, row + 1) - at(x, row - 1)) * 0.06;
      const t = (h - min) / span;
      const light = Math.max(0.55, Math.min(1.25, 0.95 + shade));
      const o = (y * size + x) * 4;
      out[o] = (150 + 80 * t) * light;
      out[o + 1] = (118 + 70 * t) * light;
      out[o + 2] = (78 + 50 * t) * light;
      out[o + 3] = 255;
    }
  }

  return out;
}
