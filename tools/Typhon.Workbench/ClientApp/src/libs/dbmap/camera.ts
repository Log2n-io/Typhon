// Hand-rolled 2D affine camera for the Database File Map (Module 15, §6.6).
//
// One world space (page-cell units); the camera is a uniform scale + translation. Deliberately not d3-zoom —
// its event model collides with custom canvas hit-testing. All transforms are pure and return a new Camera.

/** Screen = world × scale + offset. */
export interface Camera {
  scale: number;
  x: number;
  y: number;
}

/** An axis-aligned rectangle (world or screen units). */
export interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

/** Minimum / maximum zoom — the ~10^7 dynamic range the map must span without losing the page grid. */
const MIN_SCALE = 1e-4;
const MAX_SCALE = 1e5;

function clampScale(scale: number): number {
  return Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale));
}

export function worldToScreenX(cam: Camera, wx: number): number {
  return wx * cam.scale + cam.x;
}

export function worldToScreenY(cam: Camera, wy: number): number {
  return wy * cam.scale + cam.y;
}

export function screenToWorldX(cam: Camera, sx: number): number {
  return (sx - cam.x) / cam.scale;
}

export function screenToWorldY(cam: Camera, sy: number): number {
  return (sy - cam.y) / cam.scale;
}

/** Pans the camera by a screen-pixel delta. */
export function panBy(cam: Camera, dxScreen: number, dyScreen: number): Camera {
  return { scale: cam.scale, x: cam.x + dxScreen, y: cam.y + dyScreen };
}

/** Zooms by `factor` while keeping the world point under (`screenX`, `screenY`) fixed. */
export function zoomAt(cam: Camera, screenX: number, screenY: number, factor: number): Camera {
  const newScale = clampScale(cam.scale * factor);
  const effective = newScale / cam.scale;
  // Keep the cursor's world point pinned: newOffset = screen - worldPoint × newScale.
  return {
    scale: newScale,
    x: screenX - (screenX - cam.x) * effective,
    y: screenY - (screenY - cam.y) * effective,
  };
}

/** Fits `world` into a `viewportW` × `viewportH` viewport, centred, with `padding` screen-pixel margin. */
export function fitToRect(world: Rect, viewportW: number, viewportH: number, padding: number): Camera {
  const availW = Math.max(1, viewportW - 2 * padding);
  const availH = Math.max(1, viewportH - 2 * padding);
  const scale = clampScale(Math.min(availW / Math.max(world.w, 1e-9), availH / Math.max(world.h, 1e-9)));
  return {
    scale,
    x: (viewportW - world.w * scale) / 2 - world.x * scale,
    y: (viewportH - world.h * scale) / 2 - world.y * scale,
  };
}

/** Zooms-to-fit an arbitrary world rectangle (drag-zoom-to-region). */
export function zoomToWorldRect(world: Rect, viewportW: number, viewportH: number, padding: number): Camera {
  return fitToRect(world, viewportW, viewportH, padding);
}

/** The world-space rectangle currently visible in a `viewportW` × `viewportH` viewport. */
export function visibleWorldRect(cam: Camera, viewportW: number, viewportH: number): Rect {
  const x = screenToWorldX(cam, 0);
  const y = screenToWorldY(cam, 0);
  return {
    x,
    y,
    w: viewportW / cam.scale,
    h: viewportH / cam.scale,
  };
}

/** A camera that centres world point (`worldX`, `worldY`) at `scale` in a `viewportW` × `viewportH` viewport. */
export function cameraCenteredOn(
  worldX: number,
  worldY: number,
  scale: number,
  viewportW: number,
  viewportH: number,
): Camera {
  const s = clampScale(scale);
  return { scale: s, x: viewportW / 2 - worldX * s, y: viewportH / 2 - worldY * s };
}

// ── Animated fly-to (A3, §4.5) ──────────────────────────────────────────────────────────────────────────────

/** A camera animation from `from` to `to`, played over `durationMs` from the `startMs` (`performance.now()`) stamp. */
export interface CameraTween {
  from: Camera;
  to: Camera;
  startMs: number;
  durationMs: number;
  /**
   * Screen point held fixed during the glide — the world point under it is interpolated, so it stays put.
   * Defaults to the viewport centre. A cursor-anchored wheel zoom must pass the cursor here: otherwise the
   * centre-anchored interpolation makes the focus point bulge off-and-back mid-glide (a visible wobble),
   * because `1/scale` (log-eased) and the centre point (linearly eased) do not cancel between the endpoints.
   */
  anchorX?: number;
  anchorY?: number;
}

function lerp(a: number, b: number, t: number): number {
  return a + (b - a) * t;
}

/**
 * Samples a camera tween at `nowMs`. The anchor's world point moves linearly while the scale moves in log
 * space — a linear scale lerp across the map's ~10^7 zoom range would feel wrong, cramming most of the travel
 * into the first frames. Eased out (cubic); `done` is true once the tween reaches its end.
 */
export function tweenCamera(
  tween: CameraTween,
  nowMs: number,
  viewportW: number,
  viewportH: number,
): { camera: Camera; done: boolean } {
  const raw = tween.durationMs <= 0 ? 1 : (nowMs - tween.startMs) / tween.durationMs;
  const t = raw <= 0 ? 0 : raw >= 1 ? 1 : raw;
  const e = 1 - Math.pow(1 - t, 3);
  const scale = Math.exp(lerp(Math.log(tween.from.scale), Math.log(tween.to.scale), e));
  // Hold the anchor screen point fixed by interpolating the world point beneath it. For a pure zoom (wheel),
  // `from` and `to` agree on that point, so it stays exactly pinned through the whole glide — no wobble.
  const ax = tween.anchorX ?? viewportW / 2;
  const ay = tween.anchorY ?? viewportH / 2;
  const wx = lerp(screenToWorldX(tween.from, ax), screenToWorldX(tween.to, ax), e);
  const wy = lerp(screenToWorldY(tween.from, ay), screenToWorldY(tween.to, ay), e);
  return {
    camera: { scale, x: ax - wx * scale, y: ay - wy * scale },
    done: t >= 1,
  };
}
