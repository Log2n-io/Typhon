import { beforeEach, describe, expect, it } from 'vitest';
import {
  Capabilities,
  CloseCode,
  Connection,
  ConnectionState,
  MessageType,
  parseBye,
  parseHello,
  ProtocolConstants,
  WireWriter,
  writeTickHeader,
  type ConnectionClose,
  type ConnectionHandlers,
  type ConnectionOptions,
  type SessionInfo,
} from '../src/index.js';
import {
  catalogBytes,
  CATALOG_HASH,
  FakeSocket,
  FakeTimers,
  kickMessage,
  pongMessage,
  resumeToken,
  tickMessage,
  welcomeMessage,
} from './net-support.js';

/*
 * The handshake, the caps, the close codes: `03-wire-protocol.md` §§ 3 and 10. The peer is the test — the server side is
 * being built in parallel — so every event a socket can raise is raised by hand.
 */

interface Harness {
  readonly connection: Connection;
  readonly timers: FakeTimers;
  readonly socket: () => FakeSocket;
  readonly sessions: SessionInfo[];
  readonly ticks: Uint8Array[];
  readonly closes: ConnectionClose[];
  readonly kicks: { code: number; reason: string }[];
}

function harness(options: Partial<ConnectionOptions> = {}): Harness {
  const timers = new FakeTimers();
  const sessions: SessionInfo[] = [];
  const ticks: Uint8Array[] = [];
  const closes: ConnectionClose[] = [];
  const kicks: { code: number; reason: string }[] = [];
  const handlers: ConnectionHandlers = {
    onWelcome: (session) => sessions.push(session),
    onTick: (message) => ticks.push(message.slice()),
    onKick: (code, reason) => kicks.push({ code, reason }),
    onClose: (close) => closes.push(close),
  };
  const connection = new Connection({
    url: 'wss://example.test/typhon',
    kind: 'player',
    token: 'opaque',
    caps: Capabilities.Stats,
    socket: (url, protocols) => new FakeSocket(url, protocols),
    timers,
    now: () => timers.now,
    ...options,
    // The harness always records; a case adds its own handlers on top.
    handlers: { ...handlers, ...options.handlers },
  });
  return { connection, timers, socket: () => FakeSocket.latest, sessions, ticks, closes, kicks };
}

/** Opens the socket, answers HELLO with WELCOME and returns the harness. */
function opened(options: Partial<ConnectionOptions> = {}, welcome = welcomeMessage()): Harness {
  const h = harness(options);
  h.connection.connect();
  h.socket().open();
  h.socket().deliver(welcome);
  return h;
}

describe('Connection handshake', () => {
  beforeEach(() => {
    FakeSocket.reset();
  });

  it('offers the typhon.3 subprotocol, asks for arraybuffers and sends HELLO as soon as the socket opens', () => {
    const h = harness();
    h.connection.connect();
    expect(h.socket().protocols).toEqual([ProtocolConstants.webSocketSubprotocol]);
    expect(h.socket().binaryType).toBe('arraybuffer');
    expect(h.socket().sent).toHaveLength(0);

    h.socket().open();
    expect(h.connection.state).toBe(ConnectionState.AwaitingWelcome);
    const hello = parseHello(h.socket().sent[0]!);
    expect([hello.major, hello.minor, hello.kind, hello.token, hello.caps]).toEqual([3, 0, 'player', 'opaque', 1]);
    expect(Array.from(hello.resumeToken)).toEqual(Array<number>(16).fill(0));
    expect(Array.from(hello.clientCatalogHash)).toEqual(Array<number>(8).fill(0));
  });

  it('opens the session on WELCOME, compiling its catalog and taking its limits', () => {
    const h = opened();
    expect(h.connection.state).toBe(ConnectionState.Open);
    const session = h.sessions[0]!;
    expect([session.sessionId, session.tick, session.tickPeriodUs]).toEqual([7, 100, 50_000]);
    expect(session.catalogSkipped).toBe(false);
    expect(session.resumed).toBe(false);
    expect(session.plan.archetypeByName('Drone')).not.toBeNull();
    expect(Array.from(session.catalogHash)).toEqual(Array.from(CATALOG_HASH));
    expect(session.resumeToken).toBeNull();

    // The client message cap is the catalog's from now on.
    const cap = session.plan.catalog.limits.clientMessageBytes;
    expect(() => {
      h.connection.send(new Uint8Array(cap + 1));
    }).toThrow(/limit/);
  });

  it('echoes the catalog hash on the next connect and accepts a WELCOME that skips the catalog', () => {
    const first = opened();
    const cache = first.connection.catalogCache!;
    expect(Array.from(cache.hash)).toEqual(Array.from(CATALOG_HASH));

    const second = harness({ catalogCache: cache });
    second.connection.connect();
    second.socket().open();
    const hello = parseHello(second.socket().sent[0]!);
    expect(Array.from(hello.clientCatalogHash)).toEqual(Array.from(CATALOG_HASH));

    second.socket().deliver(welcomeMessage({ catalogJson: new Uint8Array(0) }));
    const session = second.sessions[0]!;
    expect(session.catalogSkipped).toBe(true);
    expect(session.plan).toBe(cache.plan);
    expect(session.catalogJson).toBe(cache.json);
  });

  it('presents a resume token, and says the session was resumed', () => {
    const token = resumeToken();
    const h = opened({ resumeToken: token });
    expect(Array.from(parseHello(h.socket().sent[0]!).resumeToken)).toEqual(Array.from(token));
    expect(h.sessions[0]!.resumed).toBe(true);
  });

  it('reports the resume token and its deadline when the session ends', () => {
    const token = resumeToken(3);
    const h = opened({}, welcomeMessage({ resumeToken: token }));
    expect(Array.from(h.sessions[0]!.resumeToken!)).toEqual(Array.from(token));

    h.timers.advance(1000);
    h.socket().serverClose(CloseCode.GoingAway, 'restart');
    const close = h.closes[0]!;
    expect([close.code, close.local]).toEqual([CloseCode.GoingAway, false]);
    expect(Array.from(close.resumeToken!)).toEqual(Array.from(token));
    // WELCOME's catalog carries limits.resumeGraceMs.
    expect(close.resumeDeadlineMs).toBe(1000 + h.sessions[0]!.plan.catalog.limits.resumeGraceMs);
  });

  it('closes with 4002 when WELCOME does not arrive within the deadline', () => {
    const h = harness();
    h.connection.connect();
    h.socket().open();
    h.timers.advance(ProtocolConstants.helloTimeoutMs - 1);
    expect(h.closes).toHaveLength(0);

    h.timers.advance(2);
    expect(h.closes[0]!.code).toBe(CloseCode.HelloTimeout);
    expect(h.closes[0]!.local).toBe(true);
    // 4002 is a code a browser's close frame accepts.
    expect(h.socket().closedWith?.code).toBe(CloseCode.HelloTimeout);
  });

  it('cancels the deadline once WELCOME arrives', () => {
    const h = opened();
    h.timers.advance(ProtocolConstants.helloTimeoutMs * 2);
    expect(h.closes).toHaveLength(0);
    expect(h.connection.state).toBe(ConnectionState.Open);
  });
});

describe('Connection frames', () => {
  beforeEach(() => {
    FakeSocket.reset();
  });

  it('hands TICK messages over in arrival order, with the time they arrived', () => {
    const recv: number[] = [];
    const h = harness();
    const seen: number[] = [];
    const connection = new Connection({
      url: 'wss://example.test/typhon',
      handlers: {
        onTick: (message, recvMs) => {
          seen.push(message[1]!);
          recv.push(recvMs);
        },
      },
      socket: (url, protocols) => new FakeSocket(url, protocols),
      timers: h.timers,
      now: () => h.timers.now,
    });
    connection.connect();
    FakeSocket.latest.open();
    FakeSocket.latest.deliver(welcomeMessage());
    for (let tick = 1; tick <= 3; tick++) {
      h.timers.advance(50);
      FakeSocket.latest.deliver(tickMessage(tick));
    }

    expect(seen).toEqual([1, 2, 3]);
    expect(recv).toEqual([50, 100, 150]);
  });

  it('reports every inbound message to the recorder hook, whatever its type', () => {
    const messages: number[] = [];
    const h = harness({ handlers: { onMessage: (message) => messages.push(message[0]!) } });
    h.connection.connect();
    h.socket().open();
    h.socket().deliver(welcomeMessage());
    h.socket().deliver(tickMessage(1));
    h.socket().deliver(pongMessage(0));
    expect(messages).toEqual([MessageType.Welcome, MessageType.Tick, MessageType.Pong]);
  });

  it('reports a KICK, then reports its code as the close code', () => {
    const h = opened();
    h.socket().deliver(kickMessage(CloseCode.TryAgainLater, 'lagging'));
    expect(h.kicks).toEqual([{ code: CloseCode.TryAgainLater, reason: 'lagging' }]);

    // The close frame that follows may carry anything; the KICK is the reason.
    h.socket().serverClose(CloseCode.Normal, '', true);
    expect(h.closes[0]!.code).toBe(CloseCode.TryAgainLater);
    expect(h.closes[0]!.local).toBe(false);
  });

  it('sends BYE and closes on a clean leave', () => {
    const h = opened();
    h.connection.close(CloseCode.Normal, 'done');
    const bye = h.socket().sent[1]!;
    expect(bye[0]).toBe(MessageType.Bye);
    expect(h.socket().closedWith?.code).toBe(CloseCode.Normal);
    expect(h.closes[0]!.local).toBe(true);
    expect(h.connection.state).toBe(ConnectionState.Closed);
  });
});

describe('Connection refusals', () => {
  beforeEach(() => {
    FakeSocket.reset();
  });

  const cases: { name: string; code: number; run: (h: Harness) => void }[] = [
    {
      name: 'a message before WELCOME',
      code: CloseCode.ProtocolError,
      run: (h) => {
        h.connection.connect();
        h.socket().open();
        h.socket().deliver(tickMessage(1));
      },
    },
    {
      name: 'a text frame on a binary protocol',
      code: CloseCode.ProtocolError,
      run: (h) => {
        h.connection.connect();
        h.socket().open();
        h.socket().deliverRaw('hello');
      },
    },
    {
      name: 'a subprotocol the server invented',
      code: CloseCode.ProtocolError,
      run: (h) => {
        h.connection.connect();
        h.socket().open('typhon.9');
      },
    },
    {
      name: 'an unknown message type',
      code: CloseCode.ProtocolError,
      run: (h) => {
        h.connection.connect();
        h.socket().open();
        h.socket().deliver(welcomeMessage());
        h.socket().deliver(Uint8Array.of(0x7e, 1, 2));
      },
    },
    {
      name: 'a WELCOME of another major',
      code: CloseCode.ProtocolError,
      run: (h) => {
        h.connection.connect();
        h.socket().open();
        h.socket().deliver(welcomeMessage({ major: 4 }));
      },
    },
    {
      name: 'a capability the client did not request',
      code: CloseCode.ProtocolError,
      run: (h) => {
        h.connection.connect();
        h.socket().open();
        h.socket().deliver(welcomeMessage({ capsGranted: Capabilities.Stats | Capabilities.Debug }));
      },
    },
    {
      name: 'a catalog skip with nothing cached',
      code: CloseCode.ProtocolError,
      run: (h) => {
        h.connection.connect();
        h.socket().open();
        h.socket().deliver(welcomeMessage({ catalogJson: new Uint8Array(0) }));
      },
    },
    {
      name: 'a truncated WELCOME',
      code: CloseCode.MalformedPayload,
      run: (h) => {
        h.connection.connect();
        h.socket().open();
        h.socket().deliver(welcomeMessage().subarray(0, 12));
      },
    },
    {
      name: 'a catalog this client refuses',
      code: CloseCode.MalformedPayload,
      run: (h) => {
        h.connection.connect();
        h.socket().open();
        h.socket().deliver(welcomeMessage({ catalogJson: Uint8Array.of(0x7b) }));
      },
    },
    {
      name: 'a malformed TICK',
      code: CloseCode.MalformedPayload,
      run: (h) => {
        h.connection.connect();
        h.socket().open();
        h.socket().deliver(welcomeMessage());
        // A block whose declared length runs past the message.
        const w = new WireWriter(32);
        writeTickHeader(w, 1, 0, 0);
        const frame = new Uint8Array([...w.written(), 0x01, 0x40]);
        h.socket().deliver(frame);
      },
    },
  ];

  for (const { name, code, run } of cases) {
    it(`closes with ${code} for ${name}`, () => {
      const h = harness({
        handlers: {
          onTick: (message) => {
            // A malformed frame must not reach the store as a whole: the reader throws mid-decode.
            if (message.length > 6) {
              throw new Error('unreachable');
            }
          },
        },
      });
      run(h);
      expect(h.closes.map((c) => c.code)).toEqual([code]);
      expect(h.closes[0]!.local).toBe(true);
      // A browser only accepts 1000 and 4000–4999 in a close frame (W24): the client says BYE 4004 instead, closes with
      // 1000, and the real code stays in the report.
      expect(parseBye(h.socket().sent[h.socket().sent.length - 1]!)).toBe(CloseCode.ClientRefusedTheStream);
      expect(h.socket().closedWith?.code).toBe(CloseCode.Normal);
    });
  }

  it('closes with 1009 for a WELCOME above the protocol limit, before decoding it', () => {
    const h = harness();
    h.connection.connect();
    h.socket().open();
    h.socket().deliver(new Uint8Array(ProtocolConstants.welcomeMaxBytes + 1));
    expect(h.closes[0]!.code).toBe(CloseCode.MessageTooBig);
    expect(h.sessions).toHaveLength(0);
    expect(parseBye(h.socket().sent[1]!)).toBe(CloseCode.ClientRefusedTheStream);
  });

  it('closes with 1009 for a frame above the catalog limit, before decoding it', () => {
    const h = opened();
    const limit = h.sessions[0]!.plan.catalog.limits.frameBytes;
    h.socket().deliver(new Uint8Array(limit + 1));
    expect(h.closes[0]!.code).toBe(CloseCode.MessageTooBig);
    expect(h.ticks).toHaveLength(0);
  });

  it('sends nothing before WELCOME: a client sends COMMANDS, PING and BYE, and only once open', () => {
    const h = harness();
    h.connection.connect();
    h.socket().open();
    expect(() => {
      h.connection.send(Uint8Array.of(0x84));
    }).toThrow(/not open/);
    // HELLO itself went out under the 16 KiB first-message limit.
    expect(h.socket().sent[0]!.length).toBeLessThanOrEqual(ProtocolConstants.helloMaxBytes);
  });

  it('refuses to send once closed, and connects only once', () => {
    const h = opened();
    h.connection.close();
    expect(() => {
      h.connection.send(Uint8Array.of(1));
    }).toThrow(/not open/);
    expect(() => {
      h.connection.connect();
    }).toThrow(/connects once/);
  });
});

describe('Connection catalog bytes', () => {
  beforeEach(() => {
    FakeSocket.reset();
  });

  it('keeps the catalog JSON exactly as it arrived, for the next session to offer', () => {
    const h = opened();
    expect(Array.from(h.connection.catalogCache!.json)).toEqual(Array.from(catalogBytes()));
  });
});
