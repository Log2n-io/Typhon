import { isValidClientCloseCode, MessageType, ProtocolConstants } from './constants.js';
import { malformed, protocolError } from './errors.js';
import { WireReader } from './reader.js';
import { uintInRange } from './range.js';
import { encodeUtf8 } from './utf8.js';
import type { WireWriter } from './writer.js';

/*
 * The control messages of § 3. Writers include the type byte; readers take a reader positioned after it, so a transport
 * reads the type once and dispatches. Tokens and hashes are opaque bytes: no 64-bit integer exists on the wire (§ 1).
 */

const RESUME_TOKEN_BYTES = 16;
const CATALOG_HASH_BYTES = 8;

/** `HELLO` (0x81): the client's first message, at most {@link ProtocolConstants.helloMaxBytes}. */
export interface HelloMessage {
  readonly major: number;
  readonly minor: number;
  /** The optional work asked for: {@link Capabilities} bits. */
  readonly caps: number;
  /** An application-declared session kind the engine never interprets, ≤ 32 UTF-8 bytes (W21). */
  readonly kind: string;
  /** An opaque token handed to the application's admission hook, ≤ 8 KiB (W22). */
  readonly token: string;
  /** 16 bytes from an earlier `WELCOME`; all zeros means none (W19). */
  readonly resumeToken: Uint8Array;
  /** The catalog digest already held, 8 bytes as they travel; all zeros means none (W20). */
  readonly clientCatalogHash: Uint8Array;
  /** Application data for the session's `Opened` hook, ≤ 256 bytes; empty means none. */
  readonly helloPayload: Uint8Array;
}

/** `WELCOME` (0x01): the server's reply to `HELLO`. */
export interface WelcomeMessage {
  readonly major: number;
  readonly minor: number;
  /** The optional work granted: a subset of what was requested. */
  readonly capsGranted: number;
  readonly sessionId: number;
  /** 16 bytes; all zeros when resume is disabled. */
  readonly resumeToken: Uint8Array;
  readonly tick: number;
  readonly tickPeriodUs: number;
  /** The catalog digest, 8 bytes as they travel. */
  readonly catalogHash: Uint8Array;
  /** The canonical catalog JSON, or empty when the client's hash matched and the catalog was skipped. */
  readonly catalogJson: Uint8Array;
}

export interface PingMessage {
  readonly clientMs: number;
  /** Drives the server's lag skip (02-execution § 6). */
  readonly lastAppliedTick: number;
}

export interface PongMessage {
  readonly clientMs: number;
  readonly tick: number;
  /** How far the server is into its tick, in microseconds: a `u32`, since a tick exceeds 65 535 µs (W31). */
  readonly usIntoTick: number;
}

export interface KickMessage {
  /** A close code (W24). */
  readonly code: number;
  /** ≤ 123 UTF-8 bytes, so the same string fits a WebSocket close frame. */
  readonly reason: string;
}

// One reader for every parse: not re-entrant, so a `read` callback must not parse another control message. It lets go of
// the message once a parse ends, so a WELCOME's catalog JSON does not stay reachable through it.
const parser = new WireReader();

/**
 * Parses a whole control message: its type byte must be `type` (else 1002), and its body must end exactly where the
 * message does (else 1007).
 */
export function parseMessage<T>(message: Uint8Array, type: number, read: (r: WireReader) => T): T {
  const r = parser.reset(message);
  try {
    const actual = r.u8();
    if (actual !== type) {
      throw protocolError(`expected message type 0x${type.toString(16)}, got 0x${actual.toString(16)}`);
    }

    const result = read(r);
    r.expectEnd('message');
    return result;
  } finally {
    r.release();
  }
}

export const parseHello = (message: Uint8Array): HelloMessage => parseMessage(message, MessageType.Hello, readHello);
export const parseWelcome = (message: Uint8Array): WelcomeMessage =>
  parseMessage(message, MessageType.Welcome, readWelcome);
export const parsePing = (message: Uint8Array): PingMessage => parseMessage(message, MessageType.Ping, readPing);
export const parsePong = (message: Uint8Array): PongMessage => parseMessage(message, MessageType.Pong, readPong);
export const parseKick = (message: Uint8Array): KickMessage => parseMessage(message, MessageType.Kick, readKick);
export const parseBye = (message: Uint8Array): number => parseMessage(message, MessageType.Bye, readBye);

// ---- HELLO ----------------------------------------------------------------------------------------------------------

export function writeHello(w: WireWriter, m: HelloMessage): void {
  w.u8(MessageType.Hello);
  w.u16(uintInRange(m.major, 0xffff, 'major'));
  w.u16(uintInRange(m.minor, 0xffff, 'minor'));
  w.u32(uintInRange(m.caps, 0xffffffff, 'caps'));
  w.str(m.kind, ProtocolConstants.sessionKindMaxBytes);
  w.str(m.token, ProtocolConstants.tokenMaxBytes);
  w.raw(exactly(m.resumeToken, RESUME_TOKEN_BYTES, 'resume token'));
  w.raw(exactly(m.clientCatalogHash, CATALOG_HASH_BYTES, 'catalog hash'));
  w.blob(m.helloPayload, ProtocolConstants.helloPayloadMaxBytes);
}

export function readHello(r: WireReader): HelloMessage {
  return {
    major: r.u16(),
    minor: r.u16(),
    caps: r.u32(),
    kind: r.str(ProtocolConstants.sessionKindMaxBytes),
    token: r.str(ProtocolConstants.tokenMaxBytes),
    resumeToken: r.copyBytes(RESUME_TOKEN_BYTES),
    clientCatalogHash: r.copyBytes(CATALOG_HASH_BYTES),
    helloPayload: r.copyBytes(r.blobLength(ProtocolConstants.helloPayloadMaxBytes)),
  };
}

// ---- WELCOME --------------------------------------------------------------------------------------------------------

export function writeWelcome(w: WireWriter, m: WelcomeMessage): void {
  w.u8(MessageType.Welcome);
  w.u16(uintInRange(m.major, 0xffff, 'major'));
  w.u16(uintInRange(m.minor, 0xffff, 'minor'));
  w.u32(uintInRange(m.capsGranted, 0xffffffff, 'capsGranted'));
  w.u32(uintInRange(m.sessionId, 0xffffffff, 'sessionId'));
  w.raw(exactly(m.resumeToken, RESUME_TOKEN_BYTES, 'resume token'));
  w.u32(uintInRange(m.tick, 0xffffffff, 'tick'));
  w.u32(uintInRange(m.tickPeriodUs, 0xffffffff, 'tickPeriodUs'));
  w.raw(exactly(m.catalogHash, CATALOG_HASH_BYTES, 'catalog hash'));
  w.varu(m.catalogJson.length);
  w.raw(m.catalogJson);
}

export function readWelcome(r: WireReader): WelcomeMessage {
  return {
    major: r.u16(),
    minor: r.u16(),
    capsGranted: r.u32(),
    sessionId: r.u32(),
    resumeToken: r.copyBytes(RESUME_TOKEN_BYTES),
    tick: r.u32(),
    tickPeriodUs: r.u32(),
    catalogHash: r.copyBytes(CATALOG_HASH_BYTES),
    catalogJson: r.copyBytes(r.blobLength(r.remaining)),
  };
}

/**
 * Throws 1002 when the server granted a capability the client did not request (W23): a server bug, not something to
 * work around. Unknown bits the client did request and the server ignored are fine.
 */
export function checkCapsGranted(requested: number, granted: number): void {
  if ((granted & ~requested) !== 0) {
    throw protocolError(
      `the server granted caps 0x${granted.toString(16)} beyond the 0x${requested.toString(16)} requested`,
    );
  }
}

// ---- PING / PONG ----------------------------------------------------------------------------------------------------

export function writePing(w: WireWriter, m: PingMessage): void {
  w.u8(MessageType.Ping);
  w.u32(uintInRange(m.clientMs, 0xffffffff, 'clientMs'));
  w.u32(uintInRange(m.lastAppliedTick, 0xffffffff, 'lastAppliedTick'));
}

export function readPing(r: WireReader): PingMessage {
  return { clientMs: r.u32(), lastAppliedTick: r.u32() };
}

export function writePong(w: WireWriter, m: PongMessage): void {
  w.u8(MessageType.Pong);
  w.u32(uintInRange(m.clientMs, 0xffffffff, 'clientMs'));
  w.u32(uintInRange(m.tick, 0xffffffff, 'tick'));
  w.u32(uintInRange(m.usIntoTick, 0xffffffff, 'usIntoTick'));
}

export function readPong(r: WireReader): PongMessage {
  return { clientMs: r.u32(), tick: r.u32(), usIntoTick: r.u32() };
}

// ---- KICK / BYE -----------------------------------------------------------------------------------------------------

/** Writes `KICK`, truncating the reason at a code-point boundary to fit a WebSocket close frame (W24). */
export function writeKick(w: WireWriter, m: KickMessage): void {
  w.u8(MessageType.Kick);
  w.u16(uintInRange(m.code, 0xffff, 'close code'));
  w.str(truncateUtf8(m.reason, ProtocolConstants.kickReasonMaxBytes), ProtocolConstants.kickReasonMaxBytes);
}

export function readKick(r: WireReader): KickMessage {
  return { code: r.u16(), reason: r.str(ProtocolConstants.kickReasonMaxBytes) };
}

/** Writes `BYE`: 1000, or a code in 4000–4999 — the codes a browser's `close()` accepts. */
export function writeBye(w: WireWriter, code: number): void {
  if (!isValidClientCloseCode(uintInRange(code, 0xffff, 'BYE code'))) {
    throw new RangeError(`BYE code ${code} is neither 1000 nor in 4000–4999`);
  }

  w.u8(MessageType.Bye);
  w.u16(code);
}

export function readBye(r: WireReader): number {
  const code = r.u16();
  if (!isValidClientCloseCode(code)) {
    throw malformed(`BYE code ${code} is not a client code`);
  }

  return code;
}

/** The longest prefix of `text` whose UTF-8 encoding fits `maxBytes`, cut between code points. */
export function truncateUtf8(text: string, maxBytes: number): string {
  let bytes = 0;
  let i = 0;
  while (i < text.length) {
    const unit = text.charCodeAt(i);
    const pair = unit >= 0xd800 && unit <= 0xdbff && i + 1 < text.length;
    const width = pair ? 2 : 1;
    const size = encodeUtf8(text.slice(i, i + width)).length;
    if (bytes + size > maxBytes) {
      break;
    }

    bytes += size;
    i += width;
  }

  return text.slice(0, i);
}

/** A catalog digest's display form: 16 lower-case hex digits, most significant first, from its 8 wire bytes (W20). */
export function catalogHashToHex(hash: Uint8Array): string {
  let hex = '';
  for (let i = CATALOG_HASH_BYTES - 1; i >= 0; i--) {
    hex += (hash[i] ?? 0).toString(16).padStart(2, '0');
  }

  return hex;
}

/** The 8 wire bytes of a catalog digest from its display form. */
export function catalogHashFromHex(hex: string): Uint8Array {
  if (!/^[0-9a-f]{16}$/.test(hex)) {
    throw new RangeError(`'${hex}' is not 16 lower-case hex digits`);
  }

  const bytes = new Uint8Array(CATALOG_HASH_BYTES);
  for (let i = 0; i < CATALOG_HASH_BYTES; i++) {
    bytes[CATALOG_HASH_BYTES - 1 - i] = parseInt(hex.slice(2 * i, 2 * i + 2), 16);
  }

  return bytes;
}

function exactly(bytes: Uint8Array, length: number, what: string): Uint8Array {
  if (bytes.length !== length) {
    throw new RangeError(`a ${what} is exactly ${length} bytes, got ${bytes.length}`);
  }

  return bytes;
}
