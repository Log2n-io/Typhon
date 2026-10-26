/**
 * The action bar: one slot per top-row digit, fixed rather than configurable (Loïc, 2026-10-24).
 *
 * <b>1 mounts, 2–4 are combat stances.</b> The swoop is a toggle the server answers (`mounted`). An attack slot is a
 * pushed/unpushed toggle between idle and that weapon's stance (`SetStance`, replicated as the player's `stance`): in it
 * the body holds the weapon — everyone watching sees it — and the server fights its target when it is an enemy within
 * the stance's reach, one blow on the stance's own beat. Each blow arrives as an `Attack` event and plays the weapon's
 * clip, on this client's avatar and on every other client's view of it. Mounting, or crossing into another realm, puts it
 * back to idle (`ui-store.ts`, `client-app.ts`, and the server's own copy of both rules).
 *
 * <b>`code`, not `key`</b>, for the reason `gait.ts` gives: `Digit2` is the top-row 2 on every layout. Shift+digit is the
 * boost, so a slot is the BARE digit only.
 */

/** What pressing a slot does. */
export type ActionKind = 'mount' | 'attack';

export interface ActionSlot {
  /** The digit printed on the slot, 1-based. */
  readonly key: number;
  /** The physical key, from `KeyboardEvent.code`. */
  readonly code: string;
  readonly kind: ActionKind;
  readonly label: string;
  /**
   * The published asset the slot stands for: its icon on the bar (catalogue `icons`), and for an attack the weapon held
   * (§ 5.10), whose descriptor names the clip it attacks with.
   */
  readonly asset: string;
  /** The slot's border and shading colour. */
  readonly color: string;
}

export const ACTION_SLOTS: readonly ActionSlot[] = [
  { key: 1, code: 'Digit1', kind: 'mount', label: 'Swoop', asset: 'swoop', color: '#3fb6c9' },
  { key: 2, code: 'Digit2', kind: 'attack', label: 'Unarmed combat', asset: 'vibroknuckler', color: '#d9963a' },
  { key: 3, code: 'Digit3', kind: 'attack', label: 'One-handed combat', asset: 'sword_vibroblade', color: '#9a83e0' },
  { key: 4, code: 'Digit4', kind: 'attack', label: 'Pistol combat', asset: 'blaster_dl44', color: '#d0533f' },
];

/** No stance: the avatar is idle, holding nothing. */
export const NO_STANCE = 0;

/** The icon size the slots ask for, drawn at its own size: a slot is this plus its 2 px border (`.slot` in styles.css). */
export const ACTION_ICON_SIZE = 48;

/**
 * The slot a key press uses, or `null` when it uses none.
 *
 * @param code The physical key, from `KeyboardEvent.code`.
 * @param modified Whether Shift, Ctrl, Alt or Meta was down with it — Shift+digit is the boost, the others the browser's.
 */
export function slotForKey(code: string, modified: boolean): ActionSlot | null {
  if (modified) {
    return null;
  }

  return ACTION_SLOTS.find((s) => s.code === code) ?? null;
}

/** The stance a slot press leaves: an attack slot toggles its own stance, the swoop drops any. */
export function stanceAfter(stance: number, slot: ActionSlot): number {
  if (slot.kind === 'mount') {
    return NO_STANCE;
  }

  return stance === slot.key ? NO_STANCE : slot.key;
}

/** The weapon a stance holds, or `null` for none. */
export function weaponOf(stance: number): string | null {
  const slot = ACTION_SLOTS.find((s) => s.key === stance);
  return slot?.kind === 'attack' ? slot.asset : null;
}

/**
 * The server's number for a stance (`Stances`: 0 none, 1 unarmed, 2 one-handed, 3 pistol): the attack slots in order.
 *
 * @param stance A slot's key, or {@link NO_STANCE}.
 */
export function serverStance(stance: number): number {
  const attacks = ACTION_SLOTS.filter((s) => s.kind === 'attack');
  const index = attacks.findIndex((s) => s.key === stance);
  return index < 0 ? 0 : index + 1;
}

/** The weapon a replicated `stance` holds, indexed by the server's number: what another player's body is drawn holding. */
export const STANCE_WEAPONS: readonly (string | null)[] = [
  null,
  ...ACTION_SLOTS.filter((s) => s.kind === 'attack').map((s) => s.asset),
];
