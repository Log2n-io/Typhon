/**
 * Which crowd bodies are drawn this frame, and at which level of detail.
 *
 * A body is culled when its bounding sphere is outside the frustum; otherwise its tier comes from its projected HEIGHT
 * in pixels — <b>the same measure `chooseBand` already uses</b> (`view-math.ts`), so a threshold means the same thing
 * at any resolution or field of view. Each threshold has hysteresis, so a body standing at a boundary does not
 * flicker between two meshes.
 *
 * <b>The coarsest tier is not drawn here, and that is the seam with what this client already had.</b> The entity
 * layer enters its near band at 7 px and leaves at 5; the crowd's last threshold is the same 7 px. Below it a body is
 * handed back to the existing far sprite, which is what the spike's own comment said this tier was for and never
 * implemented — it drew its coarsest mesh there instead.
 *
 * Pure and engine-free; the frustum is six planes `(a, b, c, d)` with the inside where `a·x + b·y + c·z + d ≥ 0`,
 * Babylon's `Frustum.GetPlanesToRef` convention.
 */

import { LEAVE_NEAR_PX } from './view-math';

/** Tier of a body outside the frustum. */
export const CULLED = 255;

/**
 * Pixel heights at which a body ENTERS tiers 0, 1 and 2, finest first.
 *
 * Below the last it is {@link SPRITE_TIER}: the crowd does not draw it and the sprite band does. The 7 is deliberately
 * the entity layer's own `ENTER_NEAR_PX`, so there is one boundary in the world rather than two a pixel apart.
 */
export const TIER_ENTER_PX: readonly number[] = [150, 40, 7];

/** Leaving a tier happens this fraction below its entry threshold. */
export const HYSTERESIS = 0.15;

/** The tier below the last threshold: drawn by the sprite band, not by the crowd. */
export const SPRITE_TIER = TIER_ENTER_PX.length;

export interface ViewParams {
  readonly eyeX: number;
  readonly eyeY: number;
  readonly eyeZ: number;
  /**
   * Pixels per metre at one metre: `renderHeight / (2 tan(fovY / 2))`.
   *
   * <b>From the ACTIVE camera, both halves of it.</b> This client has already paid for getting that wrong once: the
   * FOV was taken from one camera while this figure came from another, a 1.45× disagreement that put every LOD
   * threshold 31 % low and never threw — it only looked wrong.
   */
  readonly pixelsPerMetre: number;
  readonly planes: ArrayLike<number>;
}

/**
 * The tier for a projected height, given the tier this body had last frame.
 *
 * Hysteresis applies only between tiers the body was already in or finer than; one arriving from {@link CULLED} takes
 * the plain thresholds, because it has no history to be sticky about.
 *
 * @param pixels The body's projected height.
 * @param previous Its tier last frame, or {@link CULLED}.
 * @returns The tier, up to and including {@link SPRITE_TIER}.
 */
export function tierFor(pixels: number, previous: number): number {
  let tier = SPRITE_TIER;
  for (const [t, enter] of TIER_ENTER_PX.entries()) {
    const sticky = previous !== CULLED && previous <= t;
    let threshold = sticky ? enter * (1 - HYSTERESIS) : enter;
    // <b>At the bottom, BOTH thresholds are the entity layer's, not ours.</b> Everywhere else the hysteresis is ours
    // to choose, because both sides of the boundary are meshes this layer draws. Here the other side is the packer's
    // sprite band, and the two must agree exactly: it enters `Band.Near` at 7 px and holds it — skipping the box —
    // until 5. Our own 7 × 0.85 = 5.95 left a window from 5.00 to 5.95 px where the crowd had handed the body back
    // and the packer had not taken it, so it was drawn by NOBODY.
    //
    // Only the leaving threshold is clamped. Lowering the entry one to 5 as well would have the crowd draw a mesh
    // from 5 px while the packer still called the body Far and drew its sprite — the opposite failure, drawn TWICE,
    // and the one §4.3 names as the visible of the two.
    if (sticky && t === TIER_ENTER_PX.length - 1) {
      threshold = Math.min(threshold, LEAVE_NEAR_PX);
    }

    if (pixels >= threshold) {
      tier = t;
      break;
    }
  }

  return tier;
}

/**
 * Classifies bodies into tiers, culling the ones outside the frustum.
 *
 * Feet are read from a matrix buffer's translation (stride 16, translation at 12–14), which is the buffer that is
 * uploaded anyway — there is no second copy of a position for this.
 *
 * @param matrices The per-body matrices.
 * @param count How many bodies.
 * @param heights Each body's height in metres, or one height for all of them. Height drives the projected size AND the
 * cull sphere, so a constant here would be wrong for every outfit but one — the spike used 1.7 m for all of them while
 * the published bodies are 1.64.
 * @param view The camera.
 * @param tiers In and out: last frame's tiers, overwritten with this frame's.
 * @param totals Optional per-tier counts, for the HUD.
 * @returns How many bodies the crowd will draw — those in a tier finer than {@link SPRITE_TIER}.
 */
export function classify(
  matrices: ArrayLike<number>,
  count: number,
  heights: ArrayLike<number> | number,
  view: ViewParams,
  tiers: Uint8Array,
  totals: Int32Array | null = null,
): number {
  const p = view.planes;
  const one = typeof heights === 'number' ? heights : 0;
  let drawn = 0;
  for (let i = 0; i < count; i++) {
    const height = typeof heights === 'number' ? one : (heights[i] ?? 0);
    const radius = height * 0.5;
    const o = i * 16;
    const x = matrices[o + 12] ?? 0;
    const y = (matrices[o + 13] ?? 0) + radius;
    const z = matrices[o + 14] ?? 0;
    let inside = true;
    for (let k = 0; k < 24; k += 4) {
      if ((p[k] ?? 0) * x + (p[k + 1] ?? 0) * y + (p[k + 2] ?? 0) * z + (p[k + 3] ?? 0) < -radius) {
        inside = false;
        break;
      }
    }

    if (!inside) {
      tiers[i] = CULLED;
      continue;
    }

    const d = Math.max(0.01, Math.hypot(x - view.eyeX, y - view.eyeY, z - view.eyeZ));
    const t = tierFor((height * view.pixelsPerMetre) / d, tiers[i] ?? CULLED);
    tiers[i] = t;
    if (totals !== null) {
      totals[t] = (totals[t] ?? 0) + 1;
    }

    if (t < SPRITE_TIER) {
      drawn++;
    }
  }

  return drawn;
}

/**
 * Copies every drawn body into its slot's compact per-instance buffers, in one pass.
 *
 * A tier draws in slot `slotOf[tier]`; several tiers may share a slot when an outfit carries fewer levels than there
 * are tiers. Culled bodies, bodies in {@link SPRITE_TIER} and tiers with no slot are skipped.
 *
 * @param tiers Each body's tier.
 * @param count How many bodies.
 * @param slotOf Tier to slot, negative for "not drawn".
 * @param sources The full per-body buffers and their strides in floats.
 * @param targets `targets[slot][b]` receives source `b`.
 * @param counts Out: how many bodies landed in each slot.
 */
export function packTiers(
  tiers: Uint8Array,
  count: number,
  slotOf: ArrayLike<number>,
  sources: readonly { readonly data: ArrayLike<number>; readonly stride: number }[],
  targets: readonly (readonly Float32Array[])[],
  counts: Int32Array,
): void {
  const nSources = sources.length;
  if (targets.some((t) => t.length !== nSources)) {
    throw new Error('packTiers: every slot needs one target per source');
  }

  counts.fill(0);
  for (let i = 0; i < count; i++) {
    const t = tiers[i] ?? CULLED;
    if (t === CULLED || t >= SPRITE_TIER) {
      continue;
    }

    const slot = slotOf[t] ?? -1;
    if (slot < 0 || slot >= targets.length) {
      continue;
    }

    const bufs = targets[slot];
    const k = counts[slot] ?? 0;
    // Index loops, not iterators: this runs per body per frame, and an iterator allocates.
    for (let b = 0; b < nSources; b++) {
      const src = sources[b];
      const dst = bufs[b];
      const s = src.stride;
      const from = i * s;
      const to = k * s;
      for (let j = 0; j < s; j++) {
        dst[to + j] = src.data[from + j] ?? 0;
      }
    }

    counts[slot] = k + 1;
  }
}
