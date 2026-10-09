import { FLAT_GROUND, type GroundSampler } from '../terrain/ground-sampler';
/**
 * The camera that stands in the world rather than over it (CLI3D-10): it rides an entity, at eye height, looking where
 * the free-look is aimed. One parameter — {@link distance} — takes it from first person (0) to over the shoulder.
 *
 * **Why a second camera and not a mode on `MapCamera`.** The map camera is an orbit rig: a ground target, a distance, a
 * yaw and a pitch, with a floor on distance and pitch *precisely* so the eye can never go below `y = 0`
 * (`map-camera.ts`, and the draw order depends on it). This one has the opposite job — the eye is the subject's, the
 * target is wherever it looks, and the pitch must reach the horizon and above. Folding both into one class would mean
 * every clamp carrying a mode flag, which is how the one invariant that matters gets lost.
 *
 * **It has no authority.** It reads a position and a heading that the simulation produced and the store replicated;
 * there is no input, no prediction, no client-side movement. Spectating, not playing — the server-side half that makes
 * this a *player's* view (`Follow`, the player profile, interiors) is a separate piece of work.
 *
 * It reports `eyeX`/`eyeY`/`eyeZ`, `yaw`, `pitch`, `fov`, `nearPlane` and `farPlane` exactly as `MapCamera` does, so the
 * renderer reads whichever is active without knowing which it is.
 */

/** Height of the eye above the followed entity's own origin, metres. A human's, near enough to read as one. */
export const EYE_HEIGHT_M = 1.7;

/**
 * Pitch bounds, in `MapCamera`'s sign convention — **positive is looking down**, because that is what the scene's
 * `camera.rotation.set(pitch, yaw, 0)` means and the map camera is clamped to 12°…89° for exactly that reason. Here it
 * runs either side of level and stops short of vertical, where yaw stops meaning anything.
 */
const MIN_PITCH = (-85 * Math.PI) / 180;
const MAX_PITCH = (85 * Math.PI) / 180;

/** Over-the-shoulder distance bounds. 0 is first person. */
export const MAX_EYE_DISTANCE_M = 30;

/**
 * How fast the view settles. Looser than the map camera's 14: a followed entity changes heading abruptly — an NPC
 * turning a corner, a creature snapping to a new target — and a stiff camera makes that a jolt in the viewer's neck.
 */
const SMOOTHING_PER_SECOND = 6;

/**
 * How many points along the pull-back ray are tested against the ground. Eight is enough for a 30 m ray over terrain whose
 * steepest slope is 60°, and it is eight bilinear samples a frame — nothing against a 2 ms budget.
 */
const GROUND_PROBE_STEPS = 8;

/** How far the eye must clear the ground. Small: this is a spectator camera, not a character controller. */
const GROUND_CLEARANCE_M = 0.35;

export class EyeCamera {
  /**
   * The ground the eye may not go inside. Flat until the app hands it the baked field, which is also what every test that
   * does not care about terrain gets.
   */
  ground: GroundSampler = FLAT_GROUND;

  /** Vertical field of view, radians. Wider than the map's 0.8: a person's view, not a map's. */
  readonly fov = 1.1;

  /** The followed entity's position, in planet coordinates. Written each frame by the app. */
  subjectX = 0;
  subjectY = 0;
  subjectZ = 0;

  /** Where the eye is, after smoothing. */
  eyeX = 0;
  eyeY = 0;
  eyeZ = 0;

  yaw = 0;
  pitch = 0;

  /** 0 = first person; larger pulls the eye back along the view, over the subject's shoulder. */
  distance = 0;

  private goalYaw = 0;
  private goalPitch = 0;
  private goalDistance = 0;
  /** The smoothed anchor, so a subject that jumps does not snap the eye with it. */
  private anchorX = 0;
  private anchorY = 0;
  private anchorZ = 0;
  private placed = false;

  /**
   * Points the camera at a subject without smoothing — entering the mode, or changing subject.
   *
   * @param x Subject x, planet metres.
   * @param y Subject y (its own altitude), planet metres.
   * @param z Subject z, planet metres.
   * @param heading Facing, radians, in the renderer's convention (0 along +z).
   */
  jumpTo(x: number, y: number, z: number, heading: number): void {
    this.subjectX = this.anchorX = x;
    this.subjectY = this.anchorY = y;
    this.subjectZ = this.anchorZ = z;
    this.yaw = this.goalYaw = heading;
    this.pitch = this.goalPitch = 0;
    this.distance = this.goalDistance;
    this.placed = true;
    this.updateEye();
  }

  /** The subject moved. Its position is taken as given; the eye glides to it. */
  follow(x: number, y: number, z: number): void {
    this.subjectX = x;
    this.subjectY = y;
    this.subjectZ = z;
    if (!this.placed) {
      this.jumpTo(x, y, z, this.yaw);
    }
  }

  /** Free-look. Yaw is unbounded and wraps naturally; pitch stops short of vertical. */
  rotateBy(dYaw: number, dPitch: number): void {
    this.goalYaw += dYaw;
    this.goalPitch = clamp(this.goalPitch + dPitch, MIN_PITCH, MAX_PITCH);
  }

  /** Pulls the eye back from the subject, or pushes it in. `factor < 1` moves toward first person. */
  zoomBy(factor: number): void {
    // From exactly 0, a multiply can never leave it: stepping out of first person needs an additive nudge.
    const from = this.goalDistance === 0 ? (factor > 1 ? 1.5 : 0) : this.goalDistance * factor;
    this.goalDistance = clamp(from, 0, MAX_EYE_DISTANCE_M);
  }

  /** Snaps the view back behind the subject, the way a follow camera does when the player stops turning it. */
  faceHeading(heading: number): void {
    this.goalYaw = heading;
  }

  /**
   * Advances the smoothing.
   *
   * @param dtSeconds Elapsed seconds.
   */
  update(dtSeconds: number): void {
    const k = 1 - Math.exp(-SMOOTHING_PER_SECOND * Math.max(0, dtSeconds));
    this.anchorX += (this.subjectX - this.anchorX) * k;
    this.anchorY += (this.subjectY - this.anchorY) * k;
    this.anchorZ += (this.subjectZ - this.anchorZ) * k;
    this.yaw += (this.goalYaw - this.yaw) * k;
    this.pitch += (this.goalPitch - this.pitch) * k;
    this.distance += (this.goalDistance - this.distance) * k;
    this.updateEye();
  }

  /** What the ground-relative HUD calls altitude: height above the plane, not above the subject. */
  get altitude(): number {
    return this.eyeY;
  }

  /**
   * The ground point of interest, which for this camera is the subject it rides. Named to match `MapCamera` so the
   * render origin follows whichever camera is active without asking which it is.
   */
  get targetX(): number {
    return this.anchorX;
  }

  get targetZ(): number {
    return this.anchorZ;
  }

  /**
   * Near clip. Fixed and small: unlike the map camera's, it cannot scale with distance, because at eye level the
   * interesting geometry is a metre away and a near plane derived from a 3 km orbit would clip the subject's own feet.
   */
  get nearPlane(): number {
    return 0.15;
  }

  get farPlane(): number {
    return 40_000;
  }

  private updateEye(): void {
    const head = this.anchorY + EYE_HEIGHT_M;
    // Pulling back travels BACKWARD along the view ray, so an over-the-shoulder eye rises as it pitches down — which is
    // what keeps the subject in frame instead of sliding up out of it.
    const cp = Math.cos(this.pitch);
    const dx = -Math.sin(this.yaw) * cp;
    const dz = -Math.cos(this.yaw) * cp;
    const dy = Math.sin(this.pitch);

    // Walk out along that ray and stop before the ground. Without this the eye goes **inside the terrain**: 30 m back at
    // level pitch is inside any mesa, and looking up while pulled back put it `30 · sin(85°)` metres BELOW the subject's
    // feet. The ground draws with back faces on, so the symptom is a featureless brown screen rather than anything that
    // reads as a camera fault.
    //
    // Shortening the distance rather than lifting the eye is what a third-person camera has to do: lifting it would swing
    // the view off the subject, and the subject is the only reason this camera exists.
    let allowed = 0;
    for (let i = 1; i <= GROUND_PROBE_STEPS; i++) {
      const t = (this.distance * i) / GROUND_PROBE_STEPS;
      const y = head + dy * t;
      if (y < this.ground.heightAt(this.anchorX + dx * t, this.anchorZ + dz * t) + GROUND_CLEARANCE_M) {
        break;
      }

      allowed = t;
    }

    this.eyeX = this.anchorX + dx * allowed;
    this.eyeZ = this.anchorZ + dz * allowed;
    this.eyeY = head + dy * allowed;
  }
}

function clamp(v: number, lo: number, hi: number): number {
  return v < lo ? lo : v > hi ? hi : v;
}
