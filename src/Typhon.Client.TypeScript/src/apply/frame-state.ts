import {
  ValueKind,
  type ArchetypePlan,
  type CatalogPlan,
  type FieldPlan,
  type MessagePlan,
  type MetricPlan,
} from '../protocol/catalog.js';

/*
 * The per-frame and per-session state a `TICK` carries besides entities: the controlled entity's owner values (`SELF`),
 * command rejections (`ACKS`), source lifecycle (`SOURCES`), metrics (`STATS`), and the reusable record an event is
 * decoded into. It is allocated from the catalog, so applying a frame allocates nothing but changed text and bytes, and
 * an event's bytes buffer when a value outgrows it.
 */

const EMPTY_BYTES = new Uint8Array(0);

/**
 * A bytes value to keep: `previous` itself when its content is equal, so a value resent unchanged allocates nothing and
 * keeps its identity; otherwise a copy of the view.
 */
export function retainBytes(
  data: Uint8Array,
  offset: number,
  length: number,
  previous: Uint8Array | null | undefined,
): Uint8Array {
  if (previous?.length === length) {
    let i = 0;
    while (i < length && previous[i] === data[offset + i]) {
      i++;
    }

    if (i === length) {
      return previous;
    }
  }

  return length === 0 ? EMPTY_BYTES : data.slice(offset, offset + length);
}

/**
 * The controlled entity's owner-only state (W17), accumulated across `SELF` blocks: a group not carried by a frame
 * keeps its last values. Owner values belong to one entity, not to a slot, so they live here rather than in the store.
 */
export class SelfState {
  /** The controlled entity's archetype, or `null` before the first `SELF`, after a `RESET`, and while the session controls none (W17′). */
  archetype: ArchetypePlan | null = null;
  netId = 0;
  /** The highest command `seq` drained into a tick at or before the latest frame's (W31). */
  lastSeq = 0;
  /** Whether the current frame carried a `SELF` block. */
  received = false;
  /** The owner groups the current frame carried (bit i ↔ `archetype.ownerGroups[i]`); 0 without a `SELF` block. */
  ownerMask = 0;
  /** Incremented on every `SELF` block, whether or not a value changed, and on a reset. */
  version = 0;

  /** Per owner field (by `FieldPlan.index`): its numbers, `components` wide; empty for text and bytes. */
  numbers: Float64Array[] = [];
  /** Per owner field: its text, or `null` until received. */
  texts: (string | null)[] = [];
  /** Per owner field: its bytes, or `null` until received. */
  bytes: (Uint8Array | null)[] = [];
  /** Per owner field: 1 once a value has been received for the current controlled entity. */
  present: Uint8Array = new Uint8Array(0);

  /**
   * A `SELF` block begins. A different controlled entity starts from no values: SUB-11 then sends every group.
   * `null` is no controlled entity (W17′): the owner state is dropped and only `lastSeq` is kept.
   */
  receive(archetype: ArchetypePlan | null, netId: number, lastSeq: number, ownerMask: number): void {
    if (archetype === null) {
      // Only when there is something to drop: a spectator is acknowledged every frame it sends commands in.
      if (this.archetype === null) {
        this.netId = 0;
        this.lastSeq = lastSeq;
        this.ownerMask = 0;
        this.received = true;
        this.version++;
        return;
      }

      this.archetype = null;
      this.numbers = [];
      this.texts = [];
      this.bytes = [];
      this.present = new Uint8Array(0);
    } else if (this.archetype !== archetype || this.netId !== netId) {
      const fields = archetype.ownerFields;
      this.archetype = archetype;
      this.numbers = fields.map((f) => new Float64Array(f.valueKind === ValueKind.Number ? f.components : 0));
      this.texts = fields.map(() => null);
      this.bytes = fields.map(() => null);
      this.present = new Uint8Array(fields.length);
    }

    this.netId = netId;
    this.lastSeq = lastSeq;
    this.ownerMask = ownerMask;
    this.received = true;
    this.version++;
  }

  beginFrame(): void {
    this.received = false;
    this.ownerMask = 0;
  }

  clear(): void {
    this.archetype = null;
    this.netId = 0;
    this.lastSeq = 0;
    this.received = false;
    this.ownerMask = 0;
    this.numbers = [];
    this.texts = [];
    this.bytes = [];
    this.present = new Uint8Array(0);
    this.version++;
  }

  setNumber(field: FieldPlan, values: Float64Array): void {
    const target = this.numbers[field.index]!;
    for (let i = 0; i < target.length; i++) {
      target[i] = values[i]!;
    }

    this.present[field.index] = 1;
  }

  setText(field: FieldPlan, value: string): void {
    this.texts[field.index] = value;
    this.present[field.index] = 1;
  }

  setBytes(field: FieldPlan, data: Uint8Array, offset: number, length: number): void {
    this.bytes[field.index] = retainBytes(data, offset, length, this.bytes[field.index]);
    this.present[field.index] = 1;
  }

  /** The first number of an owner field by name, or `NaN` when it was never received. */
  number(name: string, component = 0): number {
    const index = this.archetype?.ownerFields.findIndex((f) => f.name === name) ?? -1;
    return index >= 0 && this.present[index] === 1 ? this.numbers[index]![component]! : NaN;
  }
}

/**
 * One event, decoded into buffers preallocated for its type and handed to the event handler; the same record is reused
 * for every event of that type, so a handler that keeps values copies them.
 */
export class EventRecord {
  readonly type: MessagePlan;
  /** The tick of the frame that carried it. */
  tick = 0;
  /** Per body field (by `FieldPlan.index`): where its numbers start in {@link numbers}. */
  readonly offsets: Int32Array;
  /** Numeric fields' components and lists' flattened elements. */
  readonly numbers: Float64Array;
  /** Per body field: a list's element count, or a bytes field's length. */
  readonly counts: Int32Array;
  /** Per body field: its text; empty for other kinds. */
  readonly texts: string[];
  /**
   * Per body field: a buffer grown on demand to the largest value received so far; the value is
   * `bytes[i].subarray(0, counts[i])`, and the buffer may be replaced by a later event.
   */
  readonly bytes: Uint8Array[];

  constructor(type: MessagePlan) {
    this.type = type;
    const fields = type.body.fields;
    this.offsets = new Int32Array(fields.length);
    this.counts = new Int32Array(fields.length);
    let size = 0;
    for (const f of fields) {
      this.offsets[f.index] = size;
      size +=
        f.valueKind === ValueKind.List
          ? f.maxCount * f.components
          : f.valueKind === ValueKind.Number
            ? f.components
            : 0;
    }

    this.numbers = new Float64Array(size);
    this.texts = fields.map(() => '');
    this.bytes = fields.map(() => EMPTY_BYTES);
  }

  /**
   * A numeric field's component, by field name. Resolve the field index once with {@link fieldIndex} in hot handlers.
   */
  number(name: string, component = 0): number {
    const index = this.fieldIndex(name);
    return index < 0 ? NaN : this.numbers[this.offsets[index]! + component]!;
  }

  fieldIndex(name: string): number {
    return this.type.body.fields.findIndex((f) => f.name === name);
  }

  setNumber(field: FieldPlan, values: Float64Array): void {
    const at = this.offsets[field.index]!;
    for (let i = 0; i < field.components; i++) {
      this.numbers[at + i] = values[i]!;
    }
  }

  setList(field: FieldPlan, count: number, values: Float64Array): void {
    const at = this.offsets[field.index]!;
    const length = count * field.components;
    for (let i = 0; i < length; i++) {
      this.numbers[at + i] = values[i]!;
    }

    this.counts[field.index] = count;
  }

  setText(field: FieldPlan, value: string): void {
    this.texts[field.index] = value;
  }

  setBytes(field: FieldPlan, data: Uint8Array, offset: number, length: number): void {
    let buffer = this.bytes[field.index]!;
    if (buffer.length < length) {
      buffer = new Uint8Array(Math.max(length, buffer.length * 2));
      this.bytes[field.index] = buffer;
    }

    // A loop, not `set(data.subarray(…))`: the subarray view would be an allocation per event.
    for (let i = 0; i < length; i++) {
      buffer[i] = data[offset + i]!;
    }

    this.counts[field.index] = length;
  }
}

/**
 * The latest metric values (`STATS`, W25): every metric's values, flattened in catalog order at `MetricPlan.offset`.
 */
export class StatsState {
  readonly plan: CatalogPlan;
  readonly values: Float64Array;
  /** The tick of the latest `STATS` block, or −1. */
  tick = -1;
  /** Whether the current frame carried a `STATS` block. */
  received = false;

  constructor(plan: CatalogPlan) {
    this.plan = plan;
    this.values = new Float64Array(plan.metricValueCount);
  }

  valueOf(metric: MetricPlan, index = 0): number {
    return this.values[metric.offset + index]!;
  }

  /** A metric's value by name (and label index), or `NaN` when the catalog has no such metric. */
  value(name: string, index = 0): number {
    const metric = this.plan.metricByName(name);
    return metric === null || index >= metric.valueCount ? NaN : this.values[metric.offset + index]!;
  }
}

/** This frame's command rejections (`ACKS`): `seq[i]`, `reason[i]` for i in `[0, count)`. */
export class AckList {
  seq: Uint16Array = new Uint16Array(16);
  reason: Uint8Array = new Uint8Array(16);
  count = 0;

  push(seq: number, reason: number): void {
    if (this.count === this.seq.length) {
      this.seq = grow16(this.seq);
      this.reason = grow8(this.reason);
    }

    this.seq[this.count] = seq;
    this.reason[this.count++] = reason;
  }
}

/** This frame's source lifecycle entries (`SOURCES`): `requestId[i]`, `status[i]`, `code[i]` for i in `[0, count)`. */
export class SourceList {
  requestId: Uint16Array = new Uint16Array(4);
  status: Uint8Array = new Uint8Array(4);
  code: Uint16Array = new Uint16Array(4);
  count = 0;

  push(requestId: number, status: number, code: number): void {
    if (this.count === this.requestId.length) {
      this.requestId = grow16(this.requestId);
      this.status = grow8(this.status);
      this.code = grow16(this.code);
    }

    this.requestId[this.count] = requestId;
    this.status[this.count] = status;
    this.code[this.count++] = code;
  }
}

function grow16(array: Uint16Array): Uint16Array {
  const next = new Uint16Array(array.length * 2);
  next.set(array);
  return next;
}

function grow8(array: Uint8Array): Uint8Array {
  const next = new Uint8Array(array.length * 2);
  next.set(array);
  return next;
}
