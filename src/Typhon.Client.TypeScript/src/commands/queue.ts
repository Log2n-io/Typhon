import type { CatalogPlan, MessagePlan } from '../protocol/catalog.js';
import { writeCommands, type CommandInput } from '../protocol/commands.js';
import { writeSection, type FieldValues } from '../protocol/field-codec.js';
import { varuSize, WireWriter } from '../protocol/writer.js';

/** What {@link CommandQueue.enqueue} returns instead of a sequence number. */
export const CommandRefused = {
  /** The local token bucket for this command type is empty: the server would have counted a drop (`ACKS`, reason 1). */
  RateLimited: -1,
} as const;

export interface CommandQueueOptions {
  readonly plan: CatalogPlan;
  readonly now?: () => number;
  /** The most one `COMMANDS` message may carry; default the catalog's `limits.clientMessageBytes` (W30). */
  readonly maxMessageBytes?: number;
  /** The sequence number to start from; default 1. Seq 0 is ordinary, but starting at 1 reads better in a log. */
  readonly firstSeq?: number;
}

interface Pending {
  readonly type: MessagePlan;
  readonly values: FieldValues;
  readonly seq: number;
  /** The encoded size of this command inside a message: index, seq and body. */
  readonly bytes: number;
}

/** A command type's token bucket, from its catalog `rate`. */
interface Bucket {
  tokens: number;
  lastMs: number;
  readonly perSec: number;
  readonly burst: number;
}

/**
 * Batches a rendered frame's commands into `COMMANDS` messages (§ 3): one sequence space per session, one `clientTick`
 * per message, and a local rate pre-check so a client drops its own excess instead of having the server drop it.
 *
 * - **Seq.** `u16`, wrapping, compared with serial arithmetic (RFC 1982); `SELF.lastSeq` reports the highest drained
 *   (W31). One space across every command type.
 * - **Latest wins.** A `latest` command overwrites the pending one of its type, as the server's own slot would: only the
 *   newest travels, and it carries a fresh seq.
 * - **The cap.** A batch is split into as many messages as `limits.clientMessageBytes` needs, all with the same
 *   `clientTick`; a single command above the cap is a caller error and throws.
 * - **The rate.** A token bucket per type, from the catalog's `rate` (`perSec`, `burst`); a type with no rate is
 *   unlimited. This is a pre-check, not the authority: the server's bucket decides.
 */
export class CommandQueue {
  readonly plan: CatalogPlan;
  private readonly now: () => number;
  private readonly maxMessageBytes: number;
  private readonly measuring = new WireWriter(512);
  private readonly writer = new WireWriter(1024);
  private readonly buckets = new Map<number, Bucket>();
  private readonly pending: Pending[] = [];
  private seq: number;
  private dropped = 0;
  private coalesced = 0;

  constructor(options: CommandQueueOptions) {
    this.plan = options.plan;
    this.now = options.now ?? Date.now;
    this.maxMessageBytes = options.maxMessageBytes ?? options.plan.catalog.limits.clientMessageBytes;
    this.seq = (options.firstSeq ?? 1) & 0xffff;
  }

  /** Commands waiting for the next {@link flush}. */
  get pendingCount(): number {
    return this.pending.length;
  }

  /** Commands the local rate pre-check refused. */
  get rateLimited(): number {
    return this.dropped;
  }

  /** Pending commands a newer one of the same `latest` type replaced. */
  get coalescedCount(): number {
    return this.coalesced;
  }

  /** The sequence number the next accepted command will carry. */
  get nextSeq(): number {
    return this.seq;
  }

  /**
   * Queues one command for the next flush and returns its sequence number, or {@link CommandRefused.RateLimited} when
   * the local bucket refuses it. `values` is read at flush time, so a caller must not mutate it meanwhile.
   */
  enqueue(type: MessagePlan, values: FieldValues): number {
    if (this.plan.command(type.idx) !== type) {
      throw new Error(`command '${type.name}' does not belong to this catalog`);
    }

    if (!this.take(type)) {
      this.dropped++;
      return CommandRefused.RateLimited;
    }

    const seq = this.seq;
    this.seq = (seq + 1) & 0xffff;
    const entry: Pending = { type, values, seq, bytes: this.measure(type, values) };
    if (entry.bytes > this.maxMessageBytes - this.headerBytes(1)) {
      throw new RangeError(`command '${type.name}' needs ${entry.bytes} B, above the ${this.maxMessageBytes} B limit`);
    }

    const latest = this.plan.catalog.commands[this.commandPosition(type)]?.delivery === 'latest';
    const existing = latest ? this.pending.findIndex((p) => p.type === type) : -1;
    if (existing >= 0) {
      this.pending[existing] = entry;
      this.coalesced++;
    } else {
      this.pending.push(entry);
    }

    return seq;
  }

  /**
   * Writes the pending commands as `COMMANDS` messages for `clientTick` and hands each to `send`, then clears them.
   * Returns the messages sent; 0 when nothing was pending. Each message's bytes are valid until the next call.
   *
   * `clientTick` is **the newest server tick this client had applied when the batch was built** (§ 10) — never a
   * predicted tick, never a local frame counter: `FrameApplier.tick`.
   */
  flush(clientTick: number, send: (message: Uint8Array) => void): number {
    const pending = this.pending;
    if (pending.length === 0) {
      return 0;
    }

    let messages = 0;
    let from = 0;
    while (from < pending.length) {
      let bytes = this.headerBytes(1);
      let to = from;
      while (to < pending.length && bytes + pending[to]!.bytes <= this.maxMessageBytes) {
        bytes += pending[to]!.bytes;
        to++;
        // One more command may widen the count's varint.
        bytes += this.headerBytes(to - from) - this.headerBytes(to - from - 1);
      }

      const batch: CommandInput[] = [];
      for (let i = from; i < to; i++) {
        const entry = pending[i]!;
        batch.push({ type: entry.type, seq: entry.seq, values: entry.values });
      }

      this.writer.reset();
      writeCommands(this.writer, clientTick, batch);
      send(this.writer.written());
      messages++;
      from = to;
    }

    pending.length = 0;
    return messages;
  }

  /** Drops the pending commands without sending them (a disconnect: their seqs are never acknowledged). */
  clear(): void {
    this.pending.length = 0;
  }

  private headerBytes(count: number): number {
    // u8 type | u32 clientTick | varu count
    return 5 + varuSize(count);
  }

  private measure(type: MessagePlan, values: FieldValues): number {
    this.measuring.reset();
    writeSection(this.measuring, type.body, values, true);
    return varuSize(type.idx) + 2 + this.measuring.position;
  }

  private commandPosition(type: MessagePlan): number {
    return this.plan.catalog.commands.findIndex((c) => c.idx === type.idx);
  }

  private take(type: MessagePlan): boolean {
    const rate = this.plan.catalog.commands[this.commandPosition(type)]?.rate;
    if (rate === undefined) {
      return true;
    }

    const nowMs = this.now();
    let bucket = this.buckets.get(type.idx);
    if (bucket === undefined) {
      bucket = { tokens: rate.burst, lastMs: nowMs, perSec: rate.perSec, burst: rate.burst };
      this.buckets.set(type.idx, bucket);
    }

    const elapsed = Math.max(0, nowMs - bucket.lastMs);
    bucket.lastMs = nowMs;
    bucket.tokens = Math.min(bucket.burst, bucket.tokens + (elapsed / 1000) * bucket.perSec);
    if (bucket.tokens < 1) {
      return false;
    }

    bucket.tokens -= 1;
    return true;
  }
}
