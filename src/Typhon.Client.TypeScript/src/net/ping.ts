import { monotonicNow } from '../clock/now.js';
import type { PingMessage, PongMessage } from '../protocol/messages.js';
import { systemTimers, type TimerApi, type TimerHandle } from './socket.js';

export interface PingOptions {
  /** Pings per second, from the catalog's `tick.pingHz` (4 by default). */
  readonly pingHz: number;
  readonly send: (ping: PingMessage) => void;
  /** The newest tick the consumer has applied. The server's lag skip reads it (02 § 6), so it must be the truth. */
  readonly lastAppliedTick: () => number;
  readonly now?: () => number;
  readonly timers?: TimerApi;
  /** Weight of a new sample in the smoothed round trip, default 1/8 — TCP's estimator (RFC 6298). */
  readonly rttWeight?: number;
}

/**
 * The mandatory `PING` loop (§ 3): one every `1 / pingHz` seconds once the session is open, carrying the newest applied
 * tick, and an RTT estimate from the `PONG` that answers.
 *
 * **Not optional.** A session that stops acknowledging is closed with 4001, and the server's lag skip uses
 * `lastAppliedTick` to decide whether a session is keeping up (02 § 6): a client that lies, or never pings, is skipped
 * or closed.
 *
 * `clientMs` is the local clock truncated to 32 bits; the round trip is the difference in the same arithmetic, so a wrap
 * every 49 days costs nothing.
 */
export class PingScheduler {
  private readonly options: PingOptions;
  private readonly timers: TimerApi;
  private readonly now: () => number;
  private readonly intervalMs: number;
  private readonly weight: number;

  private timer: TimerHandle = null;
  private smoothedRtt = 0;
  private latestRtt = 0;
  private sent = 0;
  private received = 0;
  private lastPongTick = -1;

  constructor(options: PingOptions) {
    if (!(options.pingHz > 0)) {
      throw new RangeError(`pingHz must be positive, got ${options.pingHz}`);
    }

    this.options = options;
    this.timers = options.timers ?? systemTimers;
    this.now = options.now ?? monotonicNow;
    this.intervalMs = 1000 / options.pingHz;
    this.weight = options.rttWeight ?? 0.125;
  }

  /** The smoothed round trip in milliseconds, 0 before the first `PONG`. */
  get rttMs(): number {
    return this.smoothedRtt;
  }

  /** The latest round trip in milliseconds, 0 before the first `PONG`. */
  get lastRttMs(): number {
    return this.latestRtt;
  }

  get pingsSent(): number {
    return this.sent;
  }

  get pongsReceived(): number {
    return this.received;
  }

  /** Pings sent that no `PONG` has answered yet. */
  get outstanding(): number {
    return this.sent - this.received;
  }

  /** The server tick of the latest `PONG`, or −1. */
  get serverTick(): number {
    return this.lastPongTick;
  }

  /** Starts the loop, pinging at once so the server hears from the client before the first frame. */
  start(): void {
    if (this.timer !== null) {
      return;
    }

    this.pingNow();
    this.schedule();
  }

  stop(): void {
    if (this.timer !== null) {
      this.timers.clearTimeout(this.timer);
      this.timer = null;
    }
  }

  /**
   * Pings the transport refused, cumulative.
   *
   * Non-zero is not an error by itself — a ping landing in the same millisecond as a close is ordinary — but a number
   * that keeps climbing means the socket has been unusable for a while and nothing has noticed.
   */
  failed = 0;

  /** Sends one `PING` immediately, off schedule. */
  pingNow(): void {
    // Counted AFTER the send, so a refused send does not leave `outstanding` permanently one too high.
    this.options.send({ clientMs: this.now() >>> 0, lastAppliedTick: this.options.lastAppliedTick() >>> 0 });
    this.sent++;
  }

  /** Records the answer: its `clientMs` is the one this client sent, so the round trip needs no state per ping. */
  onPong(pong: PongMessage, recvMs: number = this.now()): void {
    this.received++;
    this.lastPongTick = pong.tick;
    const rtt = ((recvMs >>> 0) - pong.clientMs) >>> 0;
    this.latestRtt = rtt;
    this.smoothedRtt = this.smoothedRtt === 0 ? rtt : this.smoothedRtt + this.weight * (rtt - this.smoothedRtt);
  }

  private schedule(): void {
    this.timer = this.timers.setTimeout(() => {
      this.timer = null;
      // Re-armed BEFORE the send, and the send guarded. `Connection.send` throws for a socket that is not open, and any
      // ping racing a close hits exactly that — so with the order the other way round the loop died on its first such
      // throw and never rearmed. The server then saw a client that had stopped answering and closed it with 4001, which
      // reads as a network fault and is not one. A ping that could not go out is a ping skipped, not the end of pinging.
      this.schedule();
      try {
        this.pingNow();
      } catch {
        this.failed++;
      }
    }, this.intervalMs);
  }
}
