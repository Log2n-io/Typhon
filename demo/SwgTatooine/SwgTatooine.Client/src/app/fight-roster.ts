/**
 * Who this session's avatar is in a fight with, from the blows it sees (Loïc, 2026-10-26: a health bar over the people we
 * are fighting).
 *
 * <b>From the attack events, not from a replicated target.</b> A creature replicates its `target`, so one hunting the
 * avatar is known without a blow; a player does not — its target is its session's, and nobody else's business on the
 * wire. Every blow is already an event the client plays (`Attack`: attacker, target), so a body that struck the avatar,
 * or that the avatar struck, is in the fight; one neither has touched for {@link FIGHT_MEMORY_MS} has left it.
 */

/** How long a blow keeps the other party in the fight, milliseconds: longer than the slowest weapon's beat, shorter than a lull. */
export const FIGHT_MEMORY_MS = 10_000;

export class FightRoster {
  /** The other party of each blow the avatar gave or took, and when the last one landed. */
  private readonly lastBlow = new Map<number, number>();

  /**
   * A blow seen on the wire. Kept only when the avatar gave or took it.
   *
   * @param self The avatar's netId; 0 when the session has none, and then nothing is kept.
   */
  note(attacker: number, target: number, self: number, nowMs: number): void {
    if (self === 0 || attacker === target) {
      return;
    }

    if (attacker === self && target !== 0) {
      this.lastBlow.set(target, nowMs);
    } else if (target === self && attacker !== 0) {
      this.lastBlow.set(attacker, nowMs);
    }
  }

  /** Calls `visit` with every body still in the fight, and forgets those that left it. */
  forEach(nowMs: number, visit: (netId: number) => void): void {
    for (const [netId, at] of this.lastBlow) {
      if (nowMs - at > FIGHT_MEMORY_MS) {
        this.lastBlow.delete(netId);
      } else {
        visit(netId);
      }
    }
  }

  /** Forgets everyone: a new session, whose netIds name other entities. */
  clear(): void {
    this.lastBlow.clear();
  }
}
