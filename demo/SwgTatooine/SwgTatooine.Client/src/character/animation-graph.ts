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
    // A boosted gait moves the body far faster than any clip was authored for. The rate is capped rather than left to
    // run away: past about 3x the legs are a blur either way, and an uncapped ratio turns the fastest gait into strobing.
    maxRate: 3.0,
  };
}

/**
 * What the graph decided this frame.
 *
 * <b>Reused, never fresh.</b> `update` runs once per body per frame and a crowd is thousands of bodies, so returning
 * a literal was a quarter of a million objects a second on the render path. The caller reads it and is done with it
 * before the next call; nobody may keep it. Mutable for that reason alone — treat it as read-only.
 */
export interface AnimOutput {
  clip: string;
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
  role: string;
  loop: boolean;
  rate: number;
  /** Changes whenever a one-shot starts, including a repeat of the same clip. */
  serial: number;
}

type ActionKind = 'jump' | 'attack' | 'hit' | 'roll' | 'death';

interface Action {
  readonly kind: ActionKind;
  /** The role reported with the clip: the kind's own, or the one {@link AnimationGraph.strike} names. */
  readonly role: string;
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
  /** This graph's one output; see {@link AnimOutput}. */
  private readonly out: AnimOutput = { clip: '', role: '', loop: true, rate: 1, serial: 0 };
  /**
   * The three locomotion clips, resolved once.
   *
   * `clipsForRole` wraps a string-valued role in a fresh array every call (`descriptor.ts`), so asking it for the
   * walk clip every frame allocated an array per body per frame on top of the output object. A role map does not
   * change after construction.
   */
  private readonly idleClip: string;
  private readonly walkClip: string;
  private readonly runClip: string;

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

    // Resolved here, where the throw above has just proved all three exist.
    this.idleClip = clipsForRole(roles, 'idle')[0];
    this.walkClip = clipsForRole(roles, 'walk')[0];
    this.runClip = clipsForRole(roles, 'run')[0];
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

  /**
   * An attack with a named clip: a held weapon's (ASSET_FORMAT.md § 5.10), where {@link fire} alternates the rig's
   * attack role. Restarts the clip when it is the one already playing — two blows are two.
   *
   * @param clip The clip, by name: what the rig plays.
   * @param now The graph's clock.
   * @param role The role reported with it: what the crowd's atlas finds the clip's rows by (`clipRole`).
   */
  strike(clip: string, now: number, role = 'attack'): void {
    this.begin('attack', clip, now, this.durations.get(clip) ?? 0.5, role);
  }

  /** Gets up from a knock-out — which falls as a death does — with `clip` once, movement locked while it plays, then locomotion. */
  getUp(clip: string, now: number, role: string): void {
    this.action = null;
    this.begin('hit', clip, now, this.durations.get(clip) ?? 1, role);
  }

  /** A clip's length in seconds, or `NaN` for a clip this rig does not have. */
  durationOf(clip: string): number {
    return this.durations.get(clip) ?? Number.NaN;
  }

  update(speed: number, grounded: boolean, now: number): AnimOutput {
    const action = this.active(now);
    if (action?.kind === 'jump' && grounded && now - action.start > 0.25) {
      this.action = null;
    } else if (action !== null) {
      return this.say(action.clip, action.role, false, 1);
    }

    const tuning = this.tuningValue;
    if (speed < tuning.idleBelow) {
      return this.say(this.idleClip, 'idle', true, 1);
    }

    const run = speed >= tuning.runFrom;
    const clipSpeed = run ? tuning.runClipSpeed : tuning.walkClipSpeed;
    const rate = Math.min(tuning.maxRate, Math.max(tuning.minRate, speed / clipSpeed));
    return run ? this.say(this.runClip, 'run', true, rate) : this.say(this.walkClip, 'walk', true, rate);
  }

  /** Fills the one output object. */
  private say(clip: string, role: string, loop: boolean, rate: number): AnimOutput {
    const out = this.out;
    out.clip = clip;
    out.role = role;
    out.loop = loop;
    out.rate = rate;
    out.serial = this.serial;
    return out;
  }

  /** The running one-shot, expired ones cleared. A jump never expires on its own: it ends on landing. */
  private active(now: number): Action | null {
    if (this.action !== null && this.action.kind !== 'jump' && now >= this.action.end) {
      this.action = null;
    }

    return this.action;
  }

  private begin(
    kind: ActionKind,
    clip: string,
    now: number,
    duration = this.durations.get(clip) ?? 0.5,
    role: string = kind,
  ): void {
    this.action = { kind, role, clip, start: now, end: now + duration };
    this.serial++;
  }

  private clipsOf(role: string): readonly string[] {
    return clipsForRole(this.roles, role);
  }

  private clipOf(role: string): string {
    return this.clipsOf(role)[0];
  }
}
