import type { CatalogPlan, MessagePlan } from './catalog.js';
import { MessageType } from './constants.js';
import { malformed, protocolError } from './errors.js';
import { readSection, writeSection, type FieldSink, type FieldValues } from './field-codec.js';
import { uintInRange } from './range.js';
import { WireReader } from './reader.js';
import type { RealmFrame } from './realm-frame.js';
import type { WireWriter } from './writer.js';

/*
 * `COMMANDS` (0x83): `u32 clientTick | varu count ≥ 1 | (varu cmdTypeIdx | u16 seq | fields)*` — the client's batch for
 * one rendered frame (§ 3, § 8). Fields travel in the catalog's canonical order, decoded field by field (W11).
 */

/** One command to encode. */
export interface CommandInput {
  readonly type: MessagePlan;
  /** The session's sequence number: wraps at 2^16, compared with serial arithmetic (RFC 1982). */
  readonly seq: number;
  readonly values: FieldValues;
}

/** Receives a decoded `COMMANDS` message: each command's header, then its fields through the field members. */
export interface CommandSink extends FieldSink {
  command(type: MessagePlan, seq: number, clientTick: number): void;
}

/**
 * Writes a `COMMANDS` message, type byte included. Client → server, an enum value outside its names is refused here
 * rather than sent and answered with a 1007 close (W13). A realm-framed field travels over `frame`'s bounds at the
 * command width, 32 bits (`RealmFrame.forCommands`); a command with one needs the session's frame.
 */
export function writeCommands(
  w: WireWriter,
  clientTick: number,
  commands: readonly CommandInput[],
  frame: RealmFrame | null = null,
): void {
  const commandFrame = frame === null ? null : frame.forCommands;
  if (commands.length === 0) {
    throw new RangeError('a COMMANDS message carries at least one command');
  }

  w.u8(MessageType.Commands);
  w.u32(uintInRange(clientTick, 0xffffffff, 'clientTick'));
  w.varu(commands.length);
  for (const c of commands) {
    w.varu(c.type.idx);
    w.u16(uintInRange(c.seq, 0xffff, 'seq'));
    writeSection(w, c.type.body, c.values, true, commandFrame);
  }
}

// One reader for every decode: not re-entrant, so a sink must not decode another COMMANDS message from its callbacks. It
// lets go of the message once a decode ends.
const reader = new WireReader();

/** Consumes a decode without keeping anything: the validation pass. */
const discard: CommandSink = {
  command: () => undefined,
  number: () => undefined,
  text: () => undefined,
  bytes: () => undefined,
  list: () => undefined,
};

/**
 * Validates, then decodes, a whole `COMMANDS` message, type byte included — the server's direction, for tests and tools.
 * The message is validated whole — known types, lengths, enum values within their names (W13, 1007) — before any
 * command reaches `sink` (§ 10 precisions).
 */
export function readCommands(
  message: Uint8Array,
  plan: CatalogPlan,
  sink: CommandSink,
  frame: RealmFrame | null = null,
): void {
  const commandFrame = frame === null ? null : frame.forCommands;
  try {
    decodeCommands(message, plan, discard, commandFrame);
    decodeCommands(message, plan, sink, commandFrame);
  } finally {
    reader.release();
  }
}

function decodeCommands(message: Uint8Array, plan: CatalogPlan, sink: CommandSink, frame: RealmFrame | null): void {
  const r = reader.reset(message);
  if (r.u8() !== MessageType.Commands) {
    throw protocolError('not a COMMANDS message');
  }

  const clientTick = r.u32();
  let count = r.varu();
  if (count === 0) {
    throw malformed('a COMMANDS message carries at least one command');
  }

  for (; count > 0; count--) {
    const type = plan.command(r.varu());
    const seq = r.u16();
    sink.command(type, seq, clientTick);
    // A tickLo command field rebuilds against the client's claimed tick, the only frame a command has.
    readSection(r, type.body, clientTick, sink, true, frame);
  }

  r.expectEnd('COMMANDS');
}
