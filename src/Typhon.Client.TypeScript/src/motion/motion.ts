import type { ArchetypeStore } from '../store/archetype-store.js';

/**
 * Motion segment evaluation (`03-wire-protocol.md` § 6), in 2 or 3 dimensions.
 *
 * **Time is integer ticks plus a fraction.** Render time arrives as `renderTick` (an integer) and `frac` in [0, 1), and
 * a segment's `t0` is an integer tick. `dt = (renderTick − t0) + frac` subtracts the integers first, so no precision is
 * lost however long the server has been running — unlike an absolute tick held in a float32, whose resolution degrades
 * to a quarter tick after three days at 10 Hz. Velocities are metres per tick, so a changing tick period never distorts
 * motion.
 *
 * **Which segment.** The newest segment whose start tick render time has reached; before all of them, the oldest one
 * held. An epoch change (a teleport) is never crossed.
 *
 * **Per model.**
 * - `linear`: `p(τ) = p0 + v · (τ − t0)`, extrapolated past the newest segment, backwards before the oldest.
 * - `none`: segments are position samples. Between a sample and the next one of the same epoch, the position is
 *   interpolated and the velocity is the one between them; otherwise the sample holds, with zero velocity.
 * - `static`: the position entered with, zero velocity.
 */

/**
 * The largest {@link ArchetypeStore.motionStride}: a scratch buffer this long fits any archetype's evaluated motion.
 */
export const MAX_MOTION_STRIDE = 6;

/** Ring entry of the segment that applies at `renderTick`. */
export function segmentEntryAt(store: ArchetypeStore, slot: number, renderTick: number): number {
  const b8 = slot * store.motionRecordBytes;
  const u8 = store.motionU8;
  const u32 = store.motionU32;
  const last = store.segmentHistory - 1;
  const count = u8[b8 + store.motionCountOffset]!;
  const b32 = b8 / 4;
  // Newest to oldest, wrapping by a branch rather than a remainder; the oldest held applies when render time precedes
  // them all.
  let entry = u8[b8 + store.motionHeadOffset]!;
  for (let k = 1; k < count; k++) {
    if (renderTick >= u32[b32 + entry]!) {
      return entry;
    }

    entry = entry === 0 ? last : entry - 1;
  }

  return entry;
}

/**
 * Position and velocity of one slot at render time, into `out` at `offset`: `p[dims]` then `v[dims]`
 * ({@link ArchetypeStore.motionStride} values).
 */
export function evaluateSlot(
  store: ArchetypeStore,
  slot: number,
  renderTick: number,
  frac: number,
  out: Float64Array,
  offset: number,
): void {
  requirePosition(store);
  evaluate(store, slot, segmentEntryAt(store, slot, renderTick), renderTick, frac, out, offset);
}

/** Motion epoch of the segment that applies at `renderTick`: a change means the entity teleported. */
export function epochAt(store: ArchetypeStore, slot: number, renderTick: number): number {
  requirePosition(store);
  const entry = segmentEntryAt(store, slot, renderTick);
  return store.motionU8[slot * store.motionRecordBytes + store.motionEpochOffset + entry]!;
}

/**
 * Evaluates every live entity of an archetype, in live order: entry `i` describes `store.live[i]`. `out` must hold
 * `liveCount × store.motionStride` doubles.
 */
export function evaluateLive(store: ArchetypeStore, renderTick: number, frac: number, out: Float64Array): void {
  requirePosition(store);
  const live = store.live;
  const count = store.liveCount;
  const stride = store.motionStride;
  if (store.linear || !store.moving) {
    // The hot path: one segment lookup and one multiply-add per axis. A static position has v = 0.
    const dims = store.dims;
    const u8 = store.motionU8;
    const u32 = store.motionU32;
    const f = store.motionF64;
    const recordBytes = store.motionRecordBytes;
    const last = store.segmentHistory - 1;
    const headOffset = store.motionHeadOffset;
    const countOffset = store.motionCountOffset;
    const segmentsOffset = store.motionSegmentsOffset;
    for (let i = 0; i < count; i++) {
      const slot = live[i]!;
      const b8 = slot * recordBytes;
      const b32 = b8 / 4;
      const held = u8[b8 + countOffset]!;
      let entry = u8[b8 + headOffset]!;
      for (let k = 1; k < held; k++) {
        if (renderTick >= u32[b32 + entry]!) {
          break;
        }

        entry = entry === 0 ? last : entry - 1;
      }

      const dt = renderTick - u32[b32 + entry]! + frac;
      const b = (b8 + segmentsOffset) / 8 + entry * stride;
      const o = i * stride;
      for (let a = 0; a < dims; a++) {
        const v = f[b + dims + a]!;
        out[o + a] = f[b + a]! + v * dt;
        out[o + dims + a] = v;
      }
    }

    return;
  }

  for (let i = 0; i < count; i++) {
    const slot = live[i]!;
    evaluate(store, slot, segmentEntryAt(store, slot, renderTick), renderTick, frac, out, i * stride);
  }
}

/**
 * Heading in radians of a velocity in a plane, `atan2(u, v)`: 0 along +`v`, π/2 along +`u`; `fallback` when both
 * components are zero. Which evaluated axes are `u` and `v` is the application's convention, not the protocol's.
 */
export function headingOf(u: number, v: number, fallback: number): number {
  return u === 0 && v === 0 ? fallback : Math.atan2(u, v);
}

function evaluate(
  store: ArchetypeStore,
  slot: number,
  entry: number,
  renderTick: number,
  frac: number,
  out: Float64Array,
  offset: number,
): void {
  const dims = store.dims;
  const f = store.motionF64;
  const u32 = store.motionU32;
  const b8 = slot * store.motionRecordBytes;
  const b32 = b8 / 4;
  const b = store.segmentOffset(slot, entry);
  const t0 = u32[b32 + entry]!;

  if (store.linear || !store.moving) {
    const dt = renderTick - t0 + frac;
    for (let a = 0; a < dims; a++) {
      const v = f[b + dims + a]!;
      out[offset + a] = f[b + a]! + v * dt;
      out[offset + dims + a] = v;
    }

    return;
  }

  // Samples: interpolate toward the next newer sample of the same epoch, when one is held and render time has left t0.
  const u8 = store.motionU8;
  const head = u8[b8 + store.motionHeadOffset]!;
  const next = entry === store.segmentHistory - 1 ? 0 : entry + 1;
  const t1 = u32[b32 + next]!;
  const dt = renderTick - t0 + frac;
  const epochs = b8 + store.motionEpochOffset;
  if (entry !== head && t1 > t0 && dt >= 0 && u8[epochs + next] === u8[epochs + entry]) {
    const span = t1 - t0;
    const u = dt < span ? dt / span : 1;
    const n = store.segmentOffset(slot, next);
    for (let a = 0; a < dims; a++) {
      const p0 = f[b + a]!;
      const delta = f[n + a]! - p0;
      out[offset + a] = p0 + delta * u;
      out[offset + dims + a] = dt < span ? delta / span : 0;
    }

    return;
  }

  for (let a = 0; a < dims; a++) {
    out[offset + a] = f[b + a]!;
    out[offset + dims + a] = 0;
  }
}

function requirePosition(store: ArchetypeStore): void {
  if (!store.hasPosition) {
    throw new Error(`Archetype '${store.schema.name}' has no position`);
  }
}
