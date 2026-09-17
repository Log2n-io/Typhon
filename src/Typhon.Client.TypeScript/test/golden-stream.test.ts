import { describe, expect, it } from 'vitest';
import {
  CatalogPlan,
  CodecKind,
  FrameApplier,
  MOTION_CHANGE_BIT,
  NOT_FOUND,
  parseCatalog,
  ValueKind,
  type EventRecord,
} from '../src/index.js';
import { bitsOf, goldenBin, goldenJson, goldenNames, hex, type LogEntry } from './golden-support.js';

/*
 * stream-*: TICK frames framed as `u32 len LE | message` (W31), and the replica after each frame, rendered in the
 * implementation-neutral shape of the .NET SDK's `StreamSnapshot`: every collection sorted by its key, numbers as IEEE
 * bits, text and bytes as lower-case hex. A store that applies the same frames must render the same document.
 */

const encoder = new TextEncoder();

function frames(stream: Uint8Array): Uint8Array[] {
  const view = new DataView(stream.buffer, stream.byteOffset, stream.byteLength);
  const result: Uint8Array[] = [];
  for (let at = 0; at < stream.length;) {
    const length = view.getUint32(at, true);
    result.push(stream.subarray(at + 4, at + 4 + length));
    at += 4 + length;
  }

  return result;
}

/** An event as dispatched; an entityRef also records whether the store resolves it then (the apply order, observable). */
function renderEvent(event: EventRecord, applier: FrameApplier): LogEntry {
  const fields: LogEntry = {};
  let known: LogEntry | null = null;
  for (const f of event.type.body.fields) {
    const i = f.index;
    switch (f.valueKind) {
      case ValueKind.Number:
        fields[f.name] = bitsOf(event.numbers.subarray(event.offsets[i]), f.components);
        if (f.kind === CodecKind.EntityRef) {
          known ??= {};
          known[f.name] = applier.world.locate(event.numbers[event.offsets[i]!]!) !== NOT_FOUND;
        }

        break;
      case ValueKind.List: {
        const count = event.counts[i]!;
        fields[f.name] = { count, values: bitsOf(event.numbers.subarray(event.offsets[i]), count * f.components) };
        break;
      }
      case ValueKind.Text:
        fields[f.name] = hex(encoder.encode(event.texts[i]));
        break;
      case ValueKind.Bytes:
        fields[f.name] = hex(event.bytes[i]!.subarray(0, event.counts[i]));
        break;
      default:
        break;
    }
  }

  return known === null ? { type: event.type.name, fields } : { type: event.type.name, fields, known };
}

function render(applier: FrameApplier, events: LogEntry[]): LogEntry {
  const plan = applier.plan;
  const archetypes: LogEntry = {};
  for (const a of plan.archetypes) {
    const store = applier.world.archetypeStore(a.idx);
    const entities: { netId: number; json: LogEntry }[] = [];
    for (let i = 0; i < store.liveCount; i++) {
      const slot = store.live[i]!;
      const fields: LogEntry = {};
      for (const f of a.fields) {
        const index = store.fieldIndex(f.name);
        if (index < 0) {
          continue;
        }

        if (f.valueKind === ValueKind.Number) {
          fields[f.name] = bitsOf(store.fieldAt(index).subarray(slot * f.components), f.components);
        } else if (f.valueKind === ValueKind.Text) {
          fields[f.name] = hex(encoder.encode(store.textAt(index)[slot]));
        } else if (f.valueKind === ValueKind.Bytes) {
          fields[f.name] = hex(store.bytesAt(index)[slot]!);
        }
      }

      const netId = store.netIds[slot]!;
      const entity: LogEntry = { netId };
      if (store.dims > 0) {
        const head = store.headEntry(slot);
        const at = store.segmentOffset(slot, head);
        entity.position = bitsOf(store.motionF64.subarray(at), store.dims);
        entity.velocity = bitsOf(store.motionF64.subarray(at + store.dims), store.dims);
        entity.t0 = store.motionU32[(slot * store.motionRecordBytes) / 4 + head];
        entity.epoch = store.motionU8[slot * store.motionRecordBytes + store.motionEpochOffset + head];
      }

      entity.fields = fields;
      entities.push({ netId, json: entity });
    }

    const live = (slot: number) => store.isLive(slot);
    const byNumber = (x: number, y: number) => x - y;
    archetypes[a.name] = {
      entities: entities.sort((x, y) => x.netId - y.netId).map((e) => e.json),
      entered: Array.from(store.entered.subarray(0, store.enteredCount))
        .filter(live)
        .map((slot) => store.netIds[slot]!)
        .sort(byNumber),
      updated: Array.from(store.updated.subarray(0, store.updatedCount))
        .filter(live)
        .sort((x, y) => store.netIds[x]! - store.netIds[y]!)
        .map((slot) => ({
          netId: store.netIds[slot],
          groups: store.updateMask[slot]! & 0xff,
          moved: (store.updateMask[slot]! & MOTION_CHANGE_BIT) !== 0,
        })),
      left: Array.from(store.left.subarray(0, store.leftCount)).sort(byNumber),
    };
  }

  const self = applier.selfState;
  let selfJson: LogEntry | null = null;
  if (self.archetype !== null) {
    const fields: LogEntry = {};
    for (const f of self.archetype.ownerFields) {
      if (self.present[f.index] !== 1) {
        continue;
      }

      if (f.valueKind === ValueKind.Number) {
        fields[f.name] = bitsOf(self.numbers[f.index]!, f.components);
      } else if (f.valueKind === ValueKind.Text) {
        fields[f.name] = hex(encoder.encode(self.texts[f.index]!));
      } else if (f.valueKind === ValueKind.Bytes) {
        fields[f.name] = hex(self.bytes[f.index]!);
      }
    }

    selfJson = {
      archetype: self.archetype.name,
      netId: self.netId,
      lastSeq: self.lastSeq,
      received: self.received,
      ownerMask: self.ownerMask,
      fields,
    };
  }

  const acks = applier.acks;
  const sources = applier.sources;
  const metrics: LogEntry = {};
  for (const m of plan.metrics) {
    metrics[m.name] = bitsOf(applier.stats.values.subarray(m.offset), m.valueCount);
  }

  return {
    tick: applier.tick,
    flags: applier.flags,
    periodUs: applier.periodUs,
    archetypes,
    events,
    self: selfJson,
    acks: Array.from({ length: acks.count }, (_, i) => ({ seq: acks.seq[i], reason: acks.reason[i] })),
    sources: Array.from({ length: sources.count }, (_, i) => ({
      requestId: sources.requestId[i],
      status: sources.status[i],
      code: sources.code[i],
    })),
    aggregates: applier.grids.map((g) => {
      const cells: LogEntry[] = [];
      for (let cell = 0; cell < g.cellCount; cell++) {
        const counts = Array.from(g.counts.subarray(cell * g.archetypeCount, (cell + 1) * g.archetypeCount));
        if (counts.some((c) => c !== 0)) {
          cells.push({ cell, counts });
        }
      }

      return {
        grid: g.index,
        cells,
        changed: Array.from(g.changed.subarray(0, g.changedCount)).sort((x, y) => x - y),
      };
    }),
    metrics,
    anomalies: applier.world.anomalies,
  };
}

describe('golden streams', () => {
  const names = goldenNames('stream-');

  it('finds the stream vectors', () => {
    expect(names).toContain('stream-kitchen-sink');
  });

  for (const name of names) {
    it(name, () => {
      const vector = goldenJson(name) as { catalog: string; snapshots: LogEntry[] };
      const events: LogEntry[] = [];
      const applier = new FrameApplier(CatalogPlan.compile(parseCatalog(goldenBin(vector.catalog))), {
        onEvent: (event) => events.push(renderEvent(event, applier)),
      });

      const messages = frames(goldenBin(name));
      expect(messages.length).toBe(vector.snapshots.length);
      messages.forEach((message, i) => {
        events.length = 0;
        applier.apply(message);
        expect(render(applier, [...events]), `${name}: snapshot after frame ${i}`).toEqual(vector.snapshots[i]);
      });
    });
  }
});
