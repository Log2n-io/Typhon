import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  RealmFrame,
  type ArchetypePlan,
  type CommandSink,
  type FieldPlan,
  type GridPlan,
  type MessagePlan,
  type MetricPlan,
  type TickSink,
} from '../src/index.js';

/**
 * Access to the golden vectors the C# reference encoder produced (`test/Typhon.Protocol.Tests/Golden`, 05-sdks § 4),
 * and the recording sink whose log must equal the C# `RecordingSink`'s call for call, key for key.
 */

/**
 * Walks up from this file until the repository's golden directory appears, so the tests run from any working directory.
 */
function findGoldenDirectory(): string {
  let dir = dirname(fileURLToPath(import.meta.url));
  for (;;) {
    const candidate = join(dir, 'test', 'Typhon.Protocol.Tests', 'Golden');
    if (existsSync(candidate)) {
      return candidate;
    }

    const parent = dirname(dir);
    if (parent === dir) {
      throw new Error('test/Typhon.Protocol.Tests/Golden not found above the SDK');
    }

    dir = parent;
  }
}

export const GOLDEN_DIR = findGoldenDirectory();

export function goldenBin(name: string): Uint8Array {
  const buffer = readFileSync(join(GOLDEN_DIR, `${name}.bin`));
  return new Uint8Array(buffer.buffer, buffer.byteOffset, buffer.byteLength);
}

/** A vector's expectation; the caller states the shape it reads. */
export function goldenJson(name: string): unknown {
  return JSON.parse(readFileSync(join(GOLDEN_DIR, `${name}.json`), 'utf8'));
}

export function goldenNames(prefix: string): string[] {
  return readdirSync(GOLDEN_DIR)
    .filter((f) => f.startsWith(prefix) && f.endsWith('.json'))
    .map((f) => f.slice(0, -'.json'.length))
    .sort();
}

const bitsView = new DataView(new ArrayBuffer(8));

/**
 * A double's IEEE bits as 16 lower-case hex digits, most significant first — the golden vectors' number format — or
 * `nan`: JavaScript cannot observe a NaN's sign or payload, so the vectors compare NaN as a token.
 */
export function bits(value: number): string {
  if (value !== value) {
    return 'nan';
  }

  bitsView.setFloat64(0, value);
  return bitsView.getUint32(0).toString(16).padStart(8, '0') + bitsView.getUint32(4).toString(16).padStart(8, '0');
}

export function fromBits(hex: string): number {
  if (hex === 'nan') {
    return NaN;
  }

  bitsView.setUint32(0, parseInt(hex.slice(0, 8), 16));
  bitsView.setUint32(4, parseInt(hex.slice(8, 16), 16));
  return bitsView.getFloat64(0);
}

export function bitsOf(values: ArrayLike<number>, count: number): string[] {
  const result: string[] = [];
  for (let i = 0; i < count; i++) {
    result.push(bits(values[i]!));
  }

  return result;
}

/** A realm frame as a golden vector carries it (C# `CatalogSamples.FrameJson`): every number as its IEEE bits. */
export interface FrameJson {
  readonly realmId: number;
  readonly generation: number;
  readonly kindIdx: number;
  readonly appTag: number;
  readonly posBits: number;
  readonly cellM: string;
  readonly deep: boolean;
  readonly min: readonly string[];
  readonly max: readonly string[];
}

export function frameJson(frame: RealmFrame | null): FrameJson | null {
  return frame === null
    ? null
    : {
        realmId: frame.realmId,
        generation: frame.generation,
        kindIdx: frame.kindIdx,
        appTag: frame.appTag,
        posBits: frame.positionBits,
        cellM: bits(frame.cellM),
        deep: frame.deep,
        min: bitsOf(frame.min, 3),
        max: bitsOf(frame.max, 3),
      };
}

export function frameFromJson(json: FrameJson | null | undefined): RealmFrame | null {
  return json == null
    ? null
    : new RealmFrame(
        json.realmId,
        json.generation,
        json.kindIdx,
        json.appTag,
        json.posBits,
        fromBits(json.cellM),
        json.deep,
        json.min.map(fromBits),
        json.max.map(fromBits),
      );
}

export function hex(bytes: Uint8Array): string {
  let s = '';
  for (const b of bytes) {
    s += b.toString(16).padStart(2, '0');
  }

  return s;
}

export function fromHex(text: string): Uint8Array {
  const bytes = new Uint8Array(text.length / 2);
  for (let i = 0; i < bytes.length; i++) {
    bytes[i] = parseInt(text.slice(2 * i, 2 * i + 2), 16);
  }

  return bytes;
}

export type LogEntry = Record<string, unknown>;

const encoder = new TextEncoder();

/** Records every call a decoder makes, in the exact JSON shape of the C# `RecordingSink`. */
export class RecordingSink implements TickSink, CommandSink {
  readonly log: LogEntry[] = [];
  private archetype: ArchetypePlan | null = null;

  beginTick(tick: number, flags: number, periodUs: number): void {
    this.log.push({ call: 'beginTick', tick, flags, periodUs });
  }

  realm(frame: RealmFrame | null): void {
    this.log.push({ call: 'realm', frame: frameJson(frame) });
  }

  beginEntities(archetype: ArchetypePlan): void {
    this.archetype = archetype;
    this.log.push({ call: 'beginEntities', archetype: archetype.name });
  }

  enter(netId: number, position: Float64Array, velocity: Float64Array, t0: Uint32Array, epoch: number): void {
    this.log.push({ call: 'enter', netId, ...this.motion(position, velocity), t0: t0[0], epoch });
  }

  segment(netId: number, position: Float64Array, velocity: Float64Array, t0: Uint32Array, epoch: number): void {
    this.log.push({ call: 'segment', netId, ...this.motion(position, velocity), t0: t0[0], epoch });
  }

  state(netId: number, groupMask: number): void {
    this.log.push({ call: 'state', netId, groupMask });
  }

  leave(netId: number): void {
    this.log.push({ call: 'leave', netId });
  }

  event(type: MessagePlan): void {
    this.log.push({ call: 'event', type: type.name, idx: type.idx });
  }

  self(archetype: ArchetypePlan | null, netId: number, lastSeq: number, ownerMask: number): void {
    this.log.push({ call: 'self', archetype: archetype?.name ?? null, netId, lastSeq, ownerMask });
  }

  ack(seq: number, reason: number): void {
    this.log.push({ call: 'ack', seq, reason });
  }

  source(requestId: number, status: number, code: number): void {
    this.log.push({ call: 'source', requestId, status, code });
  }

  beginAggregate(grid: GridPlan, reset: boolean): void {
    this.log.push({ call: 'beginAggregate', grid: grid.idx, reset });
  }

  aggregateCell(cell: number, counts: Uint32Array): void {
    this.log.push({ call: 'aggregateCell', cell, counts: Array.from(counts) });
  }

  metric(metric: MetricPlan, valueIndex: number, value: number): void {
    this.log.push({ call: 'metric', name: metric.name, index: valueIndex, value: bits(value) });
  }

  debug(subType: number, data: Uint8Array, offset: number, length: number): void {
    this.log.push({ call: 'debug', subType, payload: hex(data.subarray(offset, offset + length)) });
  }

  ext(appTypeId: number, data: Uint8Array, offset: number, length: number): void {
    this.log.push({ call: 'ext', appTypeId, payload: hex(data.subarray(offset, offset + length)) });
  }

  unknownBlock(blockType: number): void {
    this.log.push({ call: 'unknownBlock', blockType });
  }

  endTick(): void {
    this.log.push({ call: 'endTick' });
  }

  command(type: MessagePlan, seq: number, clientTick: number): void {
    this.log.push({ call: 'command', type: type.name, idx: type.idx, seq, clientTick });
  }

  number(field: FieldPlan, values: Float64Array): void {
    this.log.push({ call: 'number', field: field.name, values: bitsOf(values, field.components) });
  }

  text(field: FieldPlan, value: string): void {
    this.log.push({ call: 'text', field: field.name, utf8: hex(encoder.encode(value)) });
  }

  bytes(field: FieldPlan, data: Uint8Array, offset: number, length: number): void {
    this.log.push({ call: 'bytes', field: field.name, bytes: hex(data.subarray(offset, offset + length)) });
  }

  list(field: FieldPlan, count: number, values: Float64Array): void {
    this.log.push({ call: 'list', field: field.name, count, values: bitsOf(values, count * field.components) });
  }

  /** The views as handed out, which the reader sizes exactly: `dims` values, and a velocity only when one travels. */
  private motion(position: Float64Array, velocity: Float64Array): { position: string[]; velocity: string[] } {
    const p = this.archetype?.position ?? null;
    const dims = p === null ? 0 : p.dims;
    if (position.length !== dims || velocity.length !== (p?.vel ? dims : 0)) {
      throw new Error(`motion views of ${position.length} and ${velocity.length} values for ${dims} dimensions`);
    }

    return { position: bitsOf(position, position.length), velocity: bitsOf(velocity, velocity.length) };
  }
}
