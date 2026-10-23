import { PLANET_HALF_EXTENT_M } from '../data/world-data';
import type { CameraBounds } from '../render/scene-profile';

/**
 * The god-mode camera: a ground target, a distance, a yaw and a pitch, all in planet coordinates (float64). It never
 * touches Babylon; the scene reads {@link eyeX}/{@link eyeY}/{@link eyeZ} and {@link pitch}/{@link yaw} each frame.
 *
 * Map controls: drag to pan (the ground point under the cursor stays under it), right-drag to orbit, wheel to zoom toward
 * the cursor, WASD / arrows to pan at a speed proportional to altitude, Q/E to rotate. Motion is smoothed toward goal
 * values, frame-rate independent.
 *
 * The eye stays above the ground (pitch and distance floors: at least a metre or so up). <b>The draw order no longer
 * relies on that</b> — it did while the ground drew alone in its own group, and terrain rung (a) collapsed the two groups
 * into one depth buffer (`render/render-groups.ts`), so depth testing is the occlusion now. What still wants the eye above
 * the floor is simply that a camera under the ground sees the inside of it.
 *
 * <b>Its limits come from the realm</b> ({@link setBounds}), not from the planet. They used to be module constants and a
 * clamp to ±`PLANET_HALF_EXTENT_M`, which is a box a 64 m room does not occupy: a camera in an interior would have panned
 * eight kilometres off the end of the world it was in.
 */

export const MIN_DISTANCE_M = 6;
export const MAX_DISTANCE_M = 22_000;
const MAX_PITCH = (89 * Math.PI) / 180;

/**
 * The planet, which is what a camera is looking at before any realm frame has arrived.
 *
 * Not a neutral default: a camera that started unbounded would pan anywhere in the first second of a session, and one
 * that started tiny would refuse to move. The world this client opens on is the right thing to assume.
 */
const PLANET_BOUNDS: CameraBounds = {
  minX: -PLANET_HALF_EXTENT_M,
  maxX: PLANET_HALF_EXTENT_M,
  minZ: -PLANET_HALF_EXTENT_M,
  maxZ: PLANET_HALF_EXTENT_M,
  minDistanceM: MIN_DISTANCE_M,
  maxDistanceM: MAX_DISTANCE_M,
  minPitch: (12 * Math.PI) / 180,
};
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

  /** Where this camera may go, from the realm it is in. Replaced on a realm switch by {@link setBounds}. */
  private bounds: CameraBounds = PLANET_BOUNDS;

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
  /**
   * Terrain height under {@link targetX}, {@link targetZ}, in metres. The owner writes it each frame from the heightfield;
   * the camera orbits the ground point rather than sea level, which is what keeps the eye above the terrain.
   */
  groundY = 0;
  eyeZ = 0;

  constructor() {
    this.updateEye();
  }

  /**
   * Takes this camera's limits from a realm, and pulls it inside them at once.
   *
   * <b>Clamping immediately matters.</b> A switch sets the bounds and then jumps the camera, but a session panned to the
   * far edge of a planet and dropped into a 64 m room would otherwise hold coordinates outside its own world until the
   * next input arrived — and `groundY`, the pick and the region are all read from there in the frames between.
   */
  setBounds(bounds: CameraBounds): void {
    this.bounds = bounds;

    // The goal is clamped separately from the position rather than being overwritten by it: assigning
    // `goalX = targetX` would cancel any glide in flight, so calling this on a realm that had not actually changed
    // would silently stop a `follow()` mid-move.
    this.goalX = this.clampX(this.goalX);
    this.goalZ = this.clampZ(this.goalZ);
    this.targetX = this.clampX(this.targetX);
    this.targetZ = this.clampZ(this.targetZ);
    this.goalDistance = this.distance = clamp(this.distance, bounds.minDistanceM, bounds.maxDistanceM);
    this.goalPitch = this.pitch = clamp(this.pitch, bounds.minPitch, MAX_PITCH);
    this.updateEye();
  }

  private clampX(v: number): number {
    return clamp(v, this.bounds.minX, this.bounds.maxX);
  }

  private clampZ(v: number): number {
    return clamp(v, this.bounds.minZ, this.bounds.maxZ);
  }

  /** Places the camera immediately, without smoothing. */
  jumpTo(x: number, z: number, distance = this.goalDistance): void {
    this.goalX = this.targetX = this.clampX(x);
    this.goalZ = this.targetZ = this.clampZ(z);
    this.goalDistance = this.distance = clamp(distance, this.bounds.minDistanceM, this.bounds.maxDistanceM);
    this.updateEye();
  }

  /** Moves the goal; the camera glides there. */
  glideTo(x: number, z: number, distance?: number): void {
    this.goalX = this.clampX(x);
    this.goalZ = this.clampZ(z);
    if (distance !== undefined) {
      this.goalDistance = clamp(distance, this.bounds.minDistanceM, this.bounds.maxDistanceM);
    }
  }

  /** Follows a moving point: the goal tracks it, so switching to a distant entity glides instead of cutting. */
  follow(x: number, z: number): void {
    this.goalX = this.clampX(x);
    this.goalZ = this.clampZ(z);
  }

  /** Pans by a ground-plane offset in metres. */
  panBy(dx: number, dz: number): void {
    this.goalX = this.clampX(this.goalX + dx);
    this.goalZ = this.clampZ(this.goalZ + dz);
  }

  /** Moves the camera with the cursor during a drag: target and goal together, no smoothing lag. */
  dragBy(dx: number, dz: number): void {
    this.goalX = this.targetX = this.clampX(this.targetX + dx);
    this.goalZ = this.targetZ = this.clampZ(this.targetZ + dz);
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
    this.goalPitch = clamp(this.goalPitch + dPitch, this.bounds.minPitch, MAX_PITCH);
  }

  /** Zooms by a factor (< 1 closer), keeping the ground point `(gx, gz)` fixed on screen when given. */
  zoomBy(factor: number, gx?: number, gz?: number): void {
    const next = clamp(this.goalDistance * factor, this.bounds.minDistanceM, this.bounds.maxDistanceM);
    const applied = next / this.goalDistance;
    if (gx !== undefined && gz !== undefined) {
      this.goalX = this.clampX(gx + (this.goalX - gx) * applied);
      this.goalZ = this.clampZ(gz + (this.goalZ - gz) * applied);
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
    // The DISTANCE from the plane, not the signed height above it. Below the ecliptic — where a camera in space spends
    // half its time — the signed form collapses to the 1e-3 floor and this returns about 23 m, so `groundHit`'s
    // `t <= maxDistance` then rejects a ray whose real t is ten kilometres. That is the same silent death of
    // pan- and zoom-to-cursor the sign test in `groundHit` caused, reached by a second route: fixing one without the
    // other fixes nothing a viewer can see.
    const height = Math.max(Math.abs(this.eyeY - this.groundY), 1e-3);
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
   * Where a ray meets the ground plane, into `out` (x, z); false when it points at the sky or meets the ground farther than
   * `maxDistance` from its origin.
   *
   * The plane is `y = planeY`, which the camera passes as {@link groundY} — the terrain height under its own target rather
   * than sea level. It is an approximation of the real surface and a deliberate one: a drag must move the ground under the
   * cursor by exactly what the cursor moved, and a ray marched against the true heightfield makes the pan speed depend on
   * whatever slope the cursor happens to be over, which reads as the map sticking and slipping.
   */
  static groundHit(ray: Ray, out: Float64Array, maxDistance = Infinity, planeY = 0): boolean {
    // Guarded on the MAGNITUDE of dy, not its sign. It used to reject any ray that was not pointing down, which is
    // right for a camera that can only be above the plane and wrong the moment one can be under it: in space the eye
    // orbits below the ecliptic and looks up, and a sign test silently turned off pan- and zoom-to-cursor there — no
    // error, just dead input. The direction test is already `t >= 0` below, which is correct from either side; all this
    // has to exclude is a ray so nearly parallel to the plane that the division is meaningless.
    if (Math.abs(ray.dy) < 1e-6) {
      return false;
    }

    const t = (planeY - ray.oy) / ray.dy;
    if (!(t >= 0 && t <= maxDistance)) {
      return false;
    }

    out[0] = ray.ox + ray.dx * t;
    out[1] = ray.oz + ray.dz * t;
    return true;
  }

  private updateEye(): void {
    const cp = Math.cos(this.pitch);
    this.eyeX = this.targetX - cp * Math.sin(this.yaw) * this.distance;
    // Above the GROUND under the target, not above sea level. The pitch and distance floors were what kept the eye above
    // the plane `y = 0`; once the ground can rise to 130 m they stop being enough, and a camera inside a mesa sees the
    // inside of it — the ground draws with back faces on, so the symptom is a featureless brown screen rather than
    // anything that looks like a camera bug.
    this.eyeY = this.groundY + Math.sin(this.pitch) * this.distance;
    this.eyeZ = this.targetZ - cp * Math.cos(this.yaw) * this.distance;
  }
}

function clamp(v: number, min: number, max: number): number {
  return v < min ? min : v > max ? max : v;
}
