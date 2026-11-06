/**
 * Tags over the potential enemies near the viewer (Loïc, 2026-11-04: "we can't see if a character is an enemy or friendly unless we're being attacked by
 * it"): every living creature — camp humanoids and raiders included — within {@link FOE_TAG_RANGE_M}, the nearest {@link FOE_TAG_MAX}, says its name
 * and CL, and a mark says how it will behave. Players and city NPCs are friendly and carry none.
 *
 * <b>The mark is the creature's replicated `aggro`</b>, its aggro radius sent once on enter: above 0 it attacks whoever comes near
 * ({@link Hostility} `aggressive`), at 0 it fights only when attacked (`passive`). One already hunting the avatar — its replicated `target` is the
 * avatar — is `engaged`, and keeps its health bar over the tag.
 */

/** How far from the eye a creature is tagged, metres (Loïc, 2026-11-04): far enough to see a pack coming before it is on you. */
export const FOE_TAG_RANGE_M = 150;

/** The most tags at once, nearest first: a pack says more than anyone reads, and each tag is a DOM element. */
export const FOE_TAG_MAX = 12;

/** From here out a tag fades (Loïc, 2026-11-04): the farther, the less it intrudes. */
export const FOE_TAG_FADE_FROM_M = 75;

/**
 * A foe tag's opacity at `distanceM` from the eye: whole up to {@link FOE_TAG_FADE_FROM_M}, then easing (smoothstep) to nothing at
 * {@link FOE_TAG_RANGE_M}, so a tag neither pops in at the range's edge nor fades linearly through the middle distance. One hunting the avatar stays
 * whole wherever it is: that one is never noise.
 */
export function foeTagOpacity(distanceM: number, engaged: boolean): number {
  if (engaged || distanceM <= FOE_TAG_FADE_FROM_M) {
    return 1;
  }

  const t = Math.min(1, (distanceM - FOE_TAG_FADE_FROM_M) / (FOE_TAG_RANGE_M - FOE_TAG_FADE_FROM_M));
  return 1 - t * t * (3 - 2 * t);
}

/** How a creature will behave toward the viewer: attacks on sight, or only when attacked. */
export type Hostility = 'aggressive' | 'passive';

/** The mark before the name: crossed swords for one that attacks on sight, a hollow diamond for one that only fights back. */
export const HOSTILITY_MARK: Readonly<Record<Hostility, string>> = { aggressive: '⚔', passive: '◇' };

/** A creature's hostility, from its replicated aggro radius; an absent field (NaN) reads passive. */
export function hostilityOf(aggroM: number): Hostility {
  return aggroM > 0 ? 'aggressive' : 'passive';
}

/**
 * The nearest `max` of what is offered, in order, without allocating per frame: a bounded insertion into two parallel arrays — `max` is a dozen, so
 * an insertion is a dozen moves at most.
 */
export class NearestPicker {
  private readonly ids: number[] = [];
  private readonly d2s: number[] = [];
  private n = 0;
  private readonly max: number;

  constructor(max: number) {
    this.max = max;
  }

  /** Forgets the last frame's picks. */
  reset(): void {
    this.n = 0;
  }

  /** Offers `id` at squared distance `d2`; kept when it is among the nearest `max` so far. */
  offer(id: number, d2: number): void {
    if (this.n === this.max && d2 >= this.d2s[this.n - 1]) {
      return;
    }

    let i = this.n < this.max ? this.n++ : this.n - 1;
    while (i > 0 && this.d2s[i - 1] > d2) {
      this.ids[i] = this.ids[i - 1];
      this.d2s[i] = this.d2s[i - 1];
      i--;
    }

    this.ids[i] = id;
    this.d2s[i] = d2;
  }

  /** How many are kept. */
  get count(): number {
    return this.n;
  }

  /** The `i`th nearest kept. */
  idAt(i: number): number {
    return this.ids[i];
  }

  /** The `i`th nearest's squared distance. */
  d2At(i: number): number {
    return this.d2s[i];
  }
}
