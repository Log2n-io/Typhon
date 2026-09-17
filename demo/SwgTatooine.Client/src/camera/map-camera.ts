import { PLANET_HALF_EXTENT_M } from '../data/world-data';

/**
 * The god-mode camera: a ground target, a distance, a yaw and a pitch, all in planet coordinates (float64). It never
 * touches Babylon; the scene reads {@link eyeX}/{@link eyeY}/{@link eyeZ} and {@link pitch}/{@link yaw} each frame.
 *
 * Map controls: drag to pan (the ground point under the cursor stays under it), right-drag to orbit, wheel to zoom toward
 * the cursor, WASD / arrows to pan at a speed proportional to altitude, Q/E to rotate. Motion is smoothed toward goal
 * values, frame-rate independent.
 *
 * The eye stays above the ground (pitch ≥ 12°, distance ≥ 6 m: at least 1.25 m up). The draw order relies on it: the ground
 * draws first and never hides anything (`render/render-groups.ts`). A camera that could go below y = 0 must change that.
 */

export const MIN_DISTANCE_M = 6;
export const MAX_DISTANCE_M = 22_000;
const MIN_PITCH = (12 * Math.PI) / 180;
const MAX_PITCH = (89 * Math.PI) / 180;
const SMOOTHING_PER_SECOND = 14;
/** A cursor pixel may move its ground hit by at most this fraction of the camera distance (see `maxGroundHitM`). */
export const MAX_METRES_PER_PIXEL_RATIO = 0.05;

export interface Ray {
  ox: number;
  oy: number;
  oz: number;
  dx: number;
  dy: number;
  dz: number;
}

export class MapCamera {
  /** Vertical field of view, radians. */
  readonly fov = 0.8;

  targetX = 0;
  targetZ = 0;
  distance = 3000;
  yaw = 0;
  pitch = (55 * Math.PI) / 180;

  private goalX = 0;
  private goalZ = 0;
  private goalDistance = 3000;
  private goalYaw = 0;
  private goalPitch = (55 * Math.PI) / 180;

  eyeX = 0;
  eyeY = 0;
  eyeZ = 0;

  constructor() {
    this.updateEye();
  }

  /** Places the camera immediately, without smoothing. */
  jumpTo(x: number, z: number, distance = this.goalDistance): void {
    this.goalX = this.targetX = clampPlanet(x);
    this.goalZ = this.targetZ = clampPlanet(z);
    this.goalDistance = this.distance = clamp(distance, MIN_DISTANCE_M, MAX_DISTANCE_M);
    this.updateEye();
  }

  /** Moves the goal; the camera glides there. */
  glideTo(x: number, z: number, distance?: number): void {
    this.goalX = clampPlanet(x);
    this.goalZ = clampPlanet(z);
    if (distance !== undefined) {
      this.goalDistance = clamp(distance, MIN_DISTANCE_M, MAX_DISTANCE_M);
    }
  }

  /** Follows a moving point: the goal tracks it, so switching to a distant entity glides instead of cutting. */
  follow(x: number, z: number): void {
    this.goalX = clampPlanet(x);
    this.goalZ = clampPlanet(z);
  }

  /** Pans by a ground-plane offset in metres. */
  panBy(dx: number, dz: number): void {
    this.goalX = clampPlanet(this.goalX + dx);
    this.goalZ = clampPlanet(this.goalZ + dz);
  }

  /** Moves the camera with the cursor during a drag: target and goal together, no smoothing lag. */
  dragBy(dx: number, dz: number): void {
    this.goalX = this.targetX = clampPlanet(this.targetX + dx);
    this.goalZ = this.targetZ = clampPlanet(this.targetZ + dz);
    this.updateEye();
  }

  /** Pans in screen directions: `right` and `forward` in metres along the camera's ground axes. */
  panScreen(right: number, forward: number): void {
    const s = Math.sin(this.goalYaw);
    const c = Math.cos(this.goalYaw);
    this.panBy(right * c + forward * s, -right * s + forward * c);
  }

  rotateBy(dYaw: number, dPitch: number): void {
    this.goalYaw += dYaw;
    this.goalPitch = clamp(this.goalPitch + dPitch, MIN_PITCH, MAX_PITCH);
  }

  /** Zooms by a factor (< 1 closer), keeping the ground point `(gx, gz)` fixed on screen when given. */
  zoomBy(factor: number, gx?: number, gz?: number): void {
    const next = clamp(this.goalDistance * factor, MIN_DISTANCE_M, MAX_DISTANCE_M);
    const applied = next / this.goalDistance;
    if (gx !== undefined && gz !== undefined) {
      this.goalX = clampPlanet(gx + (this.goalX - gx) * applied);
      this.goalZ = clampPlanet(gz + (this.goalZ - gz) * applied);
    }

    this.goalDistance = next;
  }

  /** Advances the smoothing by `dtSeconds`. */
  update(dtSeconds: number): void {
    const k = 1 - Math.exp(-SMOOTHING_PER_SECOND * Math.max(0, dtSeconds));
    this.targetX += (this.goalX - this.targetX) * k;
    this.targetZ += (this.goalZ - this.targetZ) * k;
    this.distance *= Math.pow(this.goalDistance / this.distance, k);
    this.yaw += (this.goalYaw - this.yaw) * k;
    this.pitch += (this.goalPitch - this.pitch) * k;
    this.updateEye();
  }

  get altitude(): number {
    return this.eyeY;
  }

  /** Ground distance units per second for keyboard panning. */
  get panSpeed(): number {
    return Math.max(20, this.distance * 0.9);
  }

  /** Near clip plane: it scales with distance, which keeps depth precision at altitude. */
  get nearPlane(): number {
    return clamp(this.distance * 0.002, 0.2, 25);
  }

  get farPlane(): number {
    return Math.max(40_000, this.distance * 6);
  }

  /**
   * Farthest ground hit worth using for a cursor on a viewport `heightPx` tall: the ray length at which one pixel moves the
   * hit by {@link MAX_METRES_PER_PIXEL_RATIO} of the camera distance. Near the horizon a pixel spans kilometres, and a drag
   * or a zoom anchored there would leap.
   *
   * A ray at angle α below the horizon from eye height H meets the ground at range H / tan α; one pixel (angle δ) moves that
   * by H·δ / sin²α. Bounding it by k·distance gives sin α ≥ √(H·δ / (k·distance)), a ray length of H / sin α.
   */
  maxGroundHitM(heightPx: number): number {
    const pixelAngle = (2 * Math.tan(this.fov / 2)) / Math.max(1, heightPx);
    const height = Math.max(this.eyeY, 1e-3);
    const sinBelow = Math.min(1, Math.sqrt((height * pixelAngle) / (MAX_METRES_PER_PIXEL_RATIO * this.distance)));
    return height / sinBelow;
  }

  /**
   * World ray through a viewport pixel (planet coordinates), for a viewport of `width × height` pixels.
   * Writes into `out` and returns it.
   */
  rayThrough(px: number, py: number, width: number, height: number, out: Ray): Ray {
    const aspect = width / Math.max(1, height);
    const t = Math.tan(this.fov / 2);
    const nx = ((px / width) * 2 - 1) * t * aspect;
    const ny = (1 - (py / height) * 2) * t;
    // Camera basis: forward looks at the target; right is horizontal; up completes the frame.
    const cp = Math.cos(this.pitch);
    const sp = Math.sin(this.pitch);
    const sy = Math.sin(this.yaw);
    const cy = Math.cos(this.yaw);
    const fx = cp * sy;
    const fy = -sp;
    const fz = cp * cy;
    const rx = cy;
    const rz = -sy;
    const ux = sp * sy;
    const uy = cp;
    const uz = sp * cy;
    const dx = fx + rx * nx + ux * ny;
    const dy = fy + uy * ny;
    const dz = fz + rz * nx + uz * ny;
    const len = Math.hypot(dx, dy, dz);
    out.ox = this.eyeX;
    out.oy = this.eyeY;
    out.oz = this.eyeZ;
    out.dx = dx / len;
    out.dy = dy / len;
    out.dz = dz / len;
    return out;
  }

  /**
   * Where a ray meets the ground (y = 0), into `out` (x, z); false when it points at the sky or meets the ground farther than
   * `maxDistance` from its origin.
   */
  static groundHit(ray: Ray, out: Float64Array, maxDistance = Infinity): boolean {
    if (ray.dy >= -1e-6) {
      return false;
    }

    const t = -ray.oy / ray.dy;
    if (!(t <= maxDistance)) {
      return false;
    }

    out[0] = ray.ox + ray.dx * t;
    out[1] = ray.oz + ray.dz * t;
    return true;
  }

  private updateEye(): void {
    const cp = Math.cos(this.pitch);
    this.eyeX = this.targetX - cp * Math.sin(this.yaw) * this.distance;
    this.eyeY = Math.sin(this.pitch) * this.distance;
    this.eyeZ = this.targetZ - cp * Math.cos(this.yaw) * this.distance;
  }
}

function clamp(v: number, min: number, max: number): number {
  return v < min ? min : v > max ? max : v;
}

function clampPlanet(v: number): number {
  return clamp(v, -PLANET_HALF_EXTENT_M, PLANET_HALF_EXTENT_M);
}
