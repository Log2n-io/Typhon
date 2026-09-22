import { allocateField, isNumericKind, MOTION_CHANGE_BIT, type ArchetypeSchema, type FieldArray } from './schema.js';

/**
 * The largest render delay the store's motion ring is sized for, by default: the `Clock`'s own default `maxDelayMs`
 * (05 § 1: adaptive, clamped to 150–300 ms).
 */
export const DEFAULT_MAX_RENDER_DELAY_MS = 300;

/**
 * Segments kept per slot. Render time trails the newest frame by up to `maxRenderDelayMs`, and each tick brings at most
 * one segment, so the ring spans the delay in ticks, plus the segment render time is inside and the next one an
 * interpolated sample needs: `max(4, ⌈maxRenderDelayMs · 1000 / tickPeriodUs⌉ + 2)`. Taken from the catalog's tick
 * period, never from one application's rate.
 *
 * At most 255 (the ring's `u8` head and count). Past that cap — a delay longer than about 253 ticks — the ring covers
 * less than the delay: render time older than the oldest segment held evaluates that oldest segment (extrapolated
 * backwards for `linear`, held for `none`) rather than throwing.
 */
export function segmentHistoryFor(tickPeriodUs: number, maxRenderDelayMs = DEFAULT_MAX_RENDER_DELAY_MS): number {
  return Math.min(255, Math.max(4, Math.ceil((maxRenderDelayMs * 1000) / tickPeriodUs) + 2));
}

/**
 * The layout of one slot's motion record for `history` ring entries (H) in `dims` dimensions, contiguous:
 *
 * ```text
 *   0 .. 4H          t0 of each ring entry (u32, integer ticks)
 *   4H               head: ring index of the newest segment (u8)
 *   4H + 1           count: valid segments, 1..H (u8)
 *   4H + 2 .. 5H + 2 epoch of each ring entry (u8)
 *   padding to a multiple of 8
 *   segments         H × `p0[dims] v[dims]` (f64: metres and metres per tick)
 * ```
 *
 * Doubles, not floats: a 24-bit position over a 16 km world is exact in a float32, but the store serves any catalog, and
 * a finer quantization over a larger world is not.
 */
export function motionRecordBytes(dims: number, history: number): number {
  return motionSegmentsOffset(history) + history * 2 * dims * 8;
}

/** Where the segments start in a motion record of `history` entries: after t0s, head, count and epochs, 8-aligned. */
export function motionSegmentsOffset(history: number): number {
  return (5 * history + 2 + 7) & ~7;
}

// Small, then doubled on demand: an archetype the client never sees should not pay for slots (a 3D motion record at
// 1 kHz is 13.5 KB).
const INITIAL_CAPACITY = 16;
const MAX_CAPACITY = 1 << 24;
const EMPTY_BYTES = new Uint8Array(0);

/**
 * Every entity of one archetype the client currently holds, as structure-of-arrays at stable slots.
 *
 * - **Stable slots.** An entity keeps its slot from enter to leave, so a renderer or a picker can index by slot. A slot
 *   freed during a frame is not reused before the next frame begins, so a change list never names a slot that changed
 *   owner inside the frame it describes. A consumer keeping per-slot state resets it for the slots in {@link entered}.
 * - **Dense iteration.** {@link live} lists the occupied slots in its first {@link liveCount} entries (swap-remove), so
 *   a per-frame pass never walks holes.
 * - **Fields.** A numeric field is one typed array of `capacity × components` values; text and bytes fields hold one
 *   string or byte array per slot, replaced when the field changes — the only values a decode allocates.
 * - **Motion.** A ring of the last {@link segmentHistory} segments per slot, sized from the tick period, in one
 *   {@link motionRecordBytes}-byte record read through three views of the same buffer, in 2 or 3 dimensions. See
 *   `motion/motion.ts` for evaluation.
 * - **Growth.** Capacity doubles when the free list runs out. Every array is then replaced, so a consumer holding one
 *   must re-read it: {@link version} changes on every growth.
 */
export class ArchetypeStore {
  readonly schema: ArchetypeSchema;
  readonly hasPosition: boolean;
  /** Position dimensions: 0 when not spatial, else 2 or 3. */
  readonly dims: number;
  /** Segments are replicated (`motion`), rather than one position on enter (`static`). */
  readonly moving: boolean;
  /** Segments carry a velocity and are extrapolated; otherwise they are samples, interpolated between. */
  readonly linear: boolean;
  /** Segments kept per slot (see `segmentHistoryFor`). */
  readonly segmentHistory: number;
  /** Bytes per slot of the motion record; 0 when not spatial. */
  readonly motionRecordBytes: number;
  /** In a motion record: where the head, the count and the epochs are, and where the segments start (bytes). */
  readonly motionHeadOffset: number;
  readonly motionCountOffset: number;
  readonly motionEpochOffset: number;
  readonly motionSegmentsOffset: number;
  /** Values per slot {@link evaluateSlot} writes: `p[dims] v[dims]`. */
  readonly motionStride: number;
  /** Numbers per value, per field; 0 for text and bytes. */
  readonly fieldComponents: readonly number[];

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

  /** Motion records, three views of one buffer (see {@link motionRecordBytes}). */
  motionU32: Uint32Array = new Uint32Array(0);
  motionU8: Uint8Array = new Uint8Array(0);
  motionF64: Float64Array = new Float64Array(0);

  /** Slots that entered this frame, in `[0, enteredCount)`. A slot that also left this frame is no longer live. */
  entered: Uint32Array = new Uint32Array(0);
  enteredCount = 0;
  /**
   * Slots updated this frame, in `[0, updatedCount)`; their groups in {@link updateMask}. Skip a slot whose mask is 0.
   */
  updated: Uint32Array = new Uint32Array(0);
  updatedCount = 0;
  /**
   * Per slot: the groups changed this frame (bit = group index), plus {@link MOTION_CHANGE_BIT} when a segment arrived.
   */
  updateMask: Uint32Array = new Uint32Array(0);
  /** netIds that left this frame, in `[0, leftCount)`. */
  left: Uint32Array = new Uint32Array(0);
  leftCount = 0;

  private numeric: (FieldArray | null)[] = [];
  private texts: (string[] | null)[] = [];
  private bytes: (Uint8Array[] | null)[] = [];
  private readonly fieldIndexByName: Map<string, number>;

  /** The start tick of {@link resetMotion} and {@link pushSegment}, handed on as a one-slot array. */
  private readonly tick = new Uint32Array(1);
  private free: Uint32Array = new Uint32Array(0);
  private freeCount = 0;
  private pendingFree: Uint32Array = new Uint32Array(0);
  private pendingFreeCount = 0;

  constructor(schema: ArchetypeSchema, segmentHistory: number, initialCapacity = INITIAL_CAPACITY) {
    if (!(Number.isInteger(segmentHistory) && segmentHistory >= 1 && segmentHistory <= 255)) {
      throw new Error(`Archetype '${schema.name}': a ring of ${segmentHistory} segments; 1 to 255 expected`);
    }

    this.schema = schema;
    const position = schema.position;
    this.hasPosition = position !== undefined;
    this.dims = position?.dims ?? 0;
    this.moving = position?.kind === 'motion';
    this.linear = this.moving && position?.model !== 'none';
    this.segmentHistory = segmentHistory;
    this.motionHeadOffset = 4 * segmentHistory;
    this.motionCountOffset = 4 * segmentHistory + 1;
    this.motionEpochOffset = 4 * segmentHistory + 2;
    this.motionSegmentsOffset = motionSegmentsOffset(segmentHistory);
    this.motionRecordBytes = this.hasPosition ? motionRecordBytes(this.dims, segmentHistory) : 0;
    this.motionStride = 2 * this.dims;
    this.fieldComponents = schema.fields.map((f) => (isNumericKind(f.kind) ? (f.components ?? 1) : 0));
    this.fieldIndexByName = new Map(schema.fields.map((f, i) => [f.name, i]));
    this.grow(Math.max(1, initialCapacity));
  }

  /**
   * The typed array holding a numeric field, by name: `capacity × components` values. Re-read it after {@link version}
   * changes.
   */
  field(name: string): FieldArray {
    const index = this.fieldIndexByName.get(name);
    if (index === undefined) {
      throw new Error(`Archetype '${this.schema.name}' has no field '${name}'`);
    }

    return this.fieldAt(index);
  }

  /** The typed array holding a numeric field, by its index in the schema. */
  fieldAt(index: number): FieldArray {
    const array = this.numeric[index];
    if (array === undefined || array === null) {
      throw new Error(`Archetype '${this.schema.name}' has no numeric field #${index}`);
    }

    return array;
  }

  /** The strings of a text field, one per slot. */
  textAt(index: number): string[] {
    const array = this.texts[index];
    if (array === undefined || array === null) {
      throw new Error(`Archetype '${this.schema.name}' has no text field #${index}`);
    }

    return array;
  }

  /** The byte arrays of a bytes field, one per slot. */
  bytesAt(index: number): Uint8Array[] {
    const array = this.bytes[index];
    if (array === undefined || array === null) {
      throw new Error(`Archetype '${this.schema.name}' has no bytes field #${index}`);
    }

    return array;
  }

  /** Every numeric column by field index (`null` for text and bytes fields): the decoder's direct write path. */
  get columns(): readonly (FieldArray | null)[] {
    return this.numeric;
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
    this.clearChanges();
  }

  /**
   * Allocates a slot for an entity entering the view. Fields and motion are zeroed: an enter carries every public
   * field, and nothing may inherit the previous occupant's values.
   */
  allocate(netId: number): number {
    if (this.freeCount === 0) {
      this.grow(this.capacity * 2);
    }

    const slot = this.free[--this.freeCount]!;
    this.netIds[slot] = netId;
    this.liveIndex[slot] = this.liveCount;
    this.live[this.liveCount++] = slot;
    for (let f = 0; f < this.numeric.length; f++) {
      const array = this.numeric[f];
      if (array !== null && array !== undefined) {
        const components = this.fieldComponents[f]!;
        if (components === 1) {
          array[slot] = 0;
        } else {
          array.fill(0, slot * components, (slot + 1) * components);
        }
      } else {
        const text = this.texts[f];
        if (text !== null && text !== undefined) {
          text[slot] = '';
        } else {
          this.bytes[f]![slot] = EMPTY_BYTES;
        }
      }
    }

    if (this.hasPosition) {
      // Head, count and entry 0 only: evaluation never reads an entry beyond `count`, and the whole record is large at
      // a high tick rate.
      const b8 = slot * this.motionRecordBytes;
      const u8 = this.motionU8;
      u8[b8 + this.motionHeadOffset] = 0;
      u8[b8 + this.motionCountOffset] = 1;
      u8[b8 + this.motionEpochOffset] = 0;
      this.motionU32[b8 / 4] = 0;
      this.motionF64.fill(
        0,
        (b8 + this.motionSegmentsOffset) / 8,
        (b8 + this.motionSegmentsOffset) / 8 + this.motionStride,
      );
    }

    this.entered[this.enteredCount++] = slot;
    return slot;
  }

  /** Frees a slot. It stays unusable until the next {@link beginFrame}, unless `immediate`. */
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

  /**
   * Drops every entity at once (a `RESET` frame): slots are reusable immediately, and nothing is reported in the change
   * lists — a reset voids every slot a consumer knows, which the world's `resetThisFrame` says once.
   */
  clear(): void {
    while (this.liveCount > 0) {
      this.release(this.live[this.liveCount - 1]!, true);
    }

    this.clearChanges();
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

  /**
   * Sets the only segment of a slot: the position an entity enters with. `p` holds {@link dims} values; `v` as many, or
   * `null` for a static position or a `none`-model sample.
   */
  resetMotion(slot: number, p: ArrayLike<number>, v: ArrayLike<number> | null, t0: number, epoch: number): void {
    this.tick[0] = t0;
    this.resetMotionFrom(slot, p, v, this.tick, epoch);
  }

  /**
   * {@link resetMotion} with the start tick in `t0[0]`: a decoder's path, where a tick beyond the small-integer range
   * would be boxed crossing the call as a number.
   */
  resetMotionFrom(
    slot: number,
    p: ArrayLike<number>,
    v: ArrayLike<number> | null,
    t0: Uint32Array,
    epoch: number,
  ): void {
    const b8 = slot * this.motionRecordBytes;
    this.motionU8[b8 + this.motionHeadOffset] = 0;
    this.motionU8[b8 + this.motionCountOffset] = 1;
    this.writeSegment(slot, 0, p, v, t0, epoch);
  }

  /**
   * Appends a motion segment and marks {@link MOTION_CHANGE_BIT}. Older ones stay in the ring, so render time — which
   * trails the newest frame by the render delay — keeps finding the segment it needs even when several arrive before it
   * reaches their start ticks.
   */
  pushSegment(slot: number, p: ArrayLike<number>, v: ArrayLike<number> | null, t0: number, epoch: number): void {
    this.tick[0] = t0;
    this.pushSegmentFrom(slot, p, v, this.tick, epoch);
  }

  /** {@link pushSegment} with the start tick in `t0[0]` (see {@link resetMotionFrom}). */
  pushSegmentFrom(
    slot: number,
    p: ArrayLike<number>,
    v: ArrayLike<number> | null,
    t0: Uint32Array,
    epoch: number,
  ): void {
    if (!this.isLive(slot)) {
      return;
    }

    const b8 = slot * this.motionRecordBytes;
    const head = (this.motionU8[b8 + this.motionHeadOffset]! + 1) % this.segmentHistory;
    this.motionU8[b8 + this.motionHeadOffset] = head;
    if (this.motionU8[b8 + this.motionCountOffset]! < this.segmentHistory) {
      this.motionU8[b8 + this.motionCountOffset]!++;
    }

    this.writeSegment(slot, head, p, v, t0, epoch);
    this.markUpdated(slot, MOTION_CHANGE_BIT);
  }

  /** The newest segment's ring entry of a slot. */
  headEntry(slot: number): number {
    return this.motionU8[slot * this.motionRecordBytes + this.motionHeadOffset]!;
  }

  /** Offset in {@link motionF64} of a ring entry's `p0[0]`; the velocity follows at `+ dims`. */
  segmentOffset(slot: number, entry: number): number {
    return (slot * this.motionRecordBytes + this.motionSegmentsOffset) / 8 + entry * this.motionStride;
  }

  private writeSegment(
    slot: number,
    entry: number,
    p: ArrayLike<number>,
    v: ArrayLike<number> | null,
    t0: Uint32Array,
    epoch: number,
  ): void {
    const dims = this.dims;
    this.motionU32[(slot * this.motionRecordBytes) / 4 + entry] = t0[0]!;
    this.motionU8[slot * this.motionRecordBytes + this.motionEpochOffset + entry] = epoch;
    const f = this.motionF64;
    const b = this.segmentOffset(slot, entry);
    for (let i = 0; i < dims; i++) {
      f[b + i] = p[i]!;
      f[b + dims + i] = v === null ? 0 : v[i]!;
    }
  }

  private clearChanges(): void {
    for (let i = 0; i < this.updatedCount; i++) {
      this.updateMask[this.updated[i]!] = 0;
    }

    this.enteredCount = 0;
    this.updatedCount = 0;
    this.leftCount = 0;
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

    const motion = new ArrayBuffer(newCapacity * this.motionRecordBytes);
    const motionU8 = new Uint8Array(motion);
    motionU8.set(this.motionU8);
    this.motionU8 = motionU8;
    this.motionU32 = new Uint32Array(motion);
    this.motionF64 = new Float64Array(motion);

    this.numeric = this.schema.fields.map((field, i) => {
      const kind = field.kind;
      if (!isNumericKind(kind)) {
        return null;
      }

      const next = allocateField(kind, newCapacity * this.fieldComponents[i]!);
      const previous = this.numeric[i];
      if (previous !== undefined && previous !== null) {
        next.set(previous);
      }

      return next;
    });
    this.texts = this.schema.fields.map((field, i) =>
      field.kind === 'text' ? grownList(this.texts[i] ?? [], newCapacity, '') : null,
    );
    this.bytes = this.schema.fields.map((field, i) =>
      field.kind === 'bytes' ? grownList(this.bytes[i] ?? [], newCapacity, EMPTY_BYTES) : null,
    );

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

function grownList<T>(source: T[], length: number, fill: T): T[] {
  const next = source.slice();
  while (next.length < length) {
    next.push(fill);
  }

  return next;
}
