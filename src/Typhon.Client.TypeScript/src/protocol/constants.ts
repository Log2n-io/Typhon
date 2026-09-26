/**
 * Values fixed by the protocol major (`claude/design/Subscriptions/03-wire-protocol.md` § 2–3), which a client needs
 * before it has received any catalog.
 */
export const ProtocolConstants = {
  /**
   * The protocol major this library speaks: WebSocket subprotocol `typhon.3`, TCP preamble `TYP3`. Major 3 (Realms D-4):
   * an absolute velocity unit and realm-framed positions, which an older client would decode wrongly without noticing.
   */
  major: 3,
  /** The protocol minor this library speaks; the lower minor of the two sides wins. */
  minor: 0,
  webSocketSubprotocol: 'typhon.3',
  /** Largest `HELLO` message, in bytes: the first message's own limit, before `limits.clientMessageBytes` applies. */
  helloMaxBytes: 16 * 1024,
  /**
   * Largest `WELCOME` a client reads: the one inbound message it cannot bound with `limits.frameBytes`, since those
   * limits arrive inside it (§ 10).
   */
  welcomeMaxBytes: 1024 * 1024,
  tokenMaxBytes: 8 * 1024,
  sessionKindMaxBytes: 32,
  helloPayloadMaxBytes: 256,
  /** A WebSocket close frame's own limit (RFC 6455 § 5.5). */
  kickReasonMaxBytes: 123,
  helloTimeoutMs: 5000,
  firstAppCommandIdx: 16,
  firstAppEventIdx: 16,
  firstAppMetricIdx: 32,
  /** Change groups per archetype, public or owner: the width of a record's `u8` mask (W14, W17). */
  maxGroups: 8,
  /** An SDK limit, not a wire one: the store's entity handle holds the archetype in 8 bits (W18). */
  maxArchetypes: 255,
  /** The widest `bits` field: a shift of at most 7 plus the width fits one 32-bit read (W12). */
  maxPackedBits: 24,
  maxListCount: 255,
  /** The most cells a grid may have: its cell index fits comfortably in 32 bits, and its counts in memory. */
  maxGridCells: 1 << 24,
  /** The largest event, command or metric index: wire indices are dense from their reserved base (W27). */
  maxMessageIndex: 0xffff,
  /** The legal range of a velocity codec's `unitExp` (W5): 2^-40 to 2^16 metres per tick. */
  minVelocityUnitExp: -40,
  maxVelocityUnitExp: 16,
  /** The most realm kinds a catalog declares: a `REALM` block's `varu kindIdx` stays one byte. */
  maxRealmKinds: 128,
  /**
   * Bits per axis of a realm-framed field a client SENDS: always 32, whatever the realm's own width, because the
   * server's transport parses a command without reading the session's realm (SUB-05).
   */
  commandPositionBits: 32,
} as const;

/** The 4-byte TCP preamble, ASCII `TYP3`: each side writes its own, and a mismatch closes without a `KICK` (W31). */
export const TCP_PREAMBLE: readonly number[] = [0x54, 0x59, 0x50, 0x33];

/** The first byte of every message. Client-to-server types have the high bit set. */
export const MessageType = {
  Welcome: 0x01,
  Tick: 0x02,
  Pong: 0x03,
  Kick: 0x04,
  Hello: 0x81,
  Commands: 0x83,
  Ping: 0x84,
  Bye: 0x85,
} as const;

/** The block types inside a `TICK`. An unknown type is skipped by its length. */
export const BlockType = {
  Entities: 0x01,
  Events: 0x02,
  Self: 0x03,
  Agg: 0x04,
  Stats: 0x05,
  Debug: 0x06,
  Acks: 0x07,
  Sources: 0x08,
  /** The session's realm frame (`typhon.3`): only in a `RESET` frame, and always its first block. */
  Realm: 0x09,
  Ext: 0x7f,
} as const;

/** The `TICK` flags byte. */
export const TickFlags = {
  /** The initial fill under the enter budget is complete. */
  ViewComplete: 1,
  /** Clear the store before applying this frame. */
  Reset: 2,
  /** The server is overloaded and dilating time. */
  Overload: 4,
  /** A `u32 periodUs` follows the flags: the duration of the interval that just elapsed, [N − 1, N]. */
  Period: 8,
} as const;

/** The `caps` bits (W23): only server work a client may decline. Bits 2–31 are reserved, sent as 0 and ignored. */
export const Capabilities = {
  None: 0,
  Stats: 1,
  Debug: 2,
} as const;

/** A `SOURCES` entry's status. */
export const SourceStatus = {
  Applied: 0,
  /** The request failed; the previous source set is kept, and a `u16` code follows. */
  Error: 1,
} as const;

/** Reason codes of an `ACKS` rejection record. Codes 128–255 belong to the application. */
export const AckReason = {
  RateLimited: 1,
  Rejected: 2,
  RegionInvalid: 3,
  /** The session's role may not send this command type (the catalog's `roles`). */
  Forbidden: 4,
  FirstApplicationReason: 128,
} as const;

/** Close codes with their RFC 6455 meanings, plus the engine's private-use range (W24). */
export const CloseCode = {
  Normal: 1000,
  GoingAway: 1001,
  /** Framing, or an unknown or out-of-state message type. */
  ProtocolError: 1002,
  /** A bad index, length, count, UTF-8 sequence or an over-cap field. */
  MalformedPayload: 1007,
  /** Sustained abuse: well-formed messages, refused for too long — over budget, over rate, the wrong role. Reconnect with backoff. */
  PolicyViolation: 1008,
  MessageTooBig: 1009,
  InternalError: 1011,
  TryAgainLater: 1013,
  NoAcknowledgement: 4001,
  HelloTimeout: 4002,
  AuthenticationRejected: 4003,
  /**
   * The client refused the stream for a fault it cannot report as a WebSocket code — a browser may not send 1002, 1007
   * or 1009 (§ 10). It sends `BYE 4004`, closes with {@link CloseCode.Normal} and reports the real code to the
   * application. A server never sends it.
   */
  ClientRefusedTheStream: 4004,
  FirstApplicationCode: 4100,
  LastApplicationCode: 4999,
} as const;

/** The events the engine declares itself, at reserved indices 0–15 (W27). */
export const BuiltInEvent = {
  EventsLost: 'EventsLost',
  EventsLostIdx: 0,
  eventsLostCountField: 'count',
} as const;

/** The built-in commands (W27, W28), at reserved indices that never move. */
export const BuiltInCommand = {
  ClientRegion: 'ClientRegion',
  ClientRegionIdx: 0,
  SubscribeRequest: 'SubscribeRequest',
  SubscribeRequestIdx: 1,
  regionVerticesField: 'vertices',
  regionAltitudeField: 'altitudeM',
  regionBudgetField: 'budgetKiBps',
} as const;

/** Whether a client may send `code` in `BYE`: 1000, or 4000–4999 — the codes a browser's `WebSocket.close` accepts. */
export function isValidClientCloseCode(code: number): boolean {
  return code === CloseCode.Normal || (code >= 4000 && code <= 4999);
}
