/** How far Tab looks for a character to select, metres: anyone farther is not a candidate (Loïc, 2026-10-27). */
export const TAB_TARGET_RADIUS_M = 50;

/** A character Tab may select: its net id, its squared distance from the avatar, and whether it is an enemy (a creature). */
export interface TabCandidate {
  readonly netId: number;
  readonly d2: number;
  readonly enemy: boolean;
}

/**
 * The character Tab selects, in one cycle: the enemies first, nearest out, then the other characters, nearest out, then the
 * nearest enemy again (Loïc, 2026-10-27). The first of that order when the current selection is not a candidate; `0` when
 * there is none. Ties keep a stable order by net id, so pressing Tab again never skips one standing at the same distance.
 */
export function nextTabTarget(candidates: readonly TabCandidate[], current: number): number {
  if (candidates.length === 0) {
    return 0;
  }

  const ordered = [...candidates].sort((a, b) => Number(b.enemy) - Number(a.enemy) || a.d2 - b.d2 || a.netId - b.netId);
  const at = ordered.findIndex((c) => c.netId === current);
  return ordered[at < 0 ? 0 : (at + 1) % ordered.length].netId;
}
