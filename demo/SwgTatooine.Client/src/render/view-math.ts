/**
 * Per-frame view math on the CPU, in render space (planet coordinates minus the render origin), float64 throughout.
 * Babylon matrices are row-major arrays that transform row vectors: `clip = (x, y, z, 1) · M`.
 */

/** Band of the level of detail: a mesh, or a screen-sized sprite. */
export const Band = { Unset: 0, Near: 1, Far: 2 } as const;

/** Enter the near band at this many projected pixels, leave it below {@link LEAVE_NEAR_PX}: hysteresis, no flicker. */
export const ENTER_NEAR_PX = 7;
export const LEAVE_NEAR_PX = 5;

export function chooseBand(previous: number, pixels: number): number {
  if (previous === Band.Near) {
    return pixels < LEAVE_NEAR_PX ? Band.Far : Band.Near;
  }

  if (previous === Band.Far) {
    return pixels >= ENTER_NEAR_PX ? Band.Near : Band.Far;
  }

  return pixels >= (ENTER_NEAR_PX + LEAVE_NEAR_PX) / 2 ? Band.Near : Band.Far;
}

/**
 * The per-instance integer the shaders unpack: bits 0–3 style index, 4–6 mode, 7 selected. A style index outside
 * [0, {@link STYLE_LIMIT}) draws as style 0 rather than reading past the shaders' arrays.
 */
export const STYLE_LIMIT = 16;
export const MODE_LIMIT = 8;
export const SELECTED_BIT = STYLE_LIMIT * MODE_LIMIT;

export function packState(style: number, mode: number, selected: boolean): number {
  const s = style >= 0 && style < STYLE_LIMIT ? style | 0 : 0;
  return s + ((mode | 0) & (MODE_LIMIT - 1)) * STYLE_LIMIT + (selected ? SELECTED_BIT : 0);
}

/** Style index of a packed instance. */
export function packedStyle(packed: number): number {
  return packed % STYLE_LIMIT;
}

/** Six frustum planes `(a, b, c, d)` from a view-projection matrix, normalised so `a·x + b·y + c·z + d` is a distance. */
export function extractFrustumPlanes(m: ArrayLike<number>, out: Float64Array): void {
  // Gribb–Hartmann: plane = column 3 ± column k, where column k holds the coefficients of clip coordinate k.
  for (let p = 0; p < 6; p++) {
    const k = p >> 1;
    const sign = (p & 1) === 0 ? 1 : -1;
    const a = m[3] + sign * m[k];
    const b = m[7] + sign * m[4 + k];
    const c = m[11] + sign * m[8 + k];
    const d = m[15] + sign * m[12 + k];
    const len = Math.hypot(a, b, c) || 1;
    out[p * 4] = a / len;
    out[p * 4 + 1] = b / len;
    out[p * 4 + 2] = c / len;
    out[p * 4 + 3] = d / len;
  }
}

/** Whether a sphere is at least partly inside all six planes. */
export function sphereInFrustum(planes: Float64Array, x: number, y: number, z: number, radius: number): boolean {
  for (let p = 0; p < 24; p += 4) {
    if (planes[p] * x + planes[p + 1] * y + planes[p + 2] * z + planes[p + 3] < -radius) {
      return false;
    }
  }

  return true;
}

export interface ScreenPoint {
  x: number;
  y: number;
  /** Clip w: the distance along the view direction; ≤ 0 means behind the camera. */
  w: number;
}

/** Projects a render-space point to viewport pixels (origin top-left). */
export function projectToScreen(
  m: ArrayLike<number>,
  x: number,
  y: number,
  z: number,
  width: number,
  height: number,
  out: ScreenPoint,
): ScreenPoint {
  const cx = x * m[0] + y * m[4] + z * m[8] + m[12];
  const cy = x * m[1] + y * m[5] + z * m[9] + m[13];
  const cw = x * m[3] + y * m[7] + z * m[11] + m[15];
  out.w = cw;
  if (cw <= 1e-6) {
    out.x = Number.NaN;
    out.y = Number.NaN;
    return out;
  }

  out.x = ((cx / cw) * 0.5 + 0.5) * width;
  out.y = (1 - ((cy / cw) * 0.5 + 0.5)) * height;
  return out;
}

/**
 * The render origin: a point near the camera, snapped to a 1 km grid, subtracted from every position before it reaches
 * the GPU. Float32 then only holds values within a few kilometres, so nothing jitters when the camera is close.
 */
export class RenderOrigin {
  x = 0;
  z = 0;
  private readonly snapM: number;

  constructor(snapM = 1024) {
    this.snapM = snapM;
  }

  /** Re-centres on a planet point when it has moved a full snap away; returns true when the origin changed. */
  follow(x: number, z: number): boolean {
    const nx = Math.round(x / this.snapM) * this.snapM;
    const nz = Math.round(z / this.snapM) * this.snapM;
    if (nx === this.x && nz === this.z) {
      return false;
    }

    this.x = nx;
    this.z = nz;
    return true;
  }
}

export interface PickHit {
  archetype: number;
  netId: number;
  /** Screen distance from the cursor, pixels. */
  pixels: number;
  /** Clip w of the hit: nearer wins a tie. */
  depth: number;
}

/**
 * Picks among packed instances (`x, z, yaw, packed` at stride 4, render space): the one whose lifted centre projects
 * nearest the cursor within `maxPixels`, nearer the camera on a half-pixel tie. Updates `best` only when it improves.
 * `netIds[k]` is the entity packed at `k` — captured at pack time, so a store changed since then cannot redirect the pick.
 */
export function pickInstances(
  matrix: ArrayLike<number>,
  data: Float32Array,
  netIds: Uint32Array,
  count: number,
  liftByStyle: Float64Array | number,
  width: number,
  height: number,
  px: number,
  py: number,
  maxPixels: number,
  archetype: number,
  best: PickHit,
  screen: ScreenPoint,
): void {
  for (let k = 0; k < count; k++) {
    const b = k * 4;
    const lift = typeof liftByStyle === 'number' ? liftByStyle : liftByStyle[packedStyle(data[b + 3])];
    const s = projectToScreen(matrix, data[b], lift, data[b + 1], width, height, screen);
    // A point behind the camera projects to NaN, which is never within reach.
    const d = Math.hypot(s.x - px, s.y - py);
    if (d <= maxPixels && (d < best.pixels - 0.5 || (Math.abs(d - best.pixels) <= 0.5 && s.w < best.depth))) {
      best.archetype = archetype;
      best.netId = netIds[k];
      best.pixels = d;
      best.depth = s.w;
    }
  }
}
