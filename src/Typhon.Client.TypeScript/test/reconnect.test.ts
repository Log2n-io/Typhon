import { beforeEach, describe, expect, it } from 'vitest';
import {
  Backoff,
  CloseCode,
  parseHello,
  ReconnectingClient,
  reconnectRule,
  ReconnectRule,
  type ConnectionClose,
  type SessionInfo,
} from '../src/index.js';
import { CATALOG_HASH, FakeSocket, FakeTimers, kickMessage, resumeToken, welcomeMessage } from './net-support.js';

/*
 * Which close codes come back, and how (§ 3, 05 § 1): 1001, 1011, 1013, 4001 and 4002 reconnect with backoff; 1002,
 * 1007, 1009 and 4003 do not; 1000 and application codes are the application's call. A resume presents its token, and
 * the catalog is offered by hash so WELCOME can skip it.
 */

describe('reconnectRule', () => {
  it('follows the close-code table', () => {
    for (const code of [1001, 1011, 1013, 4001, 4002]) {
      expect(reconnectRule(code), `${code}`).toBe(ReconnectRule.Reconnect);
    }

    for (const code of [1002, 1007, 1009, 4003]) {
      expect(reconnectRule(code), `${code}`).toBe(ReconnectRule.Never);
    }

    expect(reconnectRule(CloseCode.Normal)).toBe(ReconnectRule.Application);
    expect(reconnectRule(4100)).toBe(ReconnectRule.Application);
    expect(reconnectRule(4999)).toBe(ReconnectRule.Application);
    // A socket that died without a close frame: transient.
    expect(reconnectRule(1006)).toBe(ReconnectRule.Reconnect);
    // This client's own refusal code, and the engine-reserved range: fatal until a rule gives them a meaning.
    expect(reconnectRule(CloseCode.ClientRefusedTheStream)).toBe(ReconnectRule.Never);
    expect(reconnectRule(4050)).toBe(ReconnectRule.Never);
    expect(reconnectRule(4099)).toBe(ReconnectRule.Never);
  });
});

describe('Backoff', () => {
  it('doubles up to its ceiling, keeps a jitter band and resets', () => {
    const backoff = new Backoff({ initialMs: 100, maxMs: 800, factor: 2, jitter: 0.5, random: () => 1 });
    // The full wait, minus the whole jitter band.
    expect([backoff.next(), backoff.next(), backoff.next(), backoff.next(), backoff.next()]).toEqual([
      50, 100, 200, 400, 400,
    ]);
    expect(backoff.attempt).toBe(5);

    backoff.reset();
    expect(backoff.attempt).toBe(0);
    expect(backoff.next()).toBe(50);

    const nojitter = new Backoff({ initialMs: 100, jitter: 0, random: () => 0.5 });
    expect(nojitter.next()).toBe(100);
  });
});

interface Harness {
  readonly client: ReconnectingClient;
  readonly timers: FakeTimers;
  readonly closes: ConnectionClose[];
  readonly sessions: SessionInfo[];
  readonly attempts: { attempt: number; delayMs: number }[];
  readonly gaveUp: { code: number; rule: ReconnectRule }[];
}

function harness(options: Partial<ConstructorParameters<typeof ReconnectingClient>[0]> = {}): Harness {
  const timers = new FakeTimers();
  const closes: ConnectionClose[] = [];
  const sessions: SessionInfo[] = [];
  const attempts: { attempt: number; delayMs: number }[] = [];
  const gaveUp: { code: number; rule: ReconnectRule }[] = [];
  const client = new ReconnectingClient({
    url: 'wss://example.test/typhon',
    socket: (url, protocols) => new FakeSocket(url, protocols),
    timers,
    now: () => timers.now,
    backoff: { initialMs: 100, jitter: 0, random: () => 0 },
    onReconnect: (attempt, delayMs) => attempts.push({ attempt, delayMs }),
    onGiveUp: (close, rule) => gaveUp.push({ code: close.code, rule }),
    ...options,
    handlers: {
      onWelcome: (session) => sessions.push(session),
      onClose: (close) => closes.push(close),
      ...options.handlers,
    },
  });
  return { client, timers, closes, sessions, attempts, gaveUp };
}

/** Opens the socket and answers HELLO. */
function accept(welcome = welcomeMessage()): void {
  FakeSocket.latest.open();
  FakeSocket.latest.deliver(welcome);
}

describe('ReconnectingClient', () => {
  beforeEach(() => {
    FakeSocket.reset();
  });

  it('reconnects after a transient close, backing off, and offers the catalog it already holds', () => {
    const h = harness();
    h.client.start();
    accept();
    expect(h.sessions).toHaveLength(1);

    FakeSocket.latest.serverClose(CloseCode.GoingAway, 'restart');
    expect(h.attempts).toEqual([{ attempt: 1, delayMs: 100 }]);
    expect(FakeSocket.opened).toHaveLength(1);

    h.timers.advance(100);
    expect(FakeSocket.opened).toHaveLength(2);
    FakeSocket.latest.open();
    const hello = parseHello(FakeSocket.latest.sent[0]!);
    expect(Array.from(hello.clientCatalogHash)).toEqual(Array.from(CATALOG_HASH));
    // Resume was disabled in that WELCOME, so nothing is presented.
    expect(Array.from(hello.resumeToken)).toEqual(Array<number>(16).fill(0));

    FakeSocket.latest.deliver(welcomeMessage({ catalogJson: new Uint8Array(0) }));
    expect(h.sessions[1]!.catalogSkipped).toBe(true);
    // A session opened, so the next failure starts from the first wait again.
    expect(h.client.attempt).toBe(0);
    expect(h.client.sessionsOpened).toBe(2);
  });

  it('presents the resume token while its grace lasts, and drops it afterwards', () => {
    const token = resumeToken(5);
    const h = harness();
    h.client.start();
    accept(welcomeMessage({ resumeToken: token }));
    const graceMs = h.sessions[0]!.plan.catalog.limits.resumeGraceMs;

    FakeSocket.latest.serverClose(CloseCode.InternalError, 'flush failed');
    h.timers.advance(100);
    FakeSocket.latest.open();
    expect(Array.from(parseHello(FakeSocket.latest.sent[0]!).resumeToken)).toEqual(Array.from(token));
    FakeSocket.latest.deliver(welcomeMessage({ resumeToken: token }));
    expect(h.sessions[1]!.resumed).toBe(true);

    expect(graceMs).toBe(60_000);
  });

  it('drops a resume token whose grace ran out before the next attempt', () => {
    const token = resumeToken(5);
    // A backoff longer than the catalog's 60 s grace: by the time the client comes back, the token is stale.
    const h = harness({ backoff: { initialMs: 90_000, maxMs: 120_000, jitter: 0, random: () => 0 } });
    h.client.start();
    accept(welcomeMessage({ resumeToken: token }));

    FakeSocket.latest.serverClose(CloseCode.InternalError, '');
    h.timers.advance(90_000);
    FakeSocket.latest.open();
    expect(Array.from(parseHello(FakeSocket.latest.sent[0]!).resumeToken)).toEqual(Array<number>(16).fill(0));
  });

  it('backs off further while a server keeps refusing, and stops at maxAttempts', () => {
    const h = harness({ maxAttempts: 3 });
    h.client.start();
    for (let attempt = 1; attempt <= 3; attempt++) {
      FakeSocket.latest.serverClose(CloseCode.TryAgainLater, 'full');
      expect(h.attempts[attempt - 1]).toEqual({ attempt, delayMs: 100 * 2 ** (attempt - 1) });
      h.timers.advance(100 * 2 ** (attempt - 1));
    }

    FakeSocket.latest.serverClose(CloseCode.TryAgainLater, 'full');
    expect(h.gaveUp).toEqual([{ code: CloseCode.TryAgainLater, rule: ReconnectRule.Reconnect }]);
    expect(h.client.isRunning).toBe(false);
    expect(FakeSocket.opened).toHaveLength(4);
  });

  it('does not come back after a protocol failure, an admission rejection or a clean close', () => {
    for (const code of [CloseCode.ProtocolError, CloseCode.MalformedPayload, CloseCode.MessageTooBig, 4003]) {
      FakeSocket.reset();
      const h = harness();
      h.client.start();
      accept();
      FakeSocket.latest.serverClose(code, '');
      h.timers.advance(60_000);
      expect(FakeSocket.opened, `${code}`).toHaveLength(1);
      expect(h.gaveUp[0]!.rule).toBe(ReconnectRule.Never);
    }

    FakeSocket.reset();
    const clean = harness();
    clean.client.start();
    accept();
    FakeSocket.latest.serverClose(CloseCode.Normal, '');
    clean.timers.advance(60_000);
    expect(FakeSocket.opened).toHaveLength(1);
    expect(clean.gaveUp[0]!.rule).toBe(ReconnectRule.Application);
  });

  it('lets the application decide for its own codes', () => {
    const h = harness({ shouldReconnect: (close) => close.code === 4200 });
    h.client.start();
    accept();
    FakeSocket.latest.deliver(kickMessage(4200, 'maintenance'));
    FakeSocket.latest.serverClose(CloseCode.Normal, '');
    expect(h.closes[0]!.code).toBe(4200);
    h.timers.advance(100);
    expect(FakeSocket.opened).toHaveLength(2);

    FakeSocket.latest.open();
    FakeSocket.latest.deliver(kickMessage(4300, 'gone'));
    FakeSocket.latest.serverClose(CloseCode.Normal, '');
    h.timers.advance(60_000);
    expect(FakeSocket.opened).toHaveLength(2);
  });

  it('stops on demand, sending BYE, and stays stopped', () => {
    const h = harness();
    h.client.start();
    accept();
    h.client.stop(CloseCode.Normal, 'bye');
    expect(FakeSocket.latest.closedWith?.code).toBe(CloseCode.Normal);
    expect(h.client.isRunning).toBe(false);
    h.timers.advance(60_000);
    expect(FakeSocket.opened).toHaveLength(1);
  });

  it('comes back after a HELLO that timed out', () => {
    const h = harness();
    h.client.start();
    FakeSocket.latest.open();
    h.timers.advance(5000);
    expect(h.closes[0]!.code).toBe(CloseCode.HelloTimeout);
    h.timers.advance(100);
    expect(FakeSocket.opened).toHaveLength(2);
  });
});
