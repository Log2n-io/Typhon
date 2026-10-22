import { SpeedClass } from '../data/source';

/**
 * What the held keys and the camera's facing mean as a movement intent (A9).
 *
 * <b>Pure, and separated from the input plumbing on purpose.</b> Everything interesting about A9 is the arithmetic — which
 * keys combine into which heading, in whose frame, and what "no keys" means — and none of it needs a canvas, a camera or a
 * socket. The wiring in `ClientApp` is then three lines that cannot be wrong in an interesting way.
 *
 * <b>No client-side prediction, deliberately</b> (A9's own wording). The client sends intents and the server integrates
 * through `Steer`; the avatar's position on screen is whatever replication last said. The render delay already covers the
 * round trip, so predicting would buy smoothness the viewer cannot distinguish and owe a reconciliation nobody has written.
 */

/** A movement the client wants. `heading` is meaningless when `speedClass` is {@link SpeedClass.stop}. */
export interface MoveIntent {
  /** Radians, measured from +X towards +Z — the frame the server integrates in. */
  readonly heading: number;
  readonly speedClass: number;
}

/** The key codes that mean "go", by the direction they mean in the camera's frame. */
const FORWARD = ['KeyW', 'ArrowUp'];
const BACK = ['KeyS', 'ArrowDown'];
const LEFT = ['KeyA', 'ArrowLeft'];
const RIGHT = ['KeyD', 'ArrowRight'];

/**
 * Held with a direction, travels at the SELECTED gait; a direction on its own walks.
 *
 * <b>Walking is what a body does, and speed is what you ask for</b> (2026-10-21, Loïc). The gait ladder is run, burst
 * run and two speeders — every rung is a deliberate thing, so none of them is the right thing to be doing by accident
 * while crossing a room.
 *
 * <b>Shift is both this modifier and the gait shortcuts' prefix, and the two do not collide.</b> Shift+2 is a press that
 * LATCHES which gait Shift gives and is over; Shift held with W is a state that means "that gait, now". So selecting a
 * speeder and then holding Shift+W rides it, and letting go walks. Nothing is unreachable either way.
 */
const RUN = ['ShiftLeft', 'ShiftRight'];

/** Whether any of a set of codes is held. */
function any(keys: ReadonlySet<string>, codes: readonly string[]): boolean {
  for (const code of codes) {
    if (keys.has(code)) {
      return true;
    }
  }

  return false;
}

/**
 * Turns the held keys into a movement intent in the server's frame.
 *
 * @param keys The key codes currently down.
 * @param yaw The camera's facing, radians, in the same frame as an entity's heading.
 * @param gait The speed class Shift+1..4 last selected; a run when nothing has been.
 * @returns The intent, or `null` when nothing is held and nothing needs saying.
 *
 * @remarks
 * <b>Relative to the CAMERA, not to the avatar.</b> "Forward" means into the screen, which is what a person pressing W
 * expects and what every third-person game does; an avatar-relative frame would make W mean "continue in the direction the
 * server last happened to steer me", which is unusable the moment the camera is not behind the body.
 *
 * <b>Opposite keys cancel, and that is not the same as no keys.</b> W and S together produce a zero vector, which is a
 * STOP — the person is asking to stand still — whereas nothing held at all is "I have no opinion", which must not keep
 * sending. The two are told apart below because only the first reaches the vector.
 *
 * <b>Diagonals are normalised by `atan2`, not by addition.</b> Taking the heading of the summed vector gives 45° for W+D
 * and, because the server turns a heading plus a speed CLASS into a destination, a diagonal is exactly as fast as a
 * straight line. Summing two axis speeds would have made diagonal movement 1.41× faster, which is the oldest bug in this
 * genre.
 */
export function movementIntent(
  keys: ReadonlySet<string>,
  yaw: number,
  gait: number = SpeedClass.run,
): MoveIntent | null {
  const forward = (any(keys, FORWARD) ? 1 : 0) - (any(keys, BACK) ? 1 : 0);
  const right = (any(keys, RIGHT) ? 1 : 0) - (any(keys, LEFT) ? 1 : 0);

  if (forward === 0 && right === 0) {
    // Nothing held, or opposites cancelling. Distinguished by whether any direction key is down at all: cancelling keys
    // are a request to stand still, and no keys is no request.
    const asked = any(keys, FORWARD) || any(keys, BACK) || any(keys, LEFT) || any(keys, RIGHT);
    return asked ? { heading: yaw, speedClass: SpeedClass.stop } : null;
  }

  // The camera's basis in the ground plane, and it has to be the EYE CAMERA's, not a convention invented here.
  //
  // `EyeCamera.updateEye` pulls the eye BACKWARD along `(-sin yaw, -cos yaw)`, so the direction it looks is
  // `(sin yaw, cos yaw)` — Babylon's left-handed reading of `rotation.y`, which is what the renderer is given. Right is
  // that turned a quarter turn the way the screen means it: `(cos yaw, -sin yaw)`.
  //
  // This was `(cos yaw, sin yaw)` for forward, which is the same vector rotated 90°, so every key moved the avatar a
  // quarter turn away from where the viewer was looking — W walked to screen-right at yaw 0. Nothing caught it because
  // the cases below asserted the wrong convention as confidently as the code implemented it.
  const cos = Math.cos(yaw);
  const sin = Math.sin(yaw);
  const dx = forward * sin + right * cos;
  const dz = forward * cos - right * sin;

  return {
    heading: Math.atan2(dz, dx),
    speedClass: any(keys, RUN) ? gait : SpeedClass.walk,
  };
}

/**
 * The stop to send when the keys have been let go, or `null` when none is owed.
 *
 * @param last The intent last sent, or `null` when nothing has been.
 * @returns A stop at the last heading, or `null`.
 *
 * @remarks
 * <b>Letting go of a key has to SAY something, which is the one thing {@link movementIntent} cannot do.</b> It is pure in the
 * keys, and no keys genuinely means "no opinion" — but the server does not stop an avatar for want of being told to. It aims at a
 * destination a second of travel ahead, so a client that simply stopped talking would watch its character walk on for up to a
 * second and about five metres after the key came up. That is the difference between a control that feels connected and one that
 * feels like it is on a delay, and no amount of tuning the refresh rate fixes it.
 *
 * <b>Once, not every frame.</b> The caller clears what it last sent, so a standing-still session sends nothing at all — which is
 * also what tells the next press apart from a repeat.
 *
 * It returns `null` for a `last` that was already a stop, because the avatar is not moving and saying so again changes nothing.
 */
export function stopAfter(last: MoveIntent | null): MoveIntent | null {
  if (last === null || last.speedClass === SpeedClass.stop) {
    return null;
  }

  return { heading: last.heading, speedClass: SpeedClass.stop };
}

/**
 * Whether an intent needs sending, given what was sent last.
 *
 * @param next What the keys now mean, or `null` for "no opinion".
 * @param last What was last sent, or `null` when nothing has been.
 * @returns Whether to send.
 *
 * @remarks
 * <b>`MoveDir` is rate-limited at 30/s with a burst of 60 and coalesced `LatestPerSession`, so sending one per frame is
 * legal and wasteful.</b> A held key re-sends the same heading sixty times a second; the server renews a second of travel
 * from each, so one every few frames is indistinguishable to the player and a sixth of the ingress.
 *
 * What must NOT be suppressed is a change: a new heading, a new speed class, or the stop that ends a run. So the rule is
 * "send when it differs", plus a periodic refresh the caller times — the server's destination is one second of travel
 * away, so an intent that stopped being re-sent would simply be reached and the avatar would halt.
 */
export function intentChanged(next: MoveIntent | null, last: MoveIntent | null): boolean {
  if (next === null) {
    return false;
  }

  if (last === null) {
    return true;
  }

  // A sixteenth of a radian is about 3.6°, well under what a person can aim with a mouse in one frame, and it keeps a
  // trembling yaw from re-sending every frame while the key is held.
  //
  // Compared as the SHORTEST angle between the two, because heading wraps: a camera crossing ±π moves from 3.14 to -3.14, a raw
  // difference of 2π, and a straight subtraction read that as the sharpest turn possible and re-sent an identical intent every
  // time the viewer happened to face that way.
  return next.speedClass !== last.speedClass || Math.abs(shortestAngle(next.heading - last.heading)) > HEADING_EPSILON;
}

/**
 * The speed class an intent asks for, with "no opinion" read as a stop.
 *
 * @param intent The intent, or `null` when nothing is held.
 * @returns The class.
 */
export function gaitOf(intent: MoveIntent | null): number {
  return intent === null ? SpeedClass.stop : intent.speedClass;
}

/**
 * Whether the GAIT changed — a start, a stop, or a change of speed class — as opposed to only the heading.
 *
 * @param next What the keys now mean, or `null` for "no opinion".
 * @param last What was last sent, or `null` when nothing has been.
 * @returns Whether this is a transition whose instant is worth measuring.
 *
 * @remarks
 * <b>The distinction exists because only a gait change has an INSTANT anybody can use (A14b).</b> A start, a stop and a
 * speed-class change are each caused by one key going down or up, so the client can say how far into the server's tick
 * the intent changed and the server can split that tick between the old velocity and the new one — which is what makes
 * the two sides integrate the same duration instead of differing by up to a tick of travel.
 *
 * A heading change is not of that kind: the yaw moves continuously with the mouse, and the server is told at most once
 * per tick and coalesces everything in between, so there is no single moment to name. Those send no phase and both sides
 * integrate whole steps, as they did before — the residual is a turn's worth of arc and not a gesture's worth of travel.
 *
 * Note that this is deliberately NOT {@link intentChanged}: that one decides whether to SEND, and a heading change is
 * worth sending. This decides whether the send carries a phase.
 */
export function gaitChanged(next: MoveIntent | null, last: MoveIntent | null): boolean {
  return gaitOf(next) !== gaitOf(last);
}

/** A difference of two headings, wrapped into [-π, π]. */
function shortestAngle(radians: number): number {
  const wrapped = (radians + Math.PI) % (2 * Math.PI);
  return (wrapped < 0 ? wrapped + 2 * Math.PI : wrapped) - Math.PI;
}

/** How much a heading must move to be worth re-sending. */
export const HEADING_EPSILON = 1 / 16;

/**
 * How often a held, unchanged intent is refreshed, in milliseconds.
 *
 * The server turns a heading into a destination one second of travel away, so an intent must be renewed inside that second
 * or the avatar reaches it and stops. A third of a second is three renewals per second — comfortably inside, and a tenth of
 * the rate limit.
 */
export const REFRESH_MS = 333;
