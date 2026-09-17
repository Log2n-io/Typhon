import { beforeEach, describe, expect, it } from 'vitest';
import {
  Connection,
  forEachMessage,
  FrameApplier,
  MessageType,
  replayStream,
  StreamRecorder,
  type CatalogPlan,
} from '../src/index.js';
import { goldenBin } from './golden-support.js';
import { catalogPlan, FakeSocket, FakeTimers, kickMessage, pongMessage, welcomeMessage } from './net-support.js';

/*
 * A recorded stream is the session's bytes, in the `u32 len | message` framing of W31 — the framing the golden stream
 * vectors already use. Replaying it must rebuild the same store, byte for byte: that is what lets the demo ship on a
 * recording if the connection layer slips (09 § R8), and what makes a bug report reproducible.
 */

/** Everything a store holds, as comparable JSON: netIds, fields and the newest motion segment per live slot. */
function digest(applier: FrameApplier): unknown {
  const plan = applier.plan;
  return plan.archetypes.map((a) => {
    const store = applier.world.archetypeStore(a.idx);
    const slots = Array.from(store.live.subarray(0, store.liveCount)).sort(
      (x, y) => store.netIds[x]! - store.netIds[y]!,
    );
    return {
      name: a.name,
      entities: slots.map((slot) => {
        const fields: Record<string, unknown> = {};
        for (const f of a.fields) {
          const index = store.fieldIndex(f.name);
          if (index < 0) {
            continue;
          }

          const kind = store.schema.fields[index]!.kind;
          if (kind === 'text') {
            fields[f.name] = store.textAt(index)[slot];
          } else if (kind === 'bytes') {
            fields[f.name] = Array.from(store.bytesAt(index)[slot]!);
          } else {
            const components = f.components;
            fields[f.name] = Array.from(store.fieldAt(index).subarray(slot * components, (slot + 1) * components));
          }
        }

        const motion = store.hasPosition
          ? {
              t0: store.motionU32[(slot * store.motionRecordBytes) / 4 + store.headEntry(slot)],
              epoch: store.motionU8[slot * store.motionRecordBytes + store.motionEpochOffset + store.headEntry(slot)],
              segment: Array.from(
                store.motionF64.subarray(
                  store.segmentOffset(slot, store.headEntry(slot)),
                  store.segmentOffset(slot, store.headEntry(slot)) + store.motionStride,
                ),
              ),
            }
          : null;
        return { netId: store.netIds[slot], fields, motion };
      }),
      anomalies: applier.world.anomalies,
    };
  });
}

function applierFor(plan: CatalogPlan): FrameApplier {
  return new FrameApplier(plan);
}

describe('StreamRecorder', () => {
  it('frames what it recorded the way the golden streams are framed', () => {
    const recorder = new StreamRecorder();
    recorder.record(Uint8Array.of(1, 2, 3), 10);
    recorder.record(Uint8Array.of(), 20);
    recorder.record(Uint8Array.of(9), 30);

    expect([recorder.messageCount, recorder.byteLength]).toEqual([3, 4]);
    expect(Array.from(recorder.toStream())).toEqual([3, 0, 0, 0, 1, 2, 3, 0, 0, 0, 0, 1, 0, 0, 0, 9]);
    expect(Array.from(recorder.toTimeline())).toEqual([10, 20, 30]);

    const seen: number[][] = [];
    expect(forEachMessage(recorder.toStream(), (m) => seen.push(Array.from(m)))).toBe(3);
    expect(seen).toEqual([[1, 2, 3], [], [9]]);

    recorder.reset();
    expect([recorder.messageCount, recorder.toStream().length]).toEqual([0, 0]);
  });

  it('copies what it records: a socket may reuse its buffer', () => {
    const recorder = new StreamRecorder();
    const buffer = Uint8Array.of(7, 7);
    recorder.record(buffer);
    buffer[0] = 9;
    expect(Array.from(recorder.toStream().subarray(4))).toEqual([7, 7]);
  });

  it('refuses a truncated stream instead of applying half a message', () => {
    expect(() => forEachMessage(Uint8Array.of(1, 0, 0), () => undefined)).toThrow(/length prefix/);
    expect(() => forEachMessage(Uint8Array.of(4, 0, 0, 0, 1, 2), () => undefined)).toThrow(/declares 4 B/);
  });
});

describe('replayStream', () => {
  it('rebuilds the store the live frames built, frame for frame', () => {
    const plan = catalogPlan();
    const live = applierFor(plan);
    const stream = goldenBin('stream-kitchen-sink');
    const recorder = new StreamRecorder();
    forEachMessage(stream, (message, index) => {
      recorder.record(message, 1000 + index * 50);
      live.apply(message, 1000 + index * 50);
    });

    const replayed = applierFor(plan);
    const applied = replayStream(recorder.toStream(), replayed, { timeline: recorder.toTimeline() });
    expect(applied).toBe(recorder.messageCount);
    expect(Array.from(recorder.toStream())).toEqual(Array.from(stream));
    expect(digest(replayed)).toEqual(digest(live));
    expect([replayed.tick, replayed.world.frames]).toEqual([live.tick, live.world.frames]);
  });

  it('hands a session recording’s control messages over and applies only its frames', () => {
    const recorder = new StreamRecorder();
    recorder.record(welcomeMessage());
    recorder.record(goldenBin('tick-blocks'));
    recorder.record(pongMessage(1));
    recorder.record(kickMessage(1013, 'later'));

    const others: number[] = [];
    const applier = applierFor(catalogPlan());
    const applied = replayStream(recorder.toStream(), applier, { onOther: (m) => others.push(m[0]!) });
    expect(applied).toBe(1);
    expect(others).toEqual([MessageType.Welcome, MessageType.Pong, MessageType.Kick]);
    expect(applier.tick).toBeGreaterThan(0);
  });
});

describe('recording a connection', () => {
  beforeEach(() => {
    FakeSocket.reset();
  });

  it('records every inbound message of a session, and replays its frames into an identical store', () => {
    const timers = new FakeTimers();
    const recorder = new StreamRecorder();
    let live: FrameApplier | null = null;
    const connection = new Connection({
      url: 'wss://example.test/typhon',
      socket: (url, protocols) => new FakeSocket(url, protocols),
      timers,
      now: () => timers.now,
      handlers: {
        onMessage: (message, recvMs) => {
          recorder.record(message, recvMs);
        },
        onWelcome: (session) => {
          live = new FrameApplier(session.plan);
        },
        onTick: (message, recvMs) => {
          live?.apply(message, recvMs);
        },
      },
    });

    connection.connect();
    FakeSocket.latest.open();
    FakeSocket.latest.deliver(welcomeMessage());
    forEachMessage(goldenBin('stream-kitchen-sink'), (frame) => {
      timers.advance(50);
      FakeSocket.latest.deliver(frame);
    });

    const applier = live!;
    expect(applier.world.entityCount).toBeGreaterThan(0);
    // WELCOME first, then every frame.
    expect(recorder.messageCount).toBe(1 + forEachMessage(goldenBin('stream-kitchen-sink'), () => undefined));

    const replayed = applierFor(catalogPlan());
    replayStream(recorder.toStream(), replayed, { timeline: recorder.toTimeline() });
    expect(digest(replayed)).toEqual(digest(applier));
  });
});
