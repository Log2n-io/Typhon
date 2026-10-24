import { SpeedClass } from '../data/source';

/**
 * The speed boost a player is moving at, and the keyboard shortcut that selects it.
 *
 * <b>A multiplier, not a gait</b> (Loïc, 2026-10-24: "more like a cheat code"). How a body moves is the keys': a direction
 * walks, Shift held with it runs, and the 1 key puts it on its swoop. Shift+1..4 latches ×1..×4 on top of whichever of the
 * three is in use, and that choice stays chosen until another is pressed. It replaced a ladder of burst run and two speeders,
 * which let a pedestrian outrun a rider.
 *
 * <b>The factor is sent, the speed is not.</b> Every figure lives on the SERVER and arrives as a replicated stat; the client
 * multiplies the stat by the factor it sent, and the server applies that same factor or refuses it (it grants up to ×4). This
 * module knows factors and classes and nothing about metres per second, which is what keeps a client from having its own
 * idea of how fast it is (see `no-speed-literals.test.ts`).
 */

/**
 * The boosts, by the key code of the digit that selects them.
 *
 * <b>`code`, not `key`, and that is not a style choice.</b> `KeyboardEvent.key` for Shift+1 is `!` on a US layout, `&` on
 * a French one and something else again elsewhere, so a shortcut matched on `key` would work for whoever wrote it and for
 * nobody else. `code` names the physical key: `Digit1` is the top-row 1 on every layout, and `Numpad1` — which this map
 * deliberately does not contain — is the pad.
 */
const BOOST_KEYS: ReadonlyMap<string, number> = new Map([
  ['Digit1', 1],
  ['Digit2', 2],
  ['Digit3', 3],
  ['Digit4', 4],
]);

/** The boost until something else is latched: none. */
export const DEFAULT_BOOST = 1;

/**
 * The boost a key press selects, or `null` when it selects none.
 *
 * @param code The physical key, from `KeyboardEvent.code`.
 * @param shift Whether a shift key was down with it.
 * @returns A factor of 1 to 4, or `null` to leave the boost alone.
 *
 * @remarks
 * Shift is REQUIRED: the bare 1 is the swoop, and 2..4 are free for whatever a later hotbar wants.
 */
export function boostFor(code: string, shift: boolean): number | null {
  if (!shift) {
    return null;
  }

  return BOOST_KEYS.get(code) ?? null;
}

/**
 * What to call a way of moving on screen.
 *
 * The speed is not in the name: the HUD has the metres per second from the server beside it, and a label that carried a
 * figure would be a second copy of it — stale the moment the server grants something else.
 */
export function gaitLabel(speedClass: number): string {
  switch (speedClass) {
    case SpeedClass.walk:
      return 'Walk';
    case SpeedClass.run:
      return 'Run';
    case SpeedClass.swoop:
      return 'Swoop';
    default:
      return 'Stopped';
  }
}
