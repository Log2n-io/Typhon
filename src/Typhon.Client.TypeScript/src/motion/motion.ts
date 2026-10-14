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
 * - `linear`: `p(τ) = p0 + v · (τ − t0)`, extrapolated past the newest segment and backwards before the oldest — dead
 *   reckoning, which is what the velocity is for, and what the server's emit rule bounds: it emits a segment as soon as
 *   that extrapolation would be wrong by more than the declared tolerance, so for every tick it stayed silent the
 *   velocity is within tolerance of the truth.
 *
 *   **The last tick before a newer origin is crossed to it instead.** A segment's velocity is fitted over the run that
 *   ENDED at `t0` (`MotionTracker`), so it describes travel already taken; it predicts the next tick only while the
 *   motion continues, and the tick a newer segment was emitted FOR is by definition the one where it stopped doing so —
 *   the emit was triggered by that tick's error. So extrapolation runs to `t0' − 1`, and over the final tick the
 *   position crosses from where it left off to `p0'`, landing exactly on the origin the server reported.
 *
 *   Both halves are measured. Drawing the old velocity through that final tick put a player's stop 220 mm past where it
 *   had stopped on a 10 Hz server, and 336 mm past it at the end of an eight-tick gap, each followed by a snap back —
 *   while the client held the segment that said otherwise. Blending over the WHOLE gap instead removes the bound the
 *   emit rule provides and cuts whatever the entity did in between: a path turning every three ticks lands 0.29 m from
 *   the truth against a 0.05 m tolerance. One tick of blend keeps both, and for a pair one tick apart — the per-tick
 *   refit storm a change of velocity produces — it is the whole segment, where the chord is the sim's own constant
 *   velocity step and therefore exact.
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
    const epochOffset = store.motionEpochOffset;
    for (let i = 0; i < count; i++) {
      const slot = live[i]!;
      const b8 = slot * recordBytes;
      const b32 = b8 / 4;
      const held = u8[b8 + countOffset]!;
      const head = u8[b8 + headOffset]!;
      let entry = head;
      for (let k = 1; k < held; k++) {
        if (renderTick >= u32[b32 + entry]!) {
          break;
        }

        entry = entry === 0 ? last : entry - 1;
      }

      const t0 = u32[b32 + entry]!;
      const dt = renderTick - t0 + frac;
      const b = (b8 + segmentsOffset) / 8 + entry * stride;
      const o = i * stride;

      // The final tick before a newer origin is crossed to it — the same rule `evaluate` applies, inlined here for the
      // reason the rest of this loop is. The extra reads are the neighbouring ring entry's tick and epoch, in a record this
      // iteration is already touching, and they are behind `entry !== head`: a steady mover holds one segment for tens of
      // ticks and is its own head, so it reads nothing extra and takes the extrapolation below, unchanged. An entity
      // mid-refit reads the tick, and the epoch only if render time is inside the last tick before it.
      if (entry !== head) {
        const next = entry === last ? 0 : entry + 1;
        const t1 = u32[b32 + next]!;
        const span = t1 - t0;
        if (span > 0 && dt >= span - 1 && u8[b8 + epochOffset + next] === u8[b8 + epochOffset + entry]) {
          const u = dt - (span - 1);
          const n = (b8 + segmentsOffset) / 8 + next * stride;
          for (let a = 0; a < dims; a++) {
            const from = f[b + a]! + f[b + dims + a]! * (span - 1);
            const delta = f[n + a]! - from;
            out[o + a] = from + delta * (u > 1 ? 1 : u);
            out[o + dims + a] = delta;
          }

          continue;
        }
      }

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

  const u8 = store.motionU8;
  const head = u8[b8 + store.motionHeadOffset]!;
  const dt = renderTick - t0 + frac;

  // Between two held segments of one epoch the newer origin is authoritative, and both models reach for it — `none` over the
  // whole gap, because it has no velocity to offer, and `linear` over the LAST TICK of it only. See the module remarks.
  //
  // Behind the `entry !== head` test, as in the C# twin: a steady mover is its own head and reads no neighbouring entry.
  let next = 0;
  let span = 0;
  let crossing = false;
  if (entry !== head) {
    next = entry === store.segmentHistory - 1 ? 0 : entry + 1;
    span = u32[b32 + next]! - t0;
    const epochs = b8 + store.motionEpochOffset;
    crossing = span > 0 && dt >= 0 && u8[epochs + next] === u8[epochs + entry];
  }

  if (store.linear || !store.moving) {
    if (crossing && dt >= span - 1) {
      // Extrapolated to t1 − 1, then across the final tick to the origin the server reported for t1.
      const u = dt - (span - 1);
      const n = store.segmentOffset(slot, next);
      for (let a = 0; a < dims; a++) {
        const from = f[b + a]! + f[b + dims + a]! * (span - 1);
        const delta = f[n + a]! - from;
        out[offset + a] = from + delta * (u > 1 ? 1 : u);
        out[offset + dims + a] = delta;
      }

      return;
    }

    for (let a = 0; a < dims; a++) {
      const v = f[b + dims + a]!;
      out[offset + a] = f[b + a]! + v * dt;
      out[offset + dims + a] = v;
    }

    return;
  }

  if (crossing) {
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
