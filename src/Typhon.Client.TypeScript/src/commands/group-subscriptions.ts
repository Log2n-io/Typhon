import type { AckList, SourceList } from '../apply/frame-state.js';
import type { CatalogPlan, MessagePlan } from '../protocol/catalog.js';
import { AckReason, BuiltInCommand, SourceStatus } from '../protocol/constants.js';
import type { FieldValues } from '../protocol/field-codec.js';
import { CommandRefused, type CommandQueue } from './queue.js';

/** A `SubscribeRequest`'s `op` (14 § 4, P13). */
export const SubscribeOp = {
  Subscribe: 1,
  Unsubscribe: 2,
} as const;

/**
 * How a request ended: `applied` (`SOURCE_APPLIED`: the key is held and complete, or released), `refused`
 * (`SOURCE_ERROR`, with its `code`), `rejected` (an `ACK` other than `RateLimited`, with its `ackReason`) or `dropped`
 * (the session ended first: whether the server took it is unknown, and a new session holds none of it).
 */
export type SubscribeOutcome = 'applied' | 'refused' | 'rejected' | 'dropped';

/** One `SubscribeRequest`'s answer. */
export interface SubscribeResult {
  readonly requestId: number;
  /** The explicit set's index in the catalog's `groupSets`. */
  readonly set: number;
  readonly key: bigint;
  readonly op: number;
  readonly outcome: SubscribeOutcome;
  /** The `SOURCE_ERROR` code when `refused`, else 0. */
  readonly code: number;
  /** The `ACK` reason when `rejected`, else 0. */
  readonly ackReason: number;
}

/** Sends one command, returning its sequence, or a negative value when it was not sent (no token, no session). */
export type TrySend = (command: MessagePlan, values: FieldValues) => number;

export interface GroupSubscriptionsOptions {
  /** Called for each request that ends, in the order they end, before its promise settles. */
  readonly onAnswer?: (result: SubscribeResult) => void;
}

interface Entry {
  readonly requestId: number;
  readonly set: number;
  readonly key: bigint;
  readonly op: number;
  seq: number;
  readonly settle: ((result: SubscribeResult) => void) | null;
}

/**
 * A session's `SubscribeRequest`s, from the call to the answer (14 § 4, P13): the same state machine as the .NET and
 * native SDKs', pinned across them by the `subscribe-requests` vector. It sends nothing and keeps no clock itself.
 *
 * - **Pump** it into the session's {@link CommandQueue} before each flush: the queued requests go oldest first while the
 *   queue's token bucket for the command — its catalog rate, 5 a second past a burst of the server's
 *   `MaxGroupsPerSession` — has one, and the rest wait.
 * - **Observe** each applied frame: its `ACKS`, then its `SOURCES`. An `ACK` `RateLimited` puts the request back at the
 *   head of the queue, to go again under a new sequence; any other `ACK` rejects it.
 * - **Reset** it when the session ends: what is still waiting is dropped.
 *
 * An `ACK` can be lost — a session further behind than the server's ACK history never sees it — and that request then
 * stays sent with no answer. Asking again is safe: a subscribe of a key held, or an unsubscribe of one not held, is
 * answered like the first.
 */
export class GroupSubscriptions {
  private readonly plan: CatalogPlan;
  private readonly command: MessagePlan | null;
  private readonly onAnswer: ((result: SubscribeResult) => void) | undefined;
  private readonly queued: Entry[] = [];
  private readonly sent = new Map<number, Entry>();
  private readonly requestBySeq = new Map<number, number>();
  private nextId = 0;

  constructor(plan: CatalogPlan, options: GroupSubscriptionsOptions = {}) {
    this.plan = plan;
    this.command = plan.commandByName(BuiltInCommand.SubscribeRequest);
    this.onAnswer = options.onAnswer;
  }

  /** Whether the catalog lists `SubscribeRequest`: some explicit set takes a client's requests. */
  get available(): boolean {
    return this.command !== null;
  }

  /** Requests not yet sent. */
  get queuedCount(): number {
    return this.queued.length;
  }

  /** Requests sent and not yet answered. */
  get sentCount(): number {
    return this.sent.size;
  }

  /** Asks for one key of an explicit set, by name or index: settles with the answer. */
  subscribe(set: string | number, key: bigint | number): Promise<SubscribeResult> {
    return this.ask(set, key, SubscribeOp.Subscribe);
  }

  /** Gives back one key of an explicit set, by name or index: settles with the answer. */
  unsubscribe(set: string | number, key: bigint | number): Promise<SubscribeResult> {
    return this.ask(set, key, SubscribeOp.Unsubscribe);
  }

  /**
   * Queues a request and returns its id. Throws when the catalog lists no `SubscribeRequest`, the set is not an
   * explicit one, the key is 0 or the op is neither.
   */
  request(set: number, key: bigint, op: number): number {
    return this.enqueue(set, key, op, null);
  }

  /** Sends the queued requests, oldest first, until the queue or `send` refuses one; returns how many went. */
  pump(target: CommandQueue | TrySend, max = Number.MAX_SAFE_INTEGER): number {
    const send: TrySend =
      typeof target === 'function'
        ? target
        : (command, values) => {
            const seq = target.enqueue(command, values);
            return seq === CommandRefused.RateLimited ? -1 : seq;
          };
    const command = this.command;
    let count = 0;
    while (command !== null && count < max && this.queued.length > 0) {
      const entry = this.queued[0]!;
      const seq = send(command, { requestId: entry.requestId, set: entry.set, key: entry.key, op: entry.op });
      if (seq < 0) {
        break;
      }

      this.queued.shift();
      entry.seq = seq & 0xffff;
      this.sent.set(entry.requestId, entry);
      this.requestBySeq.set(entry.seq, entry.requestId);
      count++;
    }

    return count;
  }

  /** Matches one applied frame's `ACKS`, then its `SOURCES` (a `FrameApplier`'s `acks` and `sources`). */
  observe(frame: { readonly acks: AckList; readonly sources: SourceList }): void {
    const { acks, sources } = frame;
    let requeued = 0;
    for (let i = 0; i < acks.count; i++) {
      const id = this.requestBySeq.get(acks.seq[i]!);
      const entry = id === undefined ? undefined : this.sent.get(id);
      if (id === undefined || entry === undefined) {
        continue;
      }

      this.requestBySeq.delete(acks.seq[i]!);
      this.sent.delete(id);
      const reason = acks.reason[i]!;
      if (reason === AckReason.RateLimited) {
        // Back at the head, ahead of what was never sent, in the order the refused ones went.
        this.queued.splice(requeued++, 0, entry);
        continue;
      }

      this.answer(entry, 'rejected', 0, reason);
    }

    for (let i = 0; i < sources.count; i++) {
      const entry = this.sent.get(sources.requestId[i]!);
      if (entry === undefined) {
        continue;
      }

      this.sent.delete(entry.requestId);
      this.requestBySeq.delete(entry.seq);
      if (sources.status[i] === SourceStatus.Applied) {
        this.answer(entry, 'applied', 0, 0);
      } else {
        this.answer(entry, 'refused', sources.code[i]!, 0);
      }
    }
  }

  /** The session is over: every request still sent (by id), then queued (in order), ends `dropped`. */
  reset(): void {
    const sent = [...this.sent.values()].sort((a, b) => a.requestId - b.requestId);
    const queued = this.queued.splice(0);
    this.sent.clear();
    this.requestBySeq.clear();
    for (const entry of sent) {
      this.answer(entry, 'dropped', 0, 0);
    }

    for (const entry of queued) {
      this.answer(entry, 'dropped', 0, 0);
    }
  }

  private ask(set: string | number, key: bigint | number, op: number): Promise<SubscribeResult> {
    const index = typeof set === 'string' ? this.plan.groupSetIndex(set) : set;
    if (index < 0) {
      return Promise.reject(new RangeError(`the catalog declares no group set named '${String(set)}'`));
    }

    return new Promise<SubscribeResult>((resolve, reject) => {
      try {
        this.enqueue(index, BigInt(key), op, resolve);
      } catch (error) {
        reject(error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  private enqueue(set: number, key: bigint, op: number, settle: ((result: SubscribeResult) => void) | null): number {
    if (this.command === null) {
      throw new Error('the catalog lists no SubscribeRequest: no explicit group set takes a client’s requests');
    }

    if (
      !Number.isInteger(set) ||
      set < 0 ||
      set >= this.plan.groupSets.length ||
      this.plan.groupSets[set]!.selector !== 'explicit'
    ) {
      throw new RangeError(`group set ${set} is not an explicit set of this catalog`);
    }

    if (key === 0n) {
      throw new RangeError('key 0 names no group');
    }

    if (op !== SubscribeOp.Subscribe && op !== SubscribeOp.Unsubscribe) {
      throw new RangeError(`op ${op} is neither Subscribe nor Unsubscribe`);
    }

    const entry: Entry = { requestId: this.takeId(), set, key, op, seq: 0, settle };
    this.queued.push(entry);
    return entry.requestId;
  }

  private answer(entry: Entry, outcome: SubscribeOutcome, code: number, ackReason: number): void {
    const result: SubscribeResult = {
      requestId: entry.requestId,
      set: entry.set,
      key: entry.key,
      op: entry.op,
      outcome,
      code,
      ackReason,
    };
    this.onAnswer?.(result);
    entry.settle?.(result);
  }

  // 1 to 65 535, never one still waiting for its answer.
  private takeId(): number {
    if (this.queued.length + this.sent.size >= 0xffff) {
      throw new Error('65 535 requests are waiting for their answers');
    }

    for (;;) {
      this.nextId = this.nextId === 0xffff ? 1 : this.nextId + 1;
      const id = this.nextId;
      if (!this.sent.has(id) && !this.queued.some((e) => e.requestId === id)) {
        return id;
      }
    }
  }
}
