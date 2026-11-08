import { monotonicNow } from '../clock/now.js';
import type { CatalogPlan, FieldPlan, MessagePlan } from '../protocol/catalog.js';
import { MessageType, ProtocolConstants } from '../protocol/constants.js';
import { writeSection, type FieldValues } from '../protocol/field-codec.js';
import { catalogHashToHex } from '../protocol/messages.js';
import { uintInRange } from '../protocol/range.js';
import { RealmFrame } from '../protocol/realm-frame.js';
import { varuSize, WireWriter } from '../protocol/writer.js';

/** Any frame at the command width: a queued command's size depends on its values' widths, never on the realm's bounds. */
const MEASURING_FRAME = new RealmFrame(
  0,
  0,
  0,
  0,
  ProtocolConstants.commandPositionBits,
  1,
  true,
  [-1, -1, -1],
  [1, 1, 1],
);

/** What {@link CommandQueue.enqueue} returns instead of a sequence number. */
export const CommandRefused = {
  /** The local token bucket for this command type is empty: the server would have counted a drop (`ACKS`, reason 1). */
  RateLimited: -1,
} as const;

/**
 * One command type as `typhon-codegen` emits it (#1614): its catalog name and an encoder of its body from a typed record,
 * the fields in the wire order of the catalog the module was generated from. Handed to {@link CommandQueue.enqueueTyped}.
 */
export interface GeneratedCommand<T> {
  readonly name: string;
  /** Writes the body of `value`; `fields` are the command's body fields in wire order, `frame` the realm at command width. */
  readonly encode: (w: WireWriter, value: T, fields: readonly FieldPlan[], frame: RealmFrame | null) => void;
}

/** A generated module's command set: the catalog it was generated from, checked against the session's. */
export interface GeneratedCommands {
  readonly catalogHash: string;
}

export interface CommandQueueOptions {
  readonly plan: CatalogPlan;
  readonly now?: () => number;
  /** The most one `COMMANDS` message may carry; default the catalog's `limits.clientMessageBytes` (W30). */
  readonly maxMessageBytes?: number;
  /** The sequence number to start from; default 1. Seq 0 is ordinary, but starting at 1 reads better in a log. */
  readonly firstSeq?: number;
  /**
   * The generated module's `commands`, for {@link CommandQueue.enqueueTyped}, and the session's catalog hash
   * (`WELCOME`): a module generated from another catalog is refused here, as `FrameApplier` refuses its decoders.
   */
  readonly commands?: GeneratedCommands;
  /** The session's catalog hash (`SessionInfo.catalogHash`), as `FrameApplier` takes it. */
  readonly catalogHash?: Uint8Array;
}

/** A queued command: a reused slot, so a steady frame allocates none. */
interface Pending {
  type: MessagePlan;
  seq: number;
  /** The encoded size inside a message: index, seq and body. */
  bytes: number;
  /** A name-keyed command's values, encoded at flush; `null` for a typed one, encoded at enqueue. */
  values: FieldValues | null;
  /** A typed command's body in {@link CommandQueue}'s typed buffer. */
  bodyStart: number;
  bodyEnd: number;
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
 * - **Two ways in.** {@link enqueueTyped} takes a generated command and its typed record (#1614): a misspelled field is a
 *   compile error, the body is encoded at once by specialized code into a reused buffer, and nothing is allocated.
 *   {@link enqueue} takes a plan and values by name, read at flush. Both share one queue, one seq space, one order.
 * - **Seq.** `u16`, wrapping, compared with serial arithmetic (RFC 1982); `SELF.lastSeq` reports the highest drained
 *   (W31). One space across every command type.
 * - **Latest wins.** A `latest` command removes the pending one of its type, as the server's own slot would: only the
 *   newest travels, last, with a fresh seq — so seqs stay in order on the wire.
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
  private readonly typed = new WireWriter(256);
  private readonly buckets = new Map<number, Bucket>();
  private readonly generated: GeneratedCommands | null;
  private readonly typedPlans = new Map<object, MessagePlan>();
  private readonly slots: Pending[] = [];
  private count = 0;
  private seq: number;
  private dropped = 0;
  private coalesced = 0;
  private unframed = 0;

  constructor(options: CommandQueueOptions) {
    this.plan = options.plan;
    this.now = options.now ?? monotonicNow;
    this.maxMessageBytes = options.maxMessageBytes ?? options.plan.catalog.limits.clientMessageBytes;
    this.seq = (options.firstSeq ?? 1) & 0xffff;
    const commands = this.plan.catalog.commands;
    for (let i = 0; i < commands.length; i++) {
      this.positionByIdx.set(commands[i]!.idx, i);
    }

    this.generated = options.commands ?? null;
    if (this.generated !== null) {
      if (options.catalogHash === undefined) {
        throw new Error('generated commands need the session catalog hash (catalogHash) to be checked against');
      }

      const hash = catalogHashToHex(options.catalogHash);
      if (hash !== this.generated.catalogHash) {
        throw new Error(
          `the generated commands come from catalog ${this.generated.catalogHash}, but the session's is ${hash}: regenerate them with typhon-codegen`,
        );
      }
    }
  }

  /** Commands waiting for the next {@link flush}. */
  get pendingCount(): number {
    return this.count;
  }

  /** Commands the local rate pre-check refused. */
  get rateLimited(): number {
    return this.dropped;
  }

  /** Pending commands a newer one of the same `latest` type replaced. */
  get coalescedCount(): number {
    return this.coalesced;
  }

  /**
   * Pending commands a flush dropped because they carry a realm-framed field and the session held no realm: there is no
   * frame to encode them over, and the server would refuse them anyway (SUB-30).
   */
  get droppedWithoutRealm(): number {
    return this.unframed;
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

    // Measured and checked first, the token taken last: a command refused for its values or its size spends no rate.
    const bytes = this.measure(type, values);
    this.refuseOversized(type, bytes);
    if (!this.take(type)) {
      this.dropped++;
      return CommandRefused.RateLimited;
    }

    const seq = this.nextSeqTaken();
    const slot = this.append(type);
    slot.seq = seq;
    slot.bytes = bytes;
    slot.values = values;
    return seq;
  }

  /**
   * Queues one command from its generated descriptor and typed record (#1614) and returns its sequence number, or
   * {@link CommandRefused.RateLimited}. The body is encoded now, so `value` may be reused at once — one record per
   * frame, rewritten in place, is the allocation-free pattern. `frame` (the session's realm, `FrameApplier.realmFrame`)
   * is needed only by a command with a realm-framed field, and is then refused when `null`.
   */
  enqueueTyped<T>(command: GeneratedCommand<T>, value: T, frame: RealmFrame | null = null): number {
    const type = this.typedPlan(command);

    // Encoded behind what is already queued; a latest command's predecessor is removed only once this one has encoded.
    const typed = this.typed;
    const start = typed.position;
    try {
      command.encode(typed, value, type.body.fields, frame === null ? null : frame.forCommands);
    } catch (error) {
      typed.truncate(start);
      throw error;
    }

    const bytes = varuSize(type.idx) + 2 + typed.position - start;
    try {
      this.refuseOversized(type, bytes);
    } catch (error) {
      typed.truncate(start);
      throw error;
    }

    // The token is taken once the command encoded and fits: a refused one spends no rate.
    if (!this.take(type)) {
      typed.truncate(start);
      this.dropped++;
      return CommandRefused.RateLimited;
    }

    const seq = this.nextSeqTaken();
    const slot = this.append(type);
    slot.seq = seq;
    slot.bytes = bytes;
    slot.values = null;
    slot.bodyStart = start;
    slot.bodyEnd = typed.position;
    return seq;
  }

  /**
   * Writes the pending commands as `COMMANDS` messages for `clientTick` and hands each to `send`, then clears them.
   * Returns the messages sent; 0 when nothing was pending. Each message's bytes are valid until the next call.
   *
   * `clientTick` is **the newest server tick this client had applied when the batch was built** (§ 10) — never a
   * predicted tick, never a local frame counter: `FrameApplier.tick`. `frame` is the session's realm frame
   * (`FrameApplier.realmFrame`), which a name-keyed realm-framed field — a position, a region's vertices — travels over.
   */
  flush(clientTick: number, send: (message: Uint8Array) => void, frame: RealmFrame | null = null): number {
    if (frame === null) {
      this.dropUnframed();
    }

    const count = this.count;
    if (count === 0) {
      this.typed.reset();
      return 0;
    }

    const commandFrame = frame === null ? null : frame.forCommands;
    const tick = uintInRange(clientTick, 0xffffffff, 'clientTick');
    const slots = this.slots;
    const w = this.writer;
    let messages = 0;
    let from = 0;
    // Emptied however the flush ends: a send that throws midway must not leave the batch to go out a second time, with seqs the server
    // already executed.
    try {
      while (from < count) {
        let bytes = this.headerBytes(1);
        let to = from;
        // One more command may also widen the count's varint (1 B to 2 B at 128): the widening is part of what must fit.
        while (to < count) {
          const next = slots[to]!.bytes + this.headerBytes(to - from + 1) - this.headerBytes(to - from);
          if (bytes + next > this.maxMessageBytes) {
            break;
          }

          bytes += next;
          to++;
        }

        w.reset();
        w.u8(MessageType.Commands);
        w.u32(tick);
        w.varu(to - from);
        for (let i = from; i < to; i++) {
          const slot = slots[i]!;
          w.varu(slot.type.idx);
          w.u16(slot.seq);
          if (slot.values === null) {
            w.rawRange(this.typed.bytes, slot.bodyStart, slot.bodyEnd);
          } else {
            writeSection(w, slot.type.body, slot.values, true, commandFrame);
          }
        }

        send(w.written());
        messages++;
        from = to;
      }
    } finally {
      this.clear();
    }

    return messages;
  }

  /** Drops the pending commands without sending them (a disconnect: their seqs are never acknowledged). */
  clear(): void {
    for (let i = 0; i < this.count; i++) {
      this.slots[i]!.values = null;
    }

    this.count = 0;
    this.typed.reset();
  }

  // With no realm, a name-keyed command carrying a realm-framed field cannot be encoded: it is dropped (counted) and the others go. A typed one
  // was encoded at enqueue, over the frame it was given.
  private dropUnframed(): void {
    const slots = this.slots;
    let kept = 0;
    for (let i = 0; i < this.count; i++) {
      const slot = slots[i]!;
      let keep = true;
      if (slot.values !== null) {
        try {
          this.measuring.reset();
          writeSection(this.measuring, slot.type.body, slot.values, true, null);
        } catch {
          this.unframed++;
          keep = false;
        }
      }

      if (keep) {
        this.swap(kept++, i);
      }
    }

    this.count = kept;
  }

  private typedPlan<T>(command: GeneratedCommand<T>): MessagePlan {
    const known = this.typedPlans.get(command);
    if (known !== undefined) {
      return known;
    }

    if (this.generated === null) {
      throw new Error(
        `enqueueTyped('${command.name}') needs the generated module's commands and the session's catalogHash in the queue's options`,
      );
    }

    const type = this.plan.commandByName(command.name);
    if (type === null) {
      throw new Error(`the catalog this session negotiated declares no command named '${command.name}'`);
    }

    this.typedPlans.set(command, type);
    return type;
  }

  // Takes the next slot, after removing the pending one of a latest type: the newest travels last, so seqs stay in order.
  private append(type: MessagePlan): Pending {
    const latest = this.plan.catalog.commands[this.commandPosition(type)]?.delivery === 'latest';
    if (latest) {
      for (let i = 0; i < this.count; i++) {
        if (this.slots[i]!.type === type) {
          for (let j = i; j < this.count - 1; j++) {
            this.swap(j, j + 1);
          }

          this.count--;
          this.slots[this.count]!.values = null;
          this.coalesced++;
          break;
        }
      }
    }

    let slot = this.slots[this.count];
    if (slot === undefined) {
      slot = { type, seq: 0, bytes: 0, values: null, bodyStart: 0, bodyEnd: 0 };
      this.slots.push(slot);
    }

    slot.type = type;
    this.count++;
    return slot;
  }

  private swap(a: number, b: number): void {
    if (a !== b) {
      const t = this.slots[a]!;
      this.slots[a] = this.slots[b]!;
      this.slots[b] = t;
    }
  }

  private nextSeqTaken(): number {
    const seq = this.seq;
    this.seq = (seq + 1) & 0xffff;
    return seq;
  }

  private refuseOversized(type: MessagePlan, bytes: number): void {
    if (bytes > this.maxMessageBytes - this.headerBytes(1)) {
      throw new RangeError(`command '${type.name}' needs ${bytes} B, above the ${this.maxMessageBytes} B limit`);
    }
  }

  private headerBytes(count: number): number {
    // u8 type | u32 clientTick | varu count
    return 5 + varuSize(count);
  }

  private measure(type: MessagePlan, values: FieldValues): number {
    this.measuring.reset();
    // A realm-framed field's width is the command width whatever the realm (typhon.3), so any frame measures it.
    writeSection(this.measuring, type.body, values, true, MEASURING_FRAME);
    return varuSize(type.idx) + 2 + this.measuring.position;
  }

  /**
   * Where a command sits in the catalog, by its `idx`.
   *
   * A map rather than the `findIndex` this was: `commandPosition` is called twice for every `enqueue` — once by `take`
   * for the rate bucket and once for the delivery mode — so a catalog of C commands cost 2·C·N scans per frame of N
   * commands, on the input path, to answer a question whose answer never changes.
   */
  private readonly positionByIdx = new Map<number, number>();

  private commandPosition(type: MessagePlan): number {
    return this.positionByIdx.get(type.idx) ?? -1;
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
