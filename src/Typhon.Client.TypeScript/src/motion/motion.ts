import {
  MOTION_RECORD_BYTES,
  MOTION_SEGMENTS_OFFSET,
  SEGMENT_HISTORY,
  type ArchetypeStore,
} from '../store/archetype-store.js';

/**
 * Motion segment evaluation: `p(τ) = p0 + v · (τ − t0)`.
 *
 * **Time is integer ticks plus a fraction.** Render time arrives as `renderTick` (an integer) and `frac` in [0, 1), and a
 * segment's `t0` is an integer tick. `dt = (renderTick − t0) + frac` subtracts the integers first, so no precision is
 * lost however long the server has been running — unlike an absolute tick held in a float32, whose resolution degrades
 * to a quarter tick after three days at 10 Hz. Velocities are metres per tick, so a changing tick period never
 * distorts motion.
 *
 * **Which segment.** The newest segment whose start tick render time has reached; before all of them, the oldest one
 * held (extrapolated backwards). Segments are never blended: an epoch change (a teleport) is simply a switch.
 */

/** Evaluated motion of one live entity, written at stride 4: `x z vx vz`. */
export const MOTION_STRIDE = 4;

const RECORD_WORDS = MOTION_RECORD_BYTES / 4;
const RECORD_DOUBLES = MOTION_RECORD_BYTES / 8;
const SEGMENTS_DOUBLE_OFFSET = MOTION_SEGMENTS_OFFSET / 8;

/** Ring entry of the segment that applies at `renderTick`. */
export function segmentEntryAt(store: ArchetypeStore, slot: number, renderTick: number): number {
  const b8 = slot * MOTION_RECORD_BYTES;
  const u8 = store.motionU8;
  const u32 = store.motionU32;
  const head = u8[b8 + 16]!;
  const count = u8[b8 + 17]!;
  const b32 = slot * RECORD_WORDS;
  let entry = head;
  for (let k = 0; k < count; k++) {
    entry = (head - k + SEGMENT_HISTORY) % SEGMENT_HISTORY;
    if (renderTick >= u32[b32 + entry]!) {
      return entry;
    }
  }

  return entry;
}

/** Position and velocity of one slot at render time, into `out` at `offset` (stride {@link MOTION_STRIDE}). */
export function evaluateSlot(
  store: ArchetypeStore,
  slot: number,
  renderTick: number,
  frac: number,
  out: Float64Array,
  offset: number,
): void {
  requirePosition(store);
  const entry = segmentEntryAt(store, slot, renderTick);
  const dt = renderTick - store.motionU32[slot * RECORD_WORDS + entry]! + frac;
  const b = slot * RECORD_DOUBLES + SEGMENTS_DOUBLE_OFFSET + entry * 4;
  const f = store.motionF64;
  const vx = f[b + 2]!;
  const vz = f[b + 3]!;
  out[offset] = f[b]! + vx * dt;
  out[offset + 1] = f[b + 1]! + vz * dt;
  out[offset + 2] = vx;
  out[offset + 3] = vz;
}

/** Motion epoch of the segment that applies at `renderTick`: a change means the entity teleported. */
export function epochAt(store: ArchetypeStore, slot: number, renderTick: number): number {
  requirePosition(store);
  return store.motionU8[slot * MOTION_RECORD_BYTES + 18 + segmentEntryAt(store, slot, renderTick)]!;
}

/**
 * Evaluates every live entity of an archetype, in live order: entry `i` describes `store.live[i]`. `out` must hold
 * `liveCount × MOTION_STRIDE` doubles.
 */
export function evaluateLive(store: ArchetypeStore, renderTick: number, frac: number, out: Float64Array): void {
  requirePosition(store);
  const live = store.live;
  const u8 = store.motionU8;
  const u32 = store.motionU32;
  const f = store.motionF64;
  const count = store.liveCount;
  for (let i = 0; i < count; i++) {
    const slot = live[i]!;
    const b8 = slot * MOTION_RECORD_BYTES;
    const b32 = slot * RECORD_WORDS;
    const head = u8[b8 + 16]!;
    const held = u8[b8 + 17]!;
    let entry = head;
    for (let k = 0; k < held; k++) {
      entry = (head - k + SEGMENT_HISTORY) % SEGMENT_HISTORY;
      if (renderTick >= u32[b32 + entry]!) {
        break;
      }
    }

    const dt = renderTick - u32[b32 + entry]! + frac;
    const b = slot * RECORD_DOUBLES + SEGMENTS_DOUBLE_OFFSET + entry * 4;
    const vx = f[b + 2]!;
    const vz = f[b + 3]!;
    const o = i * MOTION_STRIDE;
    out[o] = f[b]! + vx * dt;
    out[o + 1] = f[b + 1]! + vz * dt;
    out[o + 2] = vx;
    out[o + 3] = vz;
  }
}

/** Heading in radians around +Y, from a velocity; `fallback` when the entity is not moving. */
export function headingOf(vx: number, vz: number, fallback: number): number {
  return vx === 0 && vz === 0 ? fallback : Math.atan2(vx, vz);
}

function requirePosition(store: ArchetypeStore): void {
  if (!store.hasPosition) {
    throw new Error(`Archetype '${store.schema.name}' has no position`);
  }
}
