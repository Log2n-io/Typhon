import { allocateField, MOTION_GROUP, type ArchetypeSchema, type FieldArray } from './schema.js';

/** Segments kept per slot. The render delay (≤ 300 ms) spans at most three ticks, and a tick carries at most one new segment. */
export const SEGMENT_HISTORY = 4;

/**
 * Bytes per slot of the motion record. One record holds everything evaluation reads, contiguous:
 *
 * ```text
 *   0..15   t0 of the four ring entries (u32, integer ticks)
 *  16       head: ring index of the newest segment (u8)
 *  17       count: valid segments, 1..4 (u8)
 *  18..21   epoch of each ring entry (u8)
 *  22..23   padding
 *  24..151  the four segments, each `p0x p0z vx vz` (f64: metres and metres per tick)
 * ```
 *
 * Doubles, not floats: a 24-bit position over a 16 km world is exact in a float32, but the store serves any catalog, and a
 * finer quantization over a larger world is not.
 */
export const MOTION_RECORD_BYTES = 152;
export const MOTION_SEGMENTS_OFFSET = 24;

const INITIAL_CAPACITY = 256;
const MAX_CAPACITY = 1 << 24;

/**
 * Every entity of one archetype the client currently holds, as structure-of-arrays at stable slots.
 *
 * - **Stable slots.** An entity keeps its slot from enter to leave, so a renderer or a picker can index by slot. A slot
 *   freed during a frame is not reused before the next frame begins, so a change list never names a slot that changed
 *   owner inside the frame it describes. A consumer keeping per-slot state resets it for the slots in {@link entered}.
 * - **Dense iteration.** {@link live} lists the occupied slots in its first {@link liveCount} entries (swap-remove), so
 *   a per-frame pass never walks holes.
 * - **Motion.** A ring of the last {@link SEGMENT_HISTORY} segments per slot, in one {@link MOTION_RECORD_BYTES}-byte record
 *   read through three views of the same buffer. See `motion/motion.ts` for evaluation.
 * - **Growth.** Capacity doubles when the free list runs out. Every array is then replaced, so a consumer holding one
 *   must re-read it: {@link version} changes on every growth.
 */
export class ArchetypeStore {
  readonly schema: ArchetypeSchema;
  /** Change-mask bit of the motion group, or 0 when the archetype does not move. */
  readonly motionMask: number;
  readonly hasPosition: boolean;

  capacity = 0;
  /** Incremented whenever the arrays are reallocated. */
  version = 0;

  /** netId per slot; 0 when the slot is free. */
  netIds: Uint32Array = new Uint32Array(0);
  /** Occupied slots, dense in `[0, liveCount)`. */
  live: Uint32Array = new Uint32Array(0);
  liveCount = 0;
  /** Slot → position in {@link live}, or -1. */
  private liveIndex: Int32Array = new Int32Array(0);

  /** Motion records, three views of one buffer (see {@link MOTION_RECORD_BYTES}). */
  motionU32: Uint32Array = new Uint32Array(0);
  motionU8: Uint8Array = new Uint8Array(0);
  motionF64: Float64Array = new Float64Array(0);

  /** Slots that entered this frame, in `[0, enteredCount)`. A slot that also left this frame is no longer live. */
  entered: Uint32Array = new Uint32Array(0);
  enteredCount = 0;
  /** Slots updated this frame, in `[0, updatedCount)`; their groups in {@link updateMask}. Skip a slot whose mask is 0. */
  updated: Uint32Array = new Uint32Array(0);
  updatedCount = 0;
  /** Per slot: groups changed this frame (bit = group index). */
  updateMask: Uint32Array = new Uint32Array(0);
  /** netIds that left this frame, in `[0, leftCount)`. */
  left: Uint32Array = new Uint32Array(0);
  leftCount = 0;

  private fieldArrays: FieldArray[] = [];
  private readonly fieldIndexByName: Map<string, number>;

  private free: Uint32Array = new Uint32Array(0);
  private freeCount = 0;
  private pendingFree: Uint32Array = new Uint32Array(0);
  private pendingFreeCount = 0;

  constructor(schema: ArchetypeSchema, initialCapacity = INITIAL_CAPACITY) {
    this.schema = schema;
    const motionGroup = schema.groups.indexOf(MOTION_GROUP);
    this.motionMask = schema.position === 'motion' && motionGroup >= 0 ? 1 << motionGroup : 0;
    this.hasPosition = schema.position !== 'none';
    this.fieldIndexByName = new Map(schema.fields.map((f, i) => [f.name, i]));
    this.grow(Math.max(1, initialCapacity));
  }

  /** The typed array holding a field, by name. Re-read it after {@link version} changes. */
  field(name: string): FieldArray {
    const index = this.fieldIndexByName.get(name);
    if (index === undefined) {
      throw new Error(`Archetype '${this.schema.name}' has no field '${name}'`);
    }

    return this.fieldArrays[index]!;
  }

  /** The typed array holding a field, by its index in the schema. */
  fieldAt(index: number): FieldArray {
    const array = this.fieldArrays[index];
    if (array === undefined) {
      throw new Error(`Archetype '${this.schema.name}' has no field #${index}`);
    }

    return array;
  }

  fieldIndex(name: string): number {
    return this.fieldIndexByName.get(name) ?? -1;
  }

  isLive(slot: number): boolean {
    return slot >= 0 && slot < this.capacity && this.liveIndex[slot]! >= 0;
  }

  /** Starts a frame: releases the slots freed by the previous one and clears the change lists. */
  beginFrame(): void {
    for (let i = 0; i < this.pendingFreeCount; i++) {
      this.free[this.freeCount++] = this.pendingFree[i]!;
    }

    this.pendingFreeCount = 0;
    for (let i = 0; i < this.updatedCount; i++) {
      this.updateMask[this.updated[i]!] = 0;
    }

    this.enteredCount = 0;
    this.updatedCount = 0;
    this.leftCount = 0;
  }

  /**
   * Allocates a slot for an entity entering the view. Fields and motion are zeroed: an enter carries every public field,
   * but not owner-only ones, which must not inherit the previous occupant's values.
   */
  allocate(netId: number): number {
    if (this.freeCount === 0) {
      this.grow(this.capacity * 2);
    }

    const slot = this.free[--this.freeCount]!;
    this.netIds[slot] = netId;
    this.liveIndex[slot] = this.liveCount;
    this.live[this.liveCount++] = slot;
    for (const array of this.fieldArrays) {
      array[slot] = 0;
    }

    if (this.hasPosition) {
      this.motionU8.fill(0, slot * MOTION_RECORD_BYTES, (slot + 1) * MOTION_RECORD_BYTES);
    }

    this.entered[this.enteredCount++] = slot;
    return slot;
  }

  /** Frees a slot. It stays unusable until the next {@link beginFrame}, unless `immediate` (a reset frame). */
  release(slot: number, immediate = false): void {
    if (!this.isLive(slot)) {
      throw new Error(`Archetype '${this.schema.name}': slot ${slot} is not live`);
    }

    const index = this.liveIndex[slot]!;
    const lastSlot = this.live[--this.liveCount]!;
    this.live[index] = lastSlot;
    this.liveIndex[lastSlot] = index;
    this.liveIndex[slot] = -1;
    this.left[this.leftCount++] = this.netIds[slot]!;
    this.netIds[slot] = 0;
    this.updateMask[slot] = 0;
    if (immediate) {
      this.free[this.freeCount++] = slot;
    } else {
      this.pendingFree[this.pendingFreeCount++] = slot;
    }
  }

  /** Records that some groups of a live slot changed this frame. */
  markUpdated(slot: number, groupMask: number): void {
    if (groupMask === 0 || !this.isLive(slot)) {
      return;
    }

    if (this.updateMask[slot] === 0) {
      this.updated[this.updatedCount++] = slot;
    }

    this.updateMask[slot] = (this.updateMask[slot]! | groupMask) >>> 0;
  }

  /** Sets the only segment of a slot: the position an entity enters with. A static archetype passes `v = 0`. */
  resetMotion(slot: number, p0x: number, p0z: number, vx: number, vz: number, t0: number, epoch: number): void {
    const b8 = slot * MOTION_RECORD_BYTES;
    this.motionU8[b8 + 16] = 0;
    this.motionU8[b8 + 17] = 1;
    this.writeSegment(slot, 0, p0x, p0z, vx, vz, t0, epoch);
  }

  /**
   * Appends a motion segment. Older ones stay in the ring, so render time — which trails the newest frame by the render
   * delay — keeps finding the segment it needs even when several arrive before it reaches their start ticks.
   */
  pushSegment(slot: number, p0x: number, p0z: number, vx: number, vz: number, t0: number, epoch: number): void {
    if (!this.isLive(slot)) {
      return;
    }

    const b8 = slot * MOTION_RECORD_BYTES;
    const head = (this.motionU8[b8 + 16]! + 1) % SEGMENT_HISTORY;
    this.motionU8[b8 + 16] = head;
    if (this.motionU8[b8 + 17]! < SEGMENT_HISTORY) {
      this.motionU8[b8 + 17]!++;
    }

    this.writeSegment(slot, head, p0x, p0z, vx, vz, t0, epoch);
    this.markUpdated(slot, this.motionMask);
  }

  private writeSegment(
    slot: number,
    entry: number,
    p0x: number,
    p0z: number,
    vx: number,
    vz: number,
    t0: number,
    epoch: number,
  ): void {
    const b32 = (slot * MOTION_RECORD_BYTES) / 4;
    const b64 = (slot * MOTION_RECORD_BYTES + MOTION_SEGMENTS_OFFSET) / 8 + entry * 4;
    this.motionU32[b32 + entry] = t0;
    this.motionU8[slot * MOTION_RECORD_BYTES + 18 + entry] = epoch;
    const f = this.motionF64;
    f[b64] = p0x;
    f[b64 + 1] = p0z;
    f[b64 + 2] = vx;
    f[b64 + 3] = vz;
  }

  private grow(newCapacity: number): void {
    if (newCapacity > MAX_CAPACITY) {
      throw new Error(`Archetype '${this.schema.name}' would exceed ${MAX_CAPACITY} slots`);
    }

    const old = this.capacity;
    this.capacity = newCapacity;
    this.version++;

    this.netIds = grown(new Uint32Array(newCapacity), this.netIds);
    this.live = grown(new Uint32Array(newCapacity), this.live);
    const liveIndex = new Int32Array(newCapacity).fill(-1);
    liveIndex.set(this.liveIndex);
    this.liveIndex = liveIndex;

    const motion = new ArrayBuffer(this.hasPosition ? newCapacity * MOTION_RECORD_BYTES : 0);
    const motionU8 = new Uint8Array(motion);
    motionU8.set(this.motionU8);
    this.motionU8 = motionU8;
    this.motionU32 = new Uint32Array(motion);
    this.motionF64 = new Float64Array(motion);

    this.fieldArrays = this.schema.fields.map((field, i) => {
      const next = allocateField(field.kind, newCapacity);
      const previous = this.fieldArrays[i];
      if (previous !== undefined) {
        next.set(previous);
      }

      return next;
    });

    this.entered = grown(new Uint32Array(newCapacity), this.entered);
    this.updated = grown(new Uint32Array(newCapacity), this.updated);
    this.updateMask = grown(new Uint32Array(newCapacity), this.updateMask);
    this.left = grown(new Uint32Array(newCapacity), this.left);
    this.pendingFree = grown(new Uint32Array(newCapacity), this.pendingFree);

    // New slots go on the free stack highest first, so allocation hands out the lowest slot first.
    const free = grown(new Uint32Array(newCapacity), this.free);
    for (let slot = newCapacity - 1; slot >= old; slot--) {
      free[this.freeCount++] = slot;
    }

    this.free = free;
  }
}

function grown(target: Uint32Array, source: Uint32Array): Uint32Array {
  target.set(source);
  return target;
}
