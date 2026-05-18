import { describe, expect, it } from 'vitest';
import { decodeBase64, decodeInt32, decodeUint16 } from '../dbMapFetch';

// The detail tier travels as base64-encoded little-endian SoA buffers; these decoders mirror the server's
// MemoryMarshal.AsBytes writes (StorageMapService.Detail.cs).

function base64Of(bytes: number[]): string {
  return Buffer.from(bytes).toString('base64');
}

describe('decodeBase64', () => {
  it('round-trips raw bytes', () => {
    expect(Array.from(decodeBase64(base64Of([1, 2, 255, 0])))).toEqual([1, 2, 255, 0]);
  });
});

describe('decodeInt32', () => {
  it('decodes little-endian int32 values', () => {
    // 1 and -1 in little-endian.
    const b64 = base64Of([1, 0, 0, 0, 0xff, 0xff, 0xff, 0xff]);
    expect(Array.from(decodeInt32(b64))).toEqual([1, -1]);
  });
});

describe('decodeUint16', () => {
  it('decodes little-endian uint16 values', () => {
    // 1 and 65535 in little-endian.
    const b64 = base64Of([1, 0, 0xff, 0xff]);
    expect(Array.from(decodeUint16(b64))).toEqual([1, 65535]);
  });
});
