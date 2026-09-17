import { CloseCode } from './constants.js';

/**
 * Bytes received from the other side do not form a valid message. It carries the close code the connection must be
 * closed with, so a transport never classifies a decoding failure itself.
 *
 * Only a decoder throws this, and only for input it received. An encoder handed a value it cannot represent throws a
 * `RangeError` instead: that is a bug on the sending side, not a broken or hostile peer.
 */
export class WireFormatError extends Error {
  readonly closeCode: number;

  constructor(closeCode: number, message: string) {
    super(message);
    this.name = 'WireFormatError';
    this.closeCode = closeCode;
  }
}

/** A payload inconsistent with its type: close 1007. */
export function malformed(message: string): WireFormatError {
  return new WireFormatError(CloseCode.MalformedPayload, message);
}

/** Framing, or an unknown or out-of-state message type: close 1002. */
export function protocolError(message: string): WireFormatError {
  return new WireFormatError(CloseCode.ProtocolError, message);
}
