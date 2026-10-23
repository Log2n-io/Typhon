import { FLAT_GROUND, type GroundSampler } from '../terrain/ground-sampler';
import { EYE_HEIGHT_M, MIN_EYE_DISTANCE_M, WALL_MARGIN_M } from './eye-clearance';
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

// Declared with the march that measures walls from it; re-exported because the camera is where callers look for it.
export { EYE_HEIGHT_M };

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
 * Where a player's camera starts: behind and a little above the avatar (A10).
 *
 * Third person by default because a player has a body to see — first person on a character you have just created tells you
 * nothing about it. Four metres is close enough that the avatar fills a useful part of the frame and far enough that the
 * ground ahead is visible, which is what walking needs.
 */
export const DEFAULT_THIRD_PERSON_M = 4;

/**
 * How fast the view settles when SPECTATING. Looser than the map camera's 14: a followed entity changes heading abruptly
 * — an NPC turning a corner, a creature snapping to a new target — and a stiff camera makes that a jolt in the viewer's
 * neck.
 */
const SMOOTHING_PER_SECOND = 6;

/**
 * How fast the view settles when the subject is the viewer's OWN avatar.
 *
 * <b>Nearly rigid, because the lag is not smoothing here — it is input latency.</b> At 6 per second the anchor trails the
 * subject with a time constant near 170 ms, which is what a spectator wants and is exactly wrong for a player: it is added
 * on top of the round trip and the interpolation delay, so pressing a key appeared to do nothing for an appreciable moment
 * and releasing one appeared to coast. A third-person camera is bolted to its character; what should ease is the LOOK, not
 * the position, and `goalYaw`/`goalPitch` still do.
 */
const PLAYER_SMOOTHING_PER_SECOND = 40;

/**
 * How many points along the pull-back ray are tested against the ground. Eight is enough for a 30 m ray over terrain whose
 * steepest slope is 60°, and it is eight bilinear samples a frame — nothing against a 2 ms budget.
 */
const GROUND_PROBE_STEPS = 8;

/** How far the eye must clear the ground. Small: this is a spectator camera, not a character controller. */
const GROUND_CLEARANCE_M = 0.35;

/**
 * How fast a pulled-in eye goes back out, seconds (time constant). The testbench released at once and snapped back past
 * every pillar; the pull-in stays immediate — the eye must never be inside a wall — and only the release eases.
 */
const RELEASE_SECONDS = 0.3;

/** How far the eye may travel from a head along a unit direction before touching a building, capped at `max`. */
export type EyeClearance = (
  fromX: number,
  fromY: number,
  fromZ: number,
  dx: number,
  dy: number,
  dz: number,
  max: number,
) => number;

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

  /**
   * The buildings the eye may not go behind, or `null` for none: the app's march over the structures in reach
   * (`eye-clearance.ts`). Without it a wall behind the body puts the eye on the far side of the wall.
   */
  clearance: EyeClearance | null = null;

  /**
   * How high above the subject's feet the eye may go, metres — the testbench's interior cap (1.9, under the 2 m band the
   * colliders stop at). Applied by FLATTENING the pitch for the frame, before the march, so the march tests the ray used.
   */
  maxEyeHeightM = Infinity;

  /** How far back the eye may be asked to stand, metres: 7 in a room, where more is only ever a wall. */
  maxDistanceM = MAX_EYE_DISTANCE_M;

  /** The eye-to-head distance actually used last frame, after walls pulled it in — what the body's fade reads. */
  effectiveDistance = 0;

  /** The pulled-in distance being released, eased back out; see {@link RELEASE_SECONDS}. */
  private held = Infinity;

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
    this.held = Infinity;
    this.updateEye(0);
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

  /**
   * Sets the distance the camera settles at, in metres, clamped to the rig's bounds.
   *
   * @param metres 0 for first person, up to {@link MAX_EYE_DISTANCE_M}.
   *
   * @remarks
   * The GOAL rather than the current distance, so this glides like every other change to the rig rather than cutting. Its
   * one caller sets a player's starting distance once per avatar; everything after that is the viewer's wheel through
   * {@link zoomBy}, which is why this does not need to be called per frame and must not be.
   */
  /**
   * Whether the subject is the viewer's own avatar, and so whether the rig is bolted to it.
   *
   * Set by the app when a playing session takes the camera; false for a god camera riding a selection. See
   * {@link PLAYER_SMOOTHING_PER_SECOND}.
   */
  rigid = false;

  setDistance(metres: number): void {
    this.goalDistance = clamp(metres, 0, MAX_EYE_DISTANCE_M);
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
    const dt = Math.max(0, dtSeconds);

    // The POSITION follows at the subject's own rate — rigid for your avatar, eased for somebody else's — while the look
    // always eases, because a jolt in the view is a jolt in the viewer's neck whoever is being watched.
    const follow = 1 - Math.exp(-(this.rigid ? PLAYER_SMOOTHING_PER_SECOND : SMOOTHING_PER_SECOND) * dt);
    const k = 1 - Math.exp(-SMOOTHING_PER_SECOND * dt);
    this.anchorX += (this.subjectX - this.anchorX) * follow;
    this.anchorY += (this.subjectY - this.anchorY) * follow;
    this.anchorZ += (this.subjectZ - this.anchorZ) * follow;
    this.yaw += (this.goalYaw - this.yaw) * k;
    this.pitch += (this.goalPitch - this.pitch) * k;
    this.goalDistance = Math.min(this.goalDistance, this.maxDistanceM);
    this.distance += (this.goalDistance - this.distance) * k;
    this.updateEye(dt);
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

  private updateEye(dt: number): void {
    const head = this.anchorY + EYE_HEIGHT_M;
    const distance = Math.min(this.distance, this.maxDistanceM);

    // The cap first, by flattening the pitch for this frame (the testbench's order): the march then tests the ray used.
    let pitch = this.pitch;
    const ceiling = this.anchorY + this.maxEyeHeightM;
    if (distance > 0 && head + Math.sin(pitch) * distance > ceiling) {
      pitch = Math.asin(clamp((ceiling - head) / distance, -1, 1));
    }

    // Pulling back travels BACKWARD along the view ray, so an over-the-shoulder eye rises as it pitches down — which is
    // what keeps the subject in frame instead of sliding up out of it.
    const cp = Math.cos(pitch);
    const dx = -Math.sin(this.yaw) * cp;
    const dz = -Math.cos(this.yaw) * cp;
    const dy = Math.sin(pitch);

    // Walk out along that ray and stop before the ground. Without this the eye goes **inside the terrain**: 30 m back at
    // level pitch is inside any mesa, and looking up while pulled back put it `30 · sin(85°)` metres BELOW the subject's
    // feet. The ground draws with back faces on, so the symptom is a featureless brown screen rather than anything that
    // reads as a camera fault.
    //
    // Shortening the distance rather than lifting the eye is what a third-person camera has to do: lifting it would swing
    // the view off the subject, and the subject is the only reason this camera exists.
    let allowed = 0;
    for (let i = 1; i <= GROUND_PROBE_STEPS; i++) {
      const t = (distance * i) / GROUND_PROBE_STEPS;
      const y = head + dy * t;
      if (y < this.ground.heightAt(this.anchorX + dx * t, this.anchorZ + dz * t) + GROUND_CLEARANCE_M) {
        break;
      }

      allowed = t;
    }

    // And short of the first wall behind the body (the testbench's march): pulled in at once, never inside a wall, and
    // released over RELEASE_SECONDS so a pillar passing behind does not snap the eye in and out.
    if (this.clearance !== null && allowed > 0) {
      const wall = Math.max(
        MIN_EYE_DISTANCE_M,
        this.clearance(this.anchorX, head, this.anchorZ, dx, dy, dz, allowed) - WALL_MARGIN_M,
      );
      allowed = Math.min(allowed, wall);
    }

    if (allowed <= this.held || dt <= 0) {
      this.held = allowed;
    } else {
      this.held += (allowed - this.held) * (1 - Math.exp(-dt / RELEASE_SECONDS));
    }

    const used = Math.min(allowed, this.held);
    this.effectiveDistance = used;
    this.eyeX = this.anchorX + dx * used;
    this.eyeZ = this.anchorZ + dz * used;
    this.eyeY = head + dy * used;
  }
}

function clamp(v: number, lo: number, hi: number): number {
  return v < lo ? lo : v > hi ? hi : v;
}
