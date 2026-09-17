import { CatalogPlan, parseCatalog, type Catalog } from '../protocol/catalog.js';
import { CloseCode, isValidClientCloseCode, MessageType, ProtocolConstants } from '../protocol/constants.js';
import { WireFormatError } from '../protocol/errors.js';
import {
  checkCapsGranted,
  parseKick,
  parsePong,
  parseWelcome,
  truncateUtf8,
  writeBye,
  writeHello,
  writePing,
  type PingMessage,
  type PongMessage,
} from '../protocol/messages.js';
import { WireWriter } from '../protocol/writer.js';
import {
  SocketState,
  systemTimers,
  systemWebSocket,
  type TimerApi,
  type TimerHandle,
  type WebSocketFactory,
  type WebSocketLike,
} from './socket.js';

const RESUME_TOKEN_BYTES = 16;
const CATALOG_HASH_BYTES = 8;
const NO_RESUME_TOKEN = new Uint8Array(RESUME_TOKEN_BYTES);
const NO_CATALOG_HASH = new Uint8Array(CATALOG_HASH_BYTES);
const EMPTY = new Uint8Array(0);

/** Where a connection is in the handshake (`03-wire-protocol.md` § 3). */
export const ConnectionState = {
  Idle: 0,
  /** The socket is opening; nothing has been sent. */
  Connecting: 1,
  /** `HELLO` is out and `WELCOME` is due within {@link ConnectionOptions.helloTimeoutMs}. */
  AwaitingWelcome: 2,
  /** The session is open: `TICK`, `PONG` and `KICK` arrive, `COMMANDS`, `PING` and `BYE` may be sent. */
  Open: 3,
  Closed: 4,
} as const;
export type ConnectionState = (typeof ConnectionState)[keyof typeof ConnectionState];

/**
 * A catalog held from an earlier session, offered to the server by its hash so `WELCOME` may skip it (§ 10). The client
 * **echoes** the hash it was given and never computes one: there is no FNV here, and there must not be.
 */
export interface CatalogCache {
  /** The 8 hash bytes exactly as `WELCOME` sent them. */
  readonly hash: Uint8Array;
  /** The canonical catalog JSON, as it arrived. */
  readonly json: Uint8Array;
  /** The compiled plan, so a skip costs no parse either. */
  readonly plan: CatalogPlan;
}

/** What `WELCOME` opened. */
export interface SessionInfo {
  readonly sessionId: number;
  /** The server's tick when the session opened. */
  readonly tick: number;
  readonly tickPeriodUs: number;
  /** The capabilities granted: a subset of those requested (W23). */
  readonly capsGranted: number;
  /** The token a reconnect may present within `limits.resumeGraceMs`, or `null` when resume is disabled. */
  readonly resumeToken: Uint8Array | null;
  readonly catalogHash: Uint8Array;
  readonly catalogJson: Uint8Array;
  readonly catalog: Catalog;
  readonly plan: CatalogPlan;
  /** Whether `WELCOME` skipped the catalog because the offered hash matched. */
  readonly catalogSkipped: boolean;
  /** Whether this `HELLO` presented a resume token; the session's first frame then carries `RESET`. */
  readonly resumed: boolean;
}

/** Why a connection ended. */
export interface ConnectionClose {
  /**
   * The close code the reconnect policy reads (§ 3). It is the `KICK` code when one arrived, the code this side decided
   * on for a protocol failure, or the socket's close code.
   */
  readonly code: number;
  readonly reason: string;
  readonly wasClean: boolean;
  /** Whether this side ended it. */
  readonly local: boolean;
  /** The resume token of the session that ended, when it may still be resumed. */
  readonly resumeToken: Uint8Array | null;
  /** Local time after which the resume token is stale (`limits.resumeGraceMs`), or 0 when there is none. */
  readonly resumeDeadlineMs: number;
}

export interface ConnectionHandlers {
  /** The session is open: the catalog is compiled and frames follow. */
  onWelcome?(session: SessionInfo, connection: Connection): void;
  /**
   * One `TICK`, in arrival order. Apply it **synchronously**: frames do not commute, and deferring one (a worker
   * round-trip, an async decompression) reorders the stream (05 § 1).
   */
  onTick?(message: Uint8Array, recvMs: number): void;
  onPong?(pong: PongMessage, recvMs: number): void;
  /** Every inbound message, before it is decoded and whatever its type: the recorder's hook. */
  onMessage?(message: Uint8Array, recvMs: number): void;
  /** A `KICK`; its close follows. */
  onKick?(code: number, reason: string): void;
  onClose?(close: ConnectionClose): void;
}

export interface ConnectionOptions {
  readonly url: string;
  /** The application-declared session kind, ≤ 32 UTF-8 bytes; the engine never interprets it (W21). */
  readonly kind?: string;
  /** The opaque admission token, ≤ 8 KiB (W22). */
  readonly token?: string;
  /** The capabilities asked for (W23); the server grants a subset. */
  readonly caps?: number;
  /** Up to 256 bytes handed to the application's admission hook (W19). */
  readonly helloPayload?: Uint8Array;
  /** A catalog held from an earlier session: `HELLO` offers its hash so `WELCOME` can skip the JSON. */
  readonly catalogCache?: CatalogCache | null;
  /** A resume token from an earlier session, presented within its grace (01 § 3). */
  readonly resumeToken?: Uint8Array | null;
  readonly handlers?: ConnectionHandlers;
  /** Test seams: the socket, the timers and the clock. */
  readonly socket?: WebSocketFactory;
  readonly timers?: TimerApi;
  readonly now?: () => number;
  /** How long `WELCOME` may take; past it the connection closes with 4002. Default 5 s (W22). */
  readonly helloTimeoutMs?: number;
}

/**
 * A `typhon.2` WebSocket client: the handshake, the message caps, the close codes, and frames delivered in arrival order
 * for the consumer to apply synchronously.
 *
 * - **Handshake.** `HELLO` goes out as soon as the socket opens and `WELCOME` must answer within
 *   {@link ConnectionOptions.helloTimeoutMs} (5 s), else the connection ends with 4002. `WELCOME` carries the catalog,
 *   or skips it when the hash `HELLO` offered matched — the client then compiles the catalog it already held.
 * - **Caps.** Outbound: the `HELLO` limit (16 KiB) until the session opens, then `limits.clientMessageBytes`. Inbound:
 *   `WELCOME` is bounded by the protocol's 1 MiB — it carries the limits, so nothing else can bound it — and every later
 *   message by `limits.frameBytes`. Either way the connection ends with 1009 before the message is decoded (§ 10).
 * - **The ping loop is the consumer's.** `PING` is mandatory at the catalog's `pingHz` and carries the newest applied
 *   tick, which the server's lag skip reads: wire {@link PingScheduler} to {@link sendPing} on `onWelcome`, or the
 *   server closes the session with 4001.
 * - **Close codes.** A malformed message ends it with the code its error carries (1007, or 1002 for a message that is
 *   unknown or out of state). A browser may only put 1000 or 4000–4999 in a close frame (W24), so the client sends
 *   `BYE 4004` — "refused the stream" — closes the socket with 1000, and reports the real code to
 *   {@link ConnectionHandlers.onClose}. The reconnect policy reads the handler's code, never the socket's.
 */
export class Connection {
  private readonly options: ConnectionOptions;
  private readonly handlers: ConnectionHandlers;
  private readonly timers: TimerApi;
  private readonly now: () => number;
  private readonly writer = new WireWriter(ProtocolConstants.helloMaxBytes);

  private socket: WebSocketLike | null = null;
  private currentState: ConnectionState = ConnectionState.Idle;
  private session: SessionInfo | null = null;
  private helloTimer: TimerHandle = null;
  /** The code and reason to report, once decided: a `KICK`'s, or this side's verdict on a malformed message. */
  private verdict: { code: number; reason: string; local: boolean } | null = null;
  private outboundCap = ProtocolConstants.helloMaxBytes;
  /** Until `WELCOME` brings `limits.frameBytes`, the only bound a client has is the protocol's (§ 10). */
  private inboundCap: number = ProtocolConstants.welcomeMaxBytes;
  private cache: CatalogCache | null;

  constructor(options: ConnectionOptions) {
    this.options = options;
    this.handlers = options.handlers ?? {};
    this.timers = options.timers ?? systemTimers;
    this.now = options.now ?? Date.now;
    this.cache = options.catalogCache ?? null;
  }

  get state(): ConnectionState {
    return this.currentState;
  }

  /** The open session, or `null` before `WELCOME` and after the close. */
  get sessionInfo(): SessionInfo | null {
    return this.session;
  }

  /** The catalog to offer on the next connect: the one this session used. */
  get catalogCache(): CatalogCache | null {
    return this.cache;
  }

  /** Opens the socket. `HELLO` follows on its own, as soon as the socket is open. */
  connect(): void {
    if (this.currentState !== ConnectionState.Idle) {
      throw new Error('a connection connects once; build another one to reconnect');
    }

    this.currentState = ConnectionState.Connecting;
    const factory = this.options.socket ?? systemWebSocket;
    const socket = factory(this.options.url, [ProtocolConstants.webSocketSubprotocol]);
    this.socket = socket;
    socket.binaryType = 'arraybuffer';
    socket.onopen = () => {
      this.onOpen();
    };
    socket.onmessage = (event) => {
      this.onMessage(event.data);
    };
    socket.onclose = (event) => {
      this.onSocketClose(event.code, event.reason, event.wasClean);
    };
    socket.onerror = () => {
      // A socket error is always followed by a close; the close carries the outcome.
    };
  }

  /**
   * Sends a client message — `COMMANDS`, `PING` or `BYE`, the only three a client sends after `HELLO` (§ 3). One above
   * the cap in force is refused here rather than by the server, which would close with 1009.
   */
  send(message: Uint8Array): void {
    if (this.currentState !== ConnectionState.Open) {
      throw new Error('the connection is not open');
    }

    this.sendCapped(message);
  }

  private sendCapped(message: Uint8Array): void {
    if (message.length > this.outboundCap) {
      throw new RangeError(`a client message of ${message.length} B is above the ${this.outboundCap} B limit`);
    }

    this.socket?.send(message);
  }

  /** Sends a `PING`. Mandatory at the catalog's `pingHz`: the server's lag skip reads `lastAppliedTick` (02 § 6). */
  sendPing(ping: PingMessage): void {
    this.writer.reset();
    writePing(this.writer, ping);
    this.send(this.writer.written());
  }

  /** A clean leave: `BYE`, then the close. `code` is 1000 or an application code in 4000–4999 (W24). */
  close(code: number = CloseCode.Normal, reason = ''): void {
    if (this.currentState === ConnectionState.Closed || this.currentState === ConnectionState.Idle) {
      return;
    }

    if (this.currentState === ConnectionState.Open) {
      this.writer.reset();
      writeBye(this.writer, code);
      this.socket?.send(this.writer.written());
    }

    this.finish(code, reason, true);
  }

  private onOpen(): void {
    const socket = this.socket;
    if (socket === null) {
      return;
    }

    // A browser refuses the upgrade itself when the server selects no subprotocol; a raw client may not.
    if (socket.protocol !== '' && socket.protocol !== ProtocolConstants.webSocketSubprotocol) {
      this.fail(CloseCode.ProtocolError, `the server selected subprotocol '${socket.protocol}'`);
      return;
    }

    const options = this.options;
    const cache = this.cache;
    this.writer.reset();
    writeHello(this.writer, {
      major: ProtocolConstants.major,
      minor: ProtocolConstants.minor,
      caps: options.caps ?? 0,
      kind: options.kind ?? '',
      token: options.token ?? '',
      resumeToken: options.resumeToken ?? NO_RESUME_TOKEN,
      clientCatalogHash: cache?.hash ?? NO_CATALOG_HASH,
      helloPayload: options.helloPayload ?? EMPTY,
    });
    this.currentState = ConnectionState.AwaitingWelcome;
    this.sendCapped(this.writer.written());

    const timeout = this.options.helloTimeoutMs ?? ProtocolConstants.helloTimeoutMs;
    this.helloTimer = this.timers.setTimeout(() => {
      this.helloTimer = null;
      this.fail(CloseCode.HelloTimeout, `WELCOME did not arrive within ${timeout} ms`);
    }, timeout);
  }

  private onMessage(data: unknown): void {
    if (this.currentState === ConnectionState.Closed) {
      return;
    }

    if (!(data instanceof ArrayBuffer)) {
      this.fail(CloseCode.ProtocolError, 'a text or blob message on a binary protocol');
      return;
    }

    const message = new Uint8Array(data);
    const recvMs = this.now();
    if (message.length > this.inboundCap) {
      this.fail(CloseCode.MessageTooBig, `a frame of ${message.length} B is above the ${this.inboundCap} B limit`);
      return;
    }

    this.handlers.onMessage?.(message, recvMs);
    try {
      this.dispatch(message, recvMs);
    } catch (e) {
      if (e instanceof WireFormatError) {
        this.fail(e.closeCode, e.message);
        return;
      }

      // A catalog this client refuses, or any other decode failure: malformed payload.
      this.fail(CloseCode.MalformedPayload, e instanceof Error ? e.message : String(e));
    }
  }

  private dispatch(message: Uint8Array, recvMs: number): void {
    const type = message[0];
    if (this.currentState === ConnectionState.AwaitingWelcome) {
      if (type !== MessageType.Welcome) {
        this.fail(CloseCode.ProtocolError, `message type 0x${(type ?? 0).toString(16)} before WELCOME`);
        return;
      }

      this.onWelcome(message);
      return;
    }

    switch (type) {
      case MessageType.Tick:
        this.handlers.onTick?.(message, recvMs);
        break;
      case MessageType.Pong:
        this.handlers.onPong?.(parsePong(message), recvMs);
        break;
      case MessageType.Kick: {
        const kick = parseKick(message);
        this.handlers.onKick?.(kick.code, kick.reason);
        // The server closes next; remember its code, which the close frame may not carry.
        this.verdict = { code: kick.code, reason: kick.reason, local: false };
        break;
      }
      default:
        this.fail(CloseCode.ProtocolError, `message type 0x${(type ?? 0).toString(16)} is unknown or out of state`);
        break;
    }
  }

  private onWelcome(message: Uint8Array): void {
    const welcome = parseWelcome(message);
    if (welcome.major !== ProtocolConstants.major) {
      this.fail(CloseCode.ProtocolError, `the server speaks major ${welcome.major}, this client major 2`);
      return;
    }

    checkCapsGranted(this.options.caps ?? 0, welcome.capsGranted);
    const cache = this.cache;
    const skipped = welcome.catalogJson.length === 0;
    if (skipped && cache === null) {
      this.fail(CloseCode.ProtocolError, 'WELCOME skipped the catalog, and this client holds none');
      return;
    }

    const json = skipped ? cache!.json : welcome.catalogJson;
    const plan = skipped ? cache!.plan : CatalogPlan.compile(parseCatalog(json));
    // The hash is the server's, echoed back on the next connect; the client never computes one.
    this.cache = { hash: welcome.catalogHash, json, plan };

    const resumeToken = isZero(welcome.resumeToken) ? null : welcome.resumeToken;
    const session: SessionInfo = {
      sessionId: welcome.sessionId,
      tick: welcome.tick,
      tickPeriodUs: welcome.tickPeriodUs,
      capsGranted: welcome.capsGranted,
      resumeToken,
      catalogHash: welcome.catalogHash,
      catalogJson: json,
      catalog: plan.catalog,
      plan,
      catalogSkipped: skipped,
      resumed: !isZero(this.options.resumeToken ?? NO_RESUME_TOKEN),
    };

    this.clearHelloTimer();
    this.session = session;
    this.outboundCap = plan.catalog.limits.clientMessageBytes;
    this.inboundCap = plan.catalog.limits.frameBytes;
    this.currentState = ConnectionState.Open;
    this.handlers.onWelcome?.(session, this);
  }

  /** This side's verdict on the peer: report `code`, and close with what a close frame accepts (W24). */
  private fail(code: number, reason: string): void {
    if (this.currentState === ConnectionState.Closed) {
      return;
    }

    this.verdict = { code, reason, local: true };
    this.finish(code, reason, true);
  }

  private finish(code: number, reason: string, local: boolean): void {
    const socket = this.socket;
    const open = socket !== null && socket.readyState === SocketState.Open;
    if (open && !isValidClientCloseCode(code)) {
      // 1002, 1007 and 1009 are not codes a browser may close with: say so in a BYE while the socket still carries one.
      this.writer.reset();
      writeBye(this.writer, CloseCode.ClientRefusedTheStream);
      socket.send(this.writer.written());
    }

    this.clearHelloTimer();
    this.currentState = ConnectionState.Closed;
    if (socket !== null && socket.readyState !== SocketState.Closed) {
      const wire = isValidClientCloseCode(code) ? code : CloseCode.Normal;
      socket.close(wire, truncateUtf8(reason, ProtocolConstants.kickReasonMaxBytes));
    }

    this.report(code, reason, local, local);
  }

  private onSocketClose(code: number, reason: string, wasClean: boolean): void {
    if (this.currentState === ConnectionState.Closed) {
      return;
    }

    this.clearHelloTimer();
    this.currentState = ConnectionState.Closed;
    const verdict = this.verdict;
    this.report(verdict?.code ?? code, verdict?.reason ?? reason, verdict?.local ?? false, wasClean);
  }

  private report(code: number, reason: string, local: boolean, wasClean: boolean): void {
    const session = this.session;
    const resumeToken = session?.resumeToken ?? null;
    const graceMs = session?.plan.catalog.limits.resumeGraceMs ?? 0;
    this.session = null;
    this.socket = null;
    this.handlers.onClose?.({
      code,
      reason,
      wasClean,
      local,
      resumeToken,
      resumeDeadlineMs: resumeToken === null ? 0 : this.now() + graceMs,
    });
  }

  private clearHelloTimer(): void {
    if (this.helloTimer !== null) {
      this.timers.clearTimeout(this.helloTimer);
      this.helloTimer = null;
    }
  }
}

function isZero(bytes: Uint8Array): boolean {
  for (let i = 0; i < bytes.length; i++) {
    if (bytes[i] !== 0) {
      return false;
    }
  }

  return true;
}
