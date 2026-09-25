import { CloseCode } from '../protocol/constants.js';
import {
  Connection,
  ConnectionState,
  type CatalogCache,
  type ConnectionClose,
  type ConnectionHandlers,
  type ConnectionOptions,
  type SessionInfo,
} from './connection.js';
import { systemTimers, type TimerApi, type TimerHandle } from './socket.js';

/** What a close code says about reconnecting (§ 3, 05 § 1). */
export const ReconnectRule = {
  /** The failure is the client's or the session's own: reconnecting would repeat it. */
  Never: 'never',
  /** Transient: reconnect after a backoff. */
  Reconnect: 'reconnect',
  /** A clean close or an application code (4100–4999): the application decides. */
  Application: 'application',
} as const;
export type ReconnectRule = (typeof ReconnectRule)[keyof typeof ReconnectRule];

/** The rule for a close code (§ 10). The engine-reserved range 4005–4099 is fatal until a rule gives it a meaning. */
export function reconnectRule(code: number): ReconnectRule {
  switch (code) {
    case CloseCode.GoingAway:
    case CloseCode.InternalError:
    case CloseCode.TryAgainLater:
    case CloseCode.PolicyViolation:
    case CloseCode.NoAcknowledgement:
    case CloseCode.HelloTimeout:
      return ReconnectRule.Reconnect;
    case CloseCode.ProtocolError:
    case CloseCode.MalformedPayload:
    case CloseCode.MessageTooBig:
    case CloseCode.AuthenticationRejected:
      return ReconnectRule.Never;
    case CloseCode.ClientRefusedTheStream:
      // This client's own code, which no server sends: the stream was refused, and it would be refused again.
      return ReconnectRule.Never;
    case CloseCode.Normal:
      return ReconnectRule.Application;
    default:
      if (code >= CloseCode.FirstApplicationCode && code <= CloseCode.LastApplicationCode) {
        return ReconnectRule.Application;
      }

      // The engine-reserved range is fatal until a rule says otherwise; anything else — a socket that died without a
      // close frame (1006), a transport failure — is transient.
      return code > CloseCode.ClientRefusedTheStream && code < CloseCode.FirstApplicationCode
        ? ReconnectRule.Never
        : ReconnectRule.Reconnect;
  }
}

export interface BackoffOptions {
  /** The first wait, doubled per attempt. Default 500 ms. */
  readonly initialMs?: number;
  /** The longest wait. Default 30 s. */
  readonly maxMs?: number;
  readonly factor?: number;
  /** Fraction of the wait that is random, so a restarted server is not hit by every client at once. Default 0.5. */
  readonly jitter?: number;
  readonly random?: () => number;
}

/** Exponential backoff with jitter: `delay = min(max, initial · factor^attempt) · (1 − jitter · random)`. */
export class Backoff {
  private readonly initialMs: number;
  private readonly maxMs: number;
  private readonly factor: number;
  private readonly jitter: number;
  private readonly random: () => number;
  private attempts = 0;

  constructor(options: BackoffOptions = {}) {
    this.initialMs = options.initialMs ?? 500;
    this.maxMs = options.maxMs ?? 30_000;
    this.factor = options.factor ?? 2;
    this.jitter = options.jitter ?? 0.5;
    this.random = options.random ?? Math.random;
  }

  /** Attempts taken since the last {@link reset}. */
  get attempt(): number {
    return this.attempts;
  }

  /** The next wait in milliseconds, and counts the attempt. */
  next(): number {
    const full = Math.min(this.maxMs, this.initialMs * this.factor ** this.attempts);
    this.attempts++;
    return full * (1 - this.jitter * this.random());
  }

  reset(): void {
    this.attempts = 0;
  }
}

export interface ReconnectingClientOptions extends Omit<ConnectionOptions, 'resumeToken'> {
  readonly backoff?: BackoffOptions;
  /** Attempts before giving up, counted from the last open session. Default: unlimited. */
  readonly maxAttempts?: number;
  /**
   * Decides for a clean close and for application codes (4100–4999), which the engine leaves to the application.
   * Default: stay closed.
   */
  readonly shouldReconnect?: (close: ConnectionClose) => boolean;
  /** Called before each attempt, with the attempt number from 1 and the wait that preceded it. */
  readonly onReconnect?: (attempt: number, delayMs: number) => void;
  /** Called when the client gives up: the last close, and why. */
  readonly onGiveUp?: (close: ConnectionClose, rule: ReconnectRule) => void;
}

/**
 * Keeps a session up: one {@link Connection} at a time, reopened after a close the rule says is transient, with
 * exponential backoff and jitter.
 *
 * - **Resume.** A close that leaves a resume token within its grace (`limits.resumeGraceMs`) makes the next `HELLO`
 *   present it, so the server re-binds what the old session controlled (01 § 3). The resumed session's first frame
 *   carries `RESET`: the store drops what it held and refills, which is what {@link SessionInfo.resumed} announces.
 * - **Catalog.** The catalog of the session that ended is offered by hash on the next `HELLO`, so `WELCOME` skips it.
 * - **The backoff resets** when a session opens, not when a socket does: a server that accepts and immediately kicks
 *   must still be backed off.
 */
export class ReconnectingClient {
  private readonly options: ReconnectingClientOptions;
  private readonly handlers: ConnectionHandlers;
  private readonly timers: TimerApi;
  private readonly now: () => number;
  private readonly backoff: Backoff;

  private current: Connection | null = null;
  private timer: TimerHandle = null;
  private cache: CatalogCache | null;
  private resumeToken: Uint8Array | null = null;
  private resumeDeadlineMs = 0;
  private running = false;
  private opened = 0;

  constructor(options: ReconnectingClientOptions) {
    this.options = options;
    this.handlers = options.handlers ?? {};
    this.timers = options.timers ?? systemTimers;
    this.now = options.now ?? Date.now;
    this.backoff = new Backoff(options.backoff);
    this.cache = options.catalogCache ?? null;
  }

  /** The connection in flight, or `null` between attempts. */
  get connection(): Connection | null {
    return this.current;
  }

  get sessionInfo(): SessionInfo | null {
    return this.current?.sessionInfo ?? null;
  }

  /** Sessions opened since {@link start}. */
  get sessionsOpened(): number {
    return this.opened;
  }

  /** Attempts since the last session opened. */
  get attempt(): number {
    return this.backoff.attempt;
  }

  get isRunning(): boolean {
    return this.running;
  }

  /** Opens the first connection; every later one follows a close. */
  start(): void {
    if (this.running) {
      return;
    }

    this.running = true;
    this.backoff.reset();
    this.open();
  }

  /** Stops reconnecting and closes the connection in flight with `BYE`. */
  stop(code = CloseCode.Normal, reason = ''): void {
    this.running = false;
    if (this.timer !== null) {
      this.timers.clearTimeout(this.timer);
      this.timer = null;
    }

    const connection = this.current;
    this.current = null;
    if (connection !== null && connection.state !== ConnectionState.Closed) {
      connection.close(code, reason);
    }
  }

  private open(): void {
    const resume = this.resumeToken !== null && this.now() < this.resumeDeadlineMs ? this.resumeToken : null;
    const connection = new Connection({
      ...this.options,
      catalogCache: this.cache,
      resumeToken: resume,
      handlers: {
        ...this.handlers,
        onWelcome: (session, c) => {
          this.opened++;
          this.backoff.reset();
          this.handlers.onWelcome?.(session, c);
        },
        onClose: (close) => {
          this.onClose(close);
        },
      },
    });

    this.current = connection;
    connection.connect();
  }

  private onClose(close: ConnectionClose): void {
    const connection = this.current;
    this.current = null;
    this.cache = connection?.catalogCache ?? this.cache;
    this.resumeToken = close.resumeToken;
    this.resumeDeadlineMs = close.resumeDeadlineMs;
    this.handlers.onClose?.(close);
    if (!this.running) {
      return;
    }

    const rule = reconnectRule(close.code);
    const again =
      rule === ReconnectRule.Reconnect ||
      (rule === ReconnectRule.Application && (this.options.shouldReconnect?.(close) ?? false));
    const maxAttempts = this.options.maxAttempts;
    if (!again || (maxAttempts !== undefined && this.backoff.attempt >= maxAttempts)) {
      this.running = false;
      this.options.onGiveUp?.(close, rule);
      return;
    }

    const delayMs = this.backoff.next();
    this.options.onReconnect?.(this.backoff.attempt, delayMs);
    this.timer = this.timers.setTimeout(() => {
      this.timer = null;
      if (this.running) {
        this.open();
      }
    }, delayMs);
  }
}
