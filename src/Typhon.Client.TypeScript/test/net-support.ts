import {
  CatalogPlan,
  parseCatalog,
  SocketState,
  WireWriter,
  writeKick,
  writePong,
  writeTickHeader,
  writeWelcome,
  type Catalog,
  type SocketCloseEvent,
  type SocketMessageEvent,
  type TimerApi,
  type TimerHandle,
  type WebSocketLike,
} from '../src/index.js';
import { goldenBin } from './golden-support.js';

/*
 * The mock link the net tests run against: a socket whose every event a test fires by hand, and timers a test advances.
 * There is no server yet — the .NET side is being built in parallel — so a test is the peer.
 */

/** A `WebSocket` a test drives: nothing happens until the test makes it happen. */
export class FakeSocket implements WebSocketLike {
  static opened: FakeSocket[] = [];

  binaryType = '';
  protocol = '';
  readyState: number = SocketState.Connecting;
  onopen: (() => void) | null = null;
  onmessage: ((event: SocketMessageEvent) => void) | null = null;
  onclose: ((event: SocketCloseEvent) => void) | null = null;
  onerror: ((event: unknown) => void) | null = null;

  readonly url: string;
  readonly protocols: readonly string[];
  /** Everything the client sent, in order. */
  readonly sent: Uint8Array[] = [];
  /** The close the client asked for, or `null`. */
  closedWith: { code?: number; reason?: string } | null = null;

  constructor(url: string, protocols: readonly string[]) {
    this.url = url;
    this.protocols = protocols;
    FakeSocket.opened.push(this);
  }

  /** The newest socket a connection opened. */
  static get latest(): FakeSocket {
    const socket = FakeSocket.opened[FakeSocket.opened.length - 1];
    if (socket === undefined) {
      throw new Error('no socket was opened');
    }

    return socket;
  }

  static reset(): void {
    FakeSocket.opened = [];
  }

  send(data: Uint8Array): void {
    this.sent.push(data.slice());
  }

  close(code?: number, reason?: string): void {
    this.closedWith = { ...(code === undefined ? {} : { code }), ...(reason === undefined ? {} : { reason }) };
    this.readyState = SocketState.Closed;
  }

  /** The server accepted the upgrade. */
  open(protocol = 'typhon.2'): void {
    this.protocol = protocol;
    this.readyState = SocketState.Open;
    this.onopen?.();
  }

  /** The server sent a binary message. */
  deliver(message: Uint8Array): void {
    const copy = message.slice();
    this.onmessage?.({ data: copy.buffer });
  }

  /** Anything else on the wire: a text frame, a blob. */
  deliverRaw(data: unknown): void {
    this.onmessage?.({ data });
  }

  /** The socket closed from the other end, or after a `KICK`. */
  serverClose(code: number, reason = '', wasClean = true): void {
    this.readyState = SocketState.Closed;
    this.onclose?.({ code, reason, wasClean });
  }
}

interface Scheduled {
  readonly at: number;
  readonly handler: () => void;
  cancelled: boolean;
}

/** Timers a test advances by hand, so a 5 s deadline costs no wall time. */
export class FakeTimers implements TimerApi {
  private readonly scheduled: Scheduled[] = [];
  private nowMs = 0;

  get now(): number {
    return this.nowMs;
  }

  /** Pending timers. */
  get pending(): number {
    return this.scheduled.filter((s) => !s.cancelled).length;
  }

  setTimeout(handler: () => void, ms: number): TimerHandle {
    const entry: Scheduled = { at: this.nowMs + ms, handler, cancelled: false };
    this.scheduled.push(entry);
    return entry;
  }

  clearTimeout(handle: TimerHandle): void {
    (handle as Scheduled).cancelled = true;
  }

  /** Runs every timer due within `ms`, in order, advancing the clock as it goes. */
  advance(ms: number): void {
    const until = this.nowMs + ms;
    for (;;) {
      const due = this.scheduled
        .filter((s) => !s.cancelled && s.at <= until)
        .sort((a, b) => a.at - b.at)
        .shift();
      if (due === undefined) {
        break;
      }

      due.cancelled = true;
      this.nowMs = Math.max(this.nowMs, due.at);
      due.handler();
    }

    this.nowMs = until;
  }
}

/** The kitchen-sink catalog, as a server would send it. */
export function catalogBytes(): Uint8Array {
  return goldenBin('catalog-kitchen-sink');
}

export function catalogPlan(): CatalogPlan {
  return CatalogPlan.compile(catalog());
}

export function catalog(): Catalog {
  return parseCatalog(catalogBytes());
}

/** A hash the server invents; the client echoes it and never computes one. */
export const CATALOG_HASH = Uint8Array.of(1, 2, 3, 4, 5, 6, 7, 8);

export interface WelcomeParts {
  readonly sessionId?: number;
  readonly tick?: number;
  readonly tickPeriodUs?: number;
  readonly capsGranted?: number;
  readonly resumeToken?: Uint8Array;
  readonly catalogHash?: Uint8Array;
  /** Empty for a catalog skip. */
  readonly catalogJson?: Uint8Array;
  readonly major?: number;
}

export function welcomeMessage(parts: WelcomeParts = {}): Uint8Array {
  const w = new WireWriter(1 << 16);
  writeWelcome(w, {
    major: parts.major ?? 2,
    minor: 0,
    capsGranted: parts.capsGranted ?? 0,
    sessionId: parts.sessionId ?? 7,
    resumeToken: parts.resumeToken ?? new Uint8Array(16),
    tick: parts.tick ?? 100,
    tickPeriodUs: parts.tickPeriodUs ?? 50_000,
    catalogHash: parts.catalogHash ?? CATALOG_HASH,
    catalogJson: parts.catalogJson ?? catalogBytes(),
  });
  return w.toBytes();
}

/** A resume token that is not all zeros. */
export function resumeToken(seed = 9): Uint8Array {
  return Uint8Array.from({ length: 16 }, (_, i) => (seed + i) & 0xff);
}

export function tickMessage(tick: number, flags = 0): Uint8Array {
  const w = new WireWriter(64);
  writeTickHeader(w, tick, flags, 0);
  return w.toBytes();
}

export function pongMessage(clientMs: number, tick = 1, usIntoTick = 0): Uint8Array {
  const w = new WireWriter(32);
  writePong(w, { clientMs, tick, usIntoTick });
  return w.toBytes();
}

export function kickMessage(code: number, reason = ''): Uint8Array {
  const w = new WireWriter(256);
  writeKick(w, { code, reason });
  return w.toBytes();
}
