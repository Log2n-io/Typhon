import { MessageType } from '../protocol/constants.js';
import { malformed } from '../protocol/errors.js';

/** What a replay needs of a store's applier: {@link FrameApplier.apply}. */
export interface FrameConsumer {
  apply(message: Uint8Array, recvMs?: number): void;
}

/**
 * Records inbound messages exactly as they arrived and hands them back as one stream, in the `u32 len | message` framing
 * the TCP transport and the golden stream vectors use (W31). A stream replays byte for byte: the same bytes reach the
 * same decoder, so a recorded session is a test, an offline demo and a bug report.
 *
 * Copies every message: a `WebSocket` hands out a buffer it may reuse, and the store keeps views of nothing.
 */
export class StreamRecorder {
  private chunks: Uint8Array[] = [];
  private times: number[] = [];
  private bytes = 0;

  /** Messages recorded. */
  get messageCount(): number {
    return this.chunks.length;
  }

  /** Bytes of message payload, framing excluded. */
  get byteLength(): number {
    return this.bytes;
  }

  /** Records one inbound message, whatever its type, with the local time it arrived. */
  record(message: Uint8Array, recvMs = 0): void {
    this.chunks.push(message.slice());
    this.times.push(recvMs);
    this.bytes += message.length;
  }

  reset(): void {
    this.chunks = [];
    this.times = [];
    this.bytes = 0;
  }

  /** The recorded stream: `u32 len | message` per message, little-endian, the length excluding itself. */
  toStream(): Uint8Array {
    const stream = new Uint8Array(this.bytes + 4 * this.chunks.length);
    let at = 0;
    for (const chunk of this.chunks) {
      const length = chunk.length;
      stream[at] = length & 0xff;
      stream[at + 1] = (length >>> 8) & 0xff;
      stream[at + 2] = (length >>> 16) & 0xff;
      stream[at + 3] = (length >>> 24) & 0xff;
      stream.set(chunk, at + 4);
      at += 4 + length;
    }

    return stream;
  }

  /** The local receive times, one per message, in the stream's order. */
  toTimeline(): Float64Array {
    return Float64Array.from(this.times);
  }
}

/**
 * Visits every message of a recorded stream, in order. The views point into `stream`: a consumer that keeps one copies
 * it. A truncated stream is malformed (1007), as it would be on the wire.
 */
export function forEachMessage(stream: Uint8Array, visit: (message: Uint8Array, index: number) => void): number {
  let at = 0;
  let index = 0;
  while (at < stream.length) {
    if (stream.length - at < 4) {
      throw malformed(`the recorded stream ends inside a length prefix, ${stream.length - at} byte(s) in`);
    }

    const length = (stream[at]! | (stream[at + 1]! << 8) | (stream[at + 2]! << 16) | (stream[at + 3]! << 24)) >>> 0;
    if (length > stream.length - at - 4) {
      throw malformed(`the recorded stream declares ${length} B for message ${index}, and holds fewer`);
    }

    visit(stream.subarray(at + 4, at + 4 + length), index);
    at += 4 + length;
    index++;
  }

  return index;
}

/**
 * Applies a recorded stream's `TICK` messages to a store, in order, and returns how many it applied. Other messages —
 * `WELCOME`, `PONG`, `KICK` — go to `onOther`, which a replay of a whole session uses to rebuild its catalog.
 *
 * `timeline` supplies each message's receive time, so a clock sees the timing the session had; without it frames are
 * applied with no time, and the clock stays where it was.
 */
export function replayStream(
  stream: Uint8Array,
  consumer: FrameConsumer,
  options: {
    readonly timeline?: ArrayLike<number>;
    readonly onOther?: (message: Uint8Array, index: number) => void;
  } = {},
): number {
  const timeline = options.timeline;
  let applied = 0;
  forEachMessage(stream, (message, index) => {
    if (message[0] === MessageType.Tick) {
      const recvMs = timeline === undefined ? undefined : timeline[index];
      if (recvMs === undefined) {
        consumer.apply(message);
      } else {
        consumer.apply(message, recvMs);
      }

      applied++;
      return;
    }

    options.onOther?.(message, index);
  });
  return applied;
}
