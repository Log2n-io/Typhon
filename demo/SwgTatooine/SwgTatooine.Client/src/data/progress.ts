/**
 * A character's combat level on the client (character-level.md § 8): the XP bar's fill from the owner-only `xp` byte, the
 * level-up line, and the inspector's title.
 */

import { entityTag, type EntityTag } from './content-level';

/** The highest combat level. */
export const MAX_LEVEL = 50;

/** The way to the next level, 0-100, from the body's `xp` byte (256ths); `NaN` when the byte has not arrived. */
export function xpPercent(xpByte: number): number {
  if (!Number.isFinite(xpByte)) {
    return Number.NaN;
  }

  return Math.min(100, Math.max(0, (xpByte / 255) * 100));
}

/** The XP bar's tooltip: "Combat level 4 — 37 % to 5", or just the level at the cap or before the byte arrives. */
export function levelTitle(level: number, xpByte: number): string {
  if (!Number.isFinite(level) || level <= 0) {
    return 'Combat level';
  }

  const percent = xpPercent(xpByte);
  return level >= MAX_LEVEL || !Number.isFinite(percent)
    ? `Combat level ${level}`
    : `Combat level ${level} — ${Math.floor(percent)} % to ${level + 1}`;
}

/**
 * Watches one's own level for the chat's level-up line. The first sight of a body — a login, a reconnect, a realm change,
 * each a new netId — only primes it: a level already held is not news. A rise of one or more is; a fall or a repeat is not.
 */
export class LevelWatch {
  private netId = 0;
  private level = 0;

  /** The new level when it rose since the last call for the same body, otherwise `null`. */
  update(netId: number, level: number): number | null {
    const known = Number.isFinite(level) && level > 0;
    if (netId !== this.netId) {
      this.netId = netId;
      this.level = known ? level : 0;
      return null;
    }

    if (!known) {
      return null;
    }

    if (this.level === 0) {
      this.level = level;
      return null;
    }

    const rose = level > this.level;
    this.level = level;
    return rose ? level : null;
  }
}

/**
 * The inspector's title for an entity, said as its name tag is (Loïc, 2026-10-26): "Player (CL 4)" for a body with a
 * character level, "Creature #3 (CL 12)" for levelled content, the plain title otherwise — the crowd's, at level 0.
 */
export function inspectorTitle(title: string, archetype: string, level: number, viewerCl: number | null): EntityTag {
  const levelled = Number.isFinite(level) && level > 0;
  return entityTag(levelled && archetype === 'Player' ? 'Player' : title, level, viewerCl);
}
