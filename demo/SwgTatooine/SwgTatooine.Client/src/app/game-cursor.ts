/**
 * The in-game cursor: what a click on the world would do, shown before it is done.
 *
 * <b>A CSS cursor image, not a pointer drawn by the page</b> (2026-10-25, Loïc). The art is ours; the operating system
 * still composites it, so it moves with the hand at the hardware's latency. A pointer drawn in the DOM or the scene trails
 * the mouse by the frames between an input event and the next paint — one to three at 60 fps, more whenever a frame runs
 * long — which in a game reads as a sluggish, rubber-banded mouse.
 */

/** What a click would do: select (or nothing), or attack. */
export type CursorKind = 'select' | 'attack';

/**
 * The images. The crosshair is 32 × 32, the largest size every platform draws as a cursor without shrinking or refusing
 * it; the arrow is drawn on the same 32-unit grid and shown at 22 px (2026-10-25, Loïc: 32 was too big), about the size
 * of the system's own arrow.
 */
const SELECT_PX = 22;
const SELECT_SVG =
  `<svg xmlns="http://www.w3.org/2000/svg" width="${SELECT_PX}" height="${SELECT_PX}" viewBox="0 0 32 32">` +
  '<path d="M3 2 L3 24 L9 18.5 L13 27.5 L17 25.8 L13.2 17 L21 17 Z" fill="#f4efe4" stroke="#17130e" ' +
  'stroke-width="1.6" stroke-linejoin="round"/></svg>';

const ATTACK_SVG =
  '<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32" viewBox="0 0 32 32">' +
  '<g fill="none" stroke="#17130e" stroke-width="4" stroke-linecap="round" opacity="0.7">' +
  '<circle cx="16" cy="16" r="9"/><path d="M16 2 V9 M16 23 V30 M2 16 H9 M23 16 H30"/></g>' +
  '<g fill="none" stroke="#ff3b30" stroke-width="2" stroke-linecap="round">' +
  '<circle cx="16" cy="16" r="9"/><path d="M16 2 V9 M16 23 V30 M2 16 H9 M23 16 H30"/></g>' +
  '<circle cx="16" cy="16" r="1.6" fill="#ff3b30"/></svg>';

/** A CSS `cursor` value: the image, its hotspot, and the system cursor a browser that refuses the image falls back to. */
function cssCursor(svg: string, hotX: number, hotY: number, fallback: string): string {
  return `url("data:image/svg+xml,${encodeURIComponent(svg)}") ${hotX} ${hotY}, ${fallback}`;
}

/** The `cursor` value for each kind: the arrow's tip, the crosshair's centre. */
export const CURSORS: Readonly<Record<CursorKind, string>> = {
  // The tip, at (3, 2) on the 32-unit grid, in the image's own pixels.
  select: cssCursor(SELECT_SVG, Math.round((3 * SELECT_PX) / 32), Math.round((2 * SELECT_PX) / 32), 'default'),
  attack: cssCursor(ATTACK_SVG, 16, 16, 'crosshair'),
};

/** What a body under the cursor is, as far as a click on it goes. */
export interface CursorSubject {
  /** Its archetype's name, or null over nothing. */
  readonly archetype: string | null;
  /** Its replicated health, 0–1; NaN for an archetype without one. */
  readonly hp: number;
  /** Whether it is this session's own avatar. */
  readonly self: boolean;
}

/** The archetypes the server shoots at (`TatooineReplication.ClassifyTarget`): everything else is selected only. */
const SHOOTABLE = new Set(['Creature', 'CreatureLair', 'Player']);

/**
 * Whether a click on `subject` names something the server would fire at: a living creature, lair or player that is not
 * the viewer. Players included (2026-10-25, Loïc): the server's combat takes a player as a target.
 */
export function isShootable(subject: CursorSubject): boolean {
  return subject.archetype !== null && !subject.self && SHOOTABLE.has(subject.archetype) && subject.hp > 0;
}

/**
 * What a click on `subject` would do. A click selects and names the target together (A9), and the server fires only from
 * a combat stance — so the crosshair is a playing session holding a stance, over something it would shoot.
 */
export function cursorFor(subject: CursorSubject, playing: boolean, armed: boolean): CursorKind {
  return playing && armed && isShootable(subject) ? 'attack' : 'select';
}

/**
 * The selection frame's colour: red for a fight — the selection is attacking the viewer (its replicated `target` is this
 * session's avatar), or the viewer is attacking it (a combat stance, and a target the server would shoot) — green for
 * anything else.
 */
export function selectionTone(
  subject: CursorSubject,
  playing: boolean,
  armed: boolean,
  targetsViewer: boolean,
): 'passive' | 'hostile' {
  const attacksUs = targetsViewer && subject.hp > 0;
  return attacksUs || cursorFor(subject, playing, armed) === 'attack' ? 'hostile' : 'passive';
}
