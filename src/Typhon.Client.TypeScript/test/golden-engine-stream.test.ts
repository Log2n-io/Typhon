import { describe, expect, it } from 'vitest';
import {
  CatalogPlan,
  catalogHashToHex,
  FrameApplier,
  MessageType,
  parseCatalog,
  parseMessage,
  readWelcome,
  TickFlags,
  TickReader,
  type ArchetypePlan,
  type GridPlan,
  type MessagePlan,
  type MetricPlan,
  type TickSink,
} from '../src/index.js';
import { goldenBin, goldenJson } from './golden-support.js';

/*
 * stream-engine: the one golden stream produced by the ENGINE's own ENTITIES encoder rather than by a writer built from
 * an object model — bytes copied out of replication blocks. It is shaped as a per-frame CALL LOG, not as a replica
 * snapshot, which is why golden-stream.test.ts does not read it: the two files answer different questions and would
 * corrupt each other's if one glob swept both.
 *
 * This is the TypeScript half of the cross-language pin. `Typhon.Client.Tests`' `EngineStreamGoldenTests` asserts the
 * same file against the same expectation through the .NET decoder; the vector is only a pin while BOTH decoders read
 * it, because a call log one implementation agrees with is a description of that implementation.
 */

/**
 * Records the calls each frame's decode makes, spelled exactly as the .NET fixture spells them.
 *
 * A sink method declares only the parameters it logs: TypeScript lets an implementation take fewer arguments than the
 * interface hands it, which says "this one ignores the rest" without a row of underscore-prefixed names the linter then
 * objects to.
 */
class RecordingSink implements TickSink {
  readonly frames: string[][] = [];
  readonly ticks: number[] = [];
  readonly flags: number[] = [];
  private calls: string[] = [];

  beginTick(tick: number, flags: number): void {
    this.calls = [`beginTick ${tick}`];
    this.ticks.push(tick);
    this.flags.push(flags);
  }

  beginEntities(archetype: ArchetypePlan): void {
    this.calls.push(`beginEntities ${archetype.name}`);
  }

  enter(netId: number): void {
    this.calls.push(`enter ${netId}`);
  }

  segment(netId: number): void {
    this.calls.push(`segment ${netId}`);
  }

  state(netId: number, groupMask: number): void {
    this.calls.push(`state ${netId} 0x${groupMask.toString(16).padStart(2, '0')}`);
  }

  leave(netId: number): void {
    this.calls.push(`leave ${netId}`);
  }

  event(type: MessagePlan): void {
    this.calls.push(`event ${type.name}`);
  }

  // The archetype comes first and is not logged; `no-unused-vars` defaults to `after-used`, so a leading parameter kept
  // only to reach the next one is not a finding.
  self(_archetype: ArchetypePlan, netId: number): void {
    this.calls.push(`self ${netId}`);
  }

  ack(seq: number): void {
    this.calls.push(`ack ${seq}`);
  }

  source(requestId: number): void {
    this.calls.push(`source ${requestId}`);
  }

  beginAggregate(grid: GridPlan): void {
    this.calls.push(`beginAggregate ${grid.idx}`);
  }

  aggregateCell(cell: number): void {
    this.calls.push(`aggregateCell ${cell}`);
  }

  metric(metric: MetricPlan): void {
    this.calls.push(`metric ${metric.name}`);
  }

  debug(subType: number): void {
    this.calls.push(`debug ${subType}`);
  }

  ext(appTypeId: number): void {
    this.calls.push(`ext ${appTypeId}`);
  }

  unknownBlock(blockType: number): void {
    this.calls.push(`unknownBlock ${blockType}`);
  }

  endTick(): void {
    this.calls.push('endTick');
    this.frames.push(this.calls);
  }

  number(): void {}

  text(): void {}

  bytes(): void {}

  list(): void {}
}

/** Splits a TCP stream into its messages: `u32 len` little-endian, excluding itself (03 § 10, W31). */
function unframe(stream: Uint8Array): Uint8Array[] {
  const view = new DataView(stream.buffer, stream.byteOffset, stream.byteLength);
  const messages: Uint8Array[] = [];
  for (let at = 0; at < stream.length;) {
    const length = view.getUint32(at, true);
    messages.push(stream.subarray(at + 4, at + 4 + length));
    at += 4 + length;
  }

  return messages;
}

describe('the engine stream', () => {
  const vector = goldenJson('stream-engine') as {
    catalogHash: string;
    frames: { tick: number; flags: number; calls: string[] }[];
  };

  it('decodes into the calls the vector names, frame for frame', () => {
    const messages = unframe(goldenBin('stream-engine'));
    expect(messages.length, 'the stream is a WELCOME and at least one TICK').toBeGreaterThan(1);

    const welcome = parseMessage(messages[0]!, MessageType.Welcome, readWelcome);
    expect(
      welcome.catalogJson.length,
      'the vector carries its own catalog, so a reader needs nothing else',
    ).toBeGreaterThan(0);

    // The .bin and the .json are two files that can drift apart; the digest is what stops them doing it silently.
    expect(catalogHashToHex(welcome.catalogHash)).toBe(vector.catalogHash);

    const plan = CatalogPlan.compile(parseCatalog(welcome.catalogJson));
    const applier = new FrameApplier(plan, {});
    const reader = new TickReader(plan);
    const sink = new RecordingSink();

    for (const frame of messages.slice(1)) {
      applier.apply(frame);
      reader.read(frame, sink);
    }

    expect(sink.frames).toEqual(vector.frames.map((f) => f.calls));
    expect(sink.ticks).toEqual(vector.frames.map((f) => f.tick));
    expect(sink.flags).toEqual(vector.frames.map((f) => f.flags));

    const creature = plan.archetypeByName('ProjCreature')!;
    const rock = plan.archetypeByName('ProjRock')!;

    // The same four assertions the .NET fixture ends on: applying the stream leaves the replica holding what the
    // server held. The last frame is the RESET refill, so the counts are that frame's, not the churn's.
    expect(
      applier.world.anomalies,
      "the engine's stream is self-consistent: every update names an entity the store holds",
    ).toBe(0);
    expect(applier.world.frames, 'every frame applied').toBe(messages.length - 1);
    expect(applier.world.archetypeStore(creature.idx).liveCount, 'four creatures survived the churn').toBe(4);
    expect(applier.world.archetypeStore(rock.idx).liveCount, 'the static archetype re-entered with the rest').toBe(3);
    expect(applier.flags & TickFlags.Reset, 'the last frame is the profile switch').toBe(TickFlags.Reset);
  });
});

describe('the deep engine stream', () => {
  const vector = goldenJson('stream-engine-3d') as {
    catalogHash: string;
    frames: { tick: number; flags: number; calls: string[] }[];
  };

  it('decodes a deep grid’s frames — pos3 movers, 2D walkers on z = 0, statics — into the calls the vector names', () => {
    const messages = unframe(goldenBin('stream-engine-3d'));
    const welcome = parseMessage(messages[0]!, MessageType.Welcome, readWelcome);
    expect(catalogHashToHex(welcome.catalogHash)).toBe(vector.catalogHash);

    const plan = CatalogPlan.compile(parseCatalog(welcome.catalogJson));
    const applier = new FrameApplier(plan, {});
    const reader = new TickReader(plan);
    const sink = new RecordingSink();
    for (const frame of messages.slice(1)) {
      applier.apply(frame);
      reader.read(frame, sink);
    }

    expect(sink.frames).toEqual(vector.frames.map((f) => f.calls));
    expect(sink.ticks).toEqual(vector.frames.map((f) => f.tick));
    expect(sink.flags).toEqual(vector.frames.map((f) => f.flags));

    const flyer = plan.archetypeByName('ProjFlyer')!;
    expect(flyer.position?.dims, 'the flyer’s position is pos3').toBe(3);
    expect(applier.world.anomalies).toBe(0);
    expect(applier.world.archetypeStore(flyer.idx).liveCount, 'five flyers, two left, one entered').toBe(4);
    expect(applier.world.archetypeStore(plan.archetypeByName('ProjCreature')!.idx).liveCount).toBe(3);
    expect(applier.world.archetypeStore(plan.archetypeByName('ProjRock')!.idx).liveCount).toBe(2);
    expect(applier.flags & TickFlags.Reset, 'the last frame is the profile switch').toBe(TickFlags.Reset);
  });
});
