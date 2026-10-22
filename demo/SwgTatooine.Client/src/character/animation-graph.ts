import { clipsForRole, type ClipDesc, type RoleMap } from '../assets/descriptor';

/**
 * Which clip a character plays, decided from its motion and from one-shot events — and nothing else.
 *
 * <b>Pure, and driven by REPLICATED motion.</b> The graph takes a ground speed, whether the body is on the ground, and a
 * clock in seconds; it returns a clip name, whether it loops, a playback rate and a serial that changes whenever a
 * one-shot (re)starts. Babylon enters only in `render/character-rig.ts`, which crossfades to whatever this says. In this
 * client the speed is measured from the positions the server replicated, not produced by a local motor — the character
 * is animated to match what the simulation already decided, which is the same split the testbench used with its motor on
 * the other side.
 *
 * Clips are found through the descriptor's ROLES (idle, walk, run, jump, attack, hit, roll, death), never by name in
 * code, so another rig with other clip names needs only another role map. `Casual_Bald` has no `Run` and runs with
 * `Run_Carry`; nothing here knows that.
 *
 * <b>No speed is written here.</b> The thresholds come from the server's own `walkMps` and `runMps`, through
 * {@link locomotionFrom} — the arrangement `no-speed-literals.test.ts` exists to protect. A client that decided for
 * itself what "running" means would disagree with the server the day a stat is retuned, and would do it silently.
 *
 * Ported from the asset testbench (`BlenderGenTest/tools/testbench/src/character/animation-graph.ts`); its local gait
 * table is replaced by the replicated figures. The event half (jump, attack, hit, roll, death) is the port's and is
 * covered by tests; only locomotion is wired to the simulation so far, because the client has no replicated animation
 * events yet.
 *
 * Rules:
 * - death wins over everything and holds its last frame until `respawn`;
 * - attack and hit lock movement for their duration;
 * - roll does not lock movement;
 * - jump lasts while airborne (its last frame holds on a long fall) and ends on landing;
 * - otherwise locomotion: idle / walk / run by speed, the rate scaled so the feet match the ground speed.
 */

export type AnimEvent = 'jump' | 'attack' | 'hit' | 'roll' | 'die' | 'respawn';

export interface LocomotionTuning {
  /** Below this ground speed (m/s) the character idles. */
  readonly idleBelow: number;
  /** At or above this ground speed it runs; between, it walks. */
  readonly runFrom: number;
  /** Ground speed at which the walk clip plays at rate 1 — feet not sliding, m/s. */
  readonly walkClipSpeed: number;
  readonly runClipSpeed: number;
  readonly minRate: number;
  readonly maxRate: number;
}

/**
 * The height the clip speeds below were measured against: a stride scales with the body, so a shorter character has to
 * step faster to cover the same ground.
 */
export const LOCOMOTION_REFERENCE_HEIGHT = 1.8;

/** What the testbench measured by eye on the Quaternius rig at {@link LOCOMOTION_REFERENCE_HEIGHT}. */
const REFERENCE_WALK_CLIP_SPEED = 2.0;
const REFERENCE_RUN_CLIP_SPEED = 5.0;

/**
 * Locomotion tuning from the server's replicated gait speeds and the body's height.
 *
 * @param walkMps The server's walk speed. Non-finite (before the owner block arrives) falls back to the reference clip
 *   speed, which is the figure that makes the clips play at rate 1 — a neutral guess rather than a made-up gait.
 * @param runMps The server's run speed.
 * @param height The body's height in metres, from its descriptor bounds.
 * @returns The tuning {@link AnimationGraph} takes.
 *
 * @remarks
 * `runFrom` sits halfway between the two gaits, whatever they are: the question "is this a walk or a run" has no answer
 * of its own, and the midpoint is the only one that moves correctly when either speed is retuned. The clip speeds scale
 * with height; the thresholds do not, because they are the server's speeds and not the body's.
 */
export function locomotionFrom(walkMps: number, runMps: number, height: number): LocomotionTuning {
  const walk = Number.isFinite(walkMps) && walkMps > 0 ? walkMps : REFERENCE_WALK_CLIP_SPEED;
  const run = Number.isFinite(runMps) && runMps > walk ? runMps : walk * 2;
  const scale = height > 0 ? height / LOCOMOTION_REFERENCE_HEIGHT : 1;
  return {
    // A tenth of a walk: slow enough that interpolation jitter around a standing body does not start the walk clip,
    // fast enough that a genuine shuffle is not idle.
    idleBelow: walk * 0.1,
    runFrom: (walk + run) / 2,
    walkClipSpeed: REFERENCE_WALK_CLIP_SPEED * scale,
    runClipSpeed: REFERENCE_RUN_CLIP_SPEED * scale,
    minRate: 0.6,
    // A speeder gait moves the body far faster than any clip was authored for. The rate is capped rather than left to
    // run away: past about 3x the legs are a blur either way, and an uncapped ratio turns the fastest gait into strobing.
    maxRate: 3.0,
  };
}

export interface AnimOutput {
  readonly clip: string;
  /**
   * The ROLE that chose the clip — `idle`, `walk`, `run`, `attack`…
   *
   * <b>Both are reported because the two consumers address a clip differently, and conflating them is silent.</b> The
   * rig plays a clip by NAME, because that is what an `AnimationGroup` is called. The crowd's atlas is keyed by ROLE,
   * because an outfit may bake its own copy of a role and `casual_bald` fills `run` with `Run_Carry`. Handing the name
   * to something expecting the role simply finds nothing: no throw, no warning — in the crowd it left every clip slot
   * zeroed, which made the shader's frame count zero, which made its modulo a division by zero, which put NaN into
   * every bone index and drew nothing at all.
   */
  readonly role: string;
  readonly loop: boolean;
  readonly rate: number;
  /** Changes whenever a one-shot starts, including a repeat of the same clip. */
  readonly serial: number;
}

type ActionKind = 'jump' | 'attack' | 'hit' | 'roll' | 'death';

interface Action {
  readonly kind: ActionKind;
  readonly clip: string;
  readonly start: number;
  readonly end: number;
}

export class AnimationGraph {
  private action: Action | null = null;
  private serial = 0;
  private attackIndex = 0;
  private readonly durations = new Map<string, number>();
  private readonly roles: RoleMap;
  private tuningValue: LocomotionTuning;

  constructor(roles: RoleMap, clips: readonly ClipDesc[], tuning: LocomotionTuning) {
    this.roles = roles;
    this.tuningValue = tuning;
    for (const clip of clips) {
      this.durations.set(clip.name, clip.duration);
    }

    // <b>The locomotion roles are checked here, because `update` runs inside the render loop.</b> `clipOf` throws on a
    // role the rig does not carry, and locomotion asks for one every frame — so a character published without a walk
    // would take the frame loop down on its first step rather than fail where it was mounted. The event roles are not
    // checked: they are asked for only when an event fires, and `accepts` is the caller's gate.
    for (const role of ['idle', 'walk', 'run']) {
      if (roles[role] === undefined) {
        throw new Error(`character has no "${role}" clip, which locomotion needs every frame`);
      }
    }
  }

  get dead(): boolean {
    return this.action?.kind === 'death';
  }

  get tuning(): LocomotionTuning {
    return this.tuningValue;
  }

  /** Re-tunes to the server's figures once the owner block has arrived, or after a gait is retuned. */
  set tuning(value: LocomotionTuning) {
    this.tuningValue = value;
  }

  /** Whether the body must not move this tick (attacking, being hit, dead). */
  locksMovement(now: number): boolean {
    const action = this.active(now);
    return action !== null && (action.kind === 'attack' || action.kind === 'hit' || action.kind === 'death');
  }

  rolling(now: number): boolean {
    return this.active(now)?.kind === 'roll';
  }

  /** Whether a fresh event of this kind is accepted now: no attack while attacking, nothing at all while dead. */
  accepts(event: AnimEvent, now: number, grounded: boolean): boolean {
    const action = this.active(now);
    if (action?.kind === 'death') {
      return event === 'respawn';
    }

    switch (event) {
      case 'respawn':
        return false;
      case 'die':
      case 'hit':
        return true;
      case 'jump':
        return grounded && action === null;
      case 'attack':
      case 'roll':
        return grounded && (action === null || action.kind === 'jump');
    }
  }

  fire(event: AnimEvent, now: number): void {
    switch (event) {
      case 'respawn':
        this.action = null;
        return;
      case 'die':
        this.begin('death', this.clipOf('death'), now, Infinity);
        return;
      case 'attack': {
        // Alternating, so two punches in a row are two different clips rather than one restarted.
        const clips = this.clipsOf('attack');
        const clip = clips[this.attackIndex % clips.length];
        this.attackIndex++;
        this.begin('attack', clip, now);
        return;
      }

      default:
        this.begin(event, this.clipOf(event), now);
    }
  }

  update(speed: number, grounded: boolean, now: number): AnimOutput {
    const action = this.active(now);
    if (action?.kind === 'jump' && grounded && now - action.start > 0.25) {
      this.action = null;
    } else if (action !== null) {
      return { clip: action.clip, role: action.kind, loop: false, rate: 1, serial: this.serial };
    }

    const tuning = this.tuningValue;
    if (speed < tuning.idleBelow) {
      return { clip: this.clipOf('idle'), role: 'idle', loop: true, rate: 1, serial: this.serial };
    }

    const run = speed >= tuning.runFrom;
    const clipSpeed = run ? tuning.runClipSpeed : tuning.walkClipSpeed;
    const rate = Math.min(tuning.maxRate, Math.max(tuning.minRate, speed / clipSpeed));
    const role = run ? 'run' : 'walk';
    return { clip: this.clipOf(role), role, loop: true, rate, serial: this.serial };
  }

  /** The running one-shot, expired ones cleared. A jump never expires on its own: it ends on landing. */
  private active(now: number): Action | null {
    if (this.action !== null && this.action.kind !== 'jump' && now >= this.action.end) {
      this.action = null;
    }

    return this.action;
  }

  private begin(kind: ActionKind, clip: string, now: number, duration = this.durations.get(clip) ?? 0.5): void {
    this.action = { kind, clip, start: now, end: now + duration };
    this.serial++;
  }

  private clipsOf(role: string): readonly string[] {
    return clipsForRole(this.roles, role);
  }

  private clipOf(role: string): string {
    return this.clipsOf(role)[0];
  }
}
