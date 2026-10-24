import { SpeedClass } from '../data/source';

/**
 * The gait a player is travelling at, and the keyboard shortcut that selects it.
 *
 * <b>A latched selection of what SHIFT gives, not of how the body moves by default.</b> A direction key walks; Shift
 * held with it travels at the gait Shift+1..4 last chose, and that choice stays chosen. The alternative — a held key per
 * gait — cannot be combined with holding W, A, S or D on most keyboards and would make the fastest gait the one you
 * cannot steer in. So the person presses once to pick a gear, and then holds Shift to use it.
 *
 * <b>Four gaits, two of which are honest about being made up.</b> 1 is a run and 2 is SWG's Burst Run at its source
 * multiplier; 3 and 4 are travel gaits well past anything SWG had — the swoop's 14.7 m/s was the game's ceiling — and
 * exist to cross a 16 km planet rather than to reproduce anything. Every figure lives on the SERVER and arrives as a
 * replicated stat; this module knows the CLASSES and nothing about metres per second, which is the whole arrangement that
 * keeps a client from having its own idea of how fast it is (see `no-speed-literals.test.ts`).
 */

/**
 * The gaits, by the key code of the digit that selects them.
 *
 * <b>`code`, not `key`, and that is not a style choice.</b> `KeyboardEvent.key` for Shift+1 is `!` on a US layout, `&` on
 * a French one and something else again elsewhere, so a shortcut matched on `key` would work for whoever wrote it and for
 * nobody else. `code` names the physical key: `Digit1` is the top-row 1 on every layout, and `Numpad1` — which this map
 * deliberately does not contain — is the pad.
 */
const GAIT_KEYS: ReadonlyMap<string, number> = new Map([
  ['Digit1', SpeedClass.run],
  ['Digit2', SpeedClass.burst],
  ['Digit3', SpeedClass.speeder],
  ['Digit4', SpeedClass.speederFast],
]);

/** What Shift gives until something else is latched: a run, which is what every session did before gaits existed. */
export const DEFAULT_GAIT: number = SpeedClass.run;

/**
 * The gait a key press selects, or `null` when it selects none.
 *
 * @param code The physical key, from `KeyboardEvent.code`.
 * @param shift Whether a shift key was down with it.
 * @returns A speed class, or `null` to leave the gait alone.
 *
 * @remarks
 * Shift is REQUIRED, because 1..4 on their own are free for whatever a later hotbar wants, and because a digit typed by
 * accident should not change how fast the character runs.
 */
export function gaitFor(code: string, shift: boolean): number | null {
  if (!shift) {
    return null;
  }

  return GAIT_KEYS.get(code) ?? null;
}

/**
 * What to call a gait on screen.
 *
 * The speed is not in the name: the HUD has the metres per second from the server beside it, and a label that carried a
 * figure would be a second copy of it — stale the moment the server grants something else.
 */
export function gaitLabel(speedClass: number): string {
  switch (speedClass) {
    case SpeedClass.run:
      return 'Run';
    case SpeedClass.burst:
      return 'Burst run';
    case SpeedClass.speeder:
      return 'Speeder';
    case SpeedClass.speederFast:
      return 'Speeder (fast)';
    case SpeedClass.walk:
      return 'Walk';
    case SpeedClass.swoop:
      return 'Swoop';
    default:
      return 'Stopped';
  }
}

/**
 * The digit that selects a gait, for the HUD to show next to its name.
 *
 * @param speedClass A gait.
 * @returns The digit, or 0 for a class no shortcut selects.
 */
export function gaitDigit(speedClass: number): number {
  for (const [code, gait] of GAIT_KEYS) {
    if (gait === speedClass) {
      return Number(code.slice('Digit'.length));
    }
  }

  return 0;
}
