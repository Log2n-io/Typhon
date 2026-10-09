/**
 * Little-endian binary reader backed by a DataView over a fixed-position Uint8Array slice. Used by the record decoder to walk one record at
 * a time — the caller slices `bytes.subarray(pos, pos + size)`, constructs a reader, and reads fields by offset.
 *
 * **No cursor-mutation helpers.** The LZ4-decompressed record block has enough structure (12 B common header + 25 B span extension + 16 B
 * optional trace context + kind-specific payload) that offset-based reads are more readable than a running cursor. The reader's API is
 * therefore "give me the u16 at offset 4", not "seek + read next u16" — matches the pattern in the .NET codecs we're porting from.
 *
 * **i64 / u64 policy.** JavaScript's `number` loses precision above 2^53. 64-bit trace IDs and TSNs must be returned as decimal strings
 * (via `readI64Decimal` / `readU64Decimal`) so the wire values round-trip faithfully. Timestamp math uses `number` intentionally: a trace's
 * timestamp values are QueryPerformanceCounter-domain i64 (10 MHz range), and dividing by `ticksPerUs` to get µs-as-f64 rarely exceeds 2^40
 * in practice — well within safe-integer range for any realistic trace duration (decades of wall-clock time). Callers doing µs math thus
 * pay no precision penalty.
 */
export class BinaryReader {
  private readonly view: DataView;

  constructor(bytes: Uint8Array) {
    this.view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  }

  /** Size of the underlying slice in bytes. */
  get length(): number { return this.view.byteLength; }

  readU8(offset: number): number {
    return this.view.getUint8(offset);
  }

  readU16(offset: number): number {
    return this.view.getUint16(offset, /*littleEndian*/ true);
  }

  readU32(offset: number): number {
    return this.view.getUint32(offset, /*littleEndian*/ true);
  }

  readI32(offset: number): number {
    return this.view.getInt32(offset, /*littleEndian*/ true);
  }

  readF32(offset: number): number {
    return this.view.getFloat32(offset, /*littleEndian*/ true);
  }

  /**
   * Read an f64. Added for kind 70, the first record to carry one.
   *
   * The engine sends the tightness numerators and the relocation spend as f64 rather than the f32 means kind 66 uses,
   * because a consumer folding several realms must re-derive a mean from summed numerators over a summed sample count —
   * and that sum is checkable against the archetype-wide total in f64, only approximately so after an f32 round trip.
   * A `number` is an f64, so nothing is lost on this side.
   */
  readF64(offset: number): number {
    return this.view.getFloat64(offset, /*littleEndian*/ true);
  }

  /**
   * Read an i64 as a Number. Only safe for values within 2^53 — use <see cref="readI64Decimal"/> for IDs/TSNs that can exceed that range.
   * OK for timestamp values that get divided by ticksPerUs immediately (the division compresses the range).
   */
  readI64AsNumber(offset: number): number {
    // BigInt arithmetic works for any range; converting to Number is the potentially-lossy step. We do it here because timestamps always
    // fit in safe-integer range in practice (a 10 MHz QPC runs for 28+ years before exceeding 2^53).
    const low = this.view.getUint32(offset, true);
    const high = this.view.getInt32(offset + 4, true);
    return high * 0x100000000 + low;
  }

  /**
   * Read a u64 as a decimal string. Preserves full 64-bit precision — use for SpanId, ParentSpanId, TraceIdHi/Lo, EntityId, and any other
   * wire-level identifier that may exceed 2^53. Matches the server's JSON DTO format (which also emits decimal strings for these fields).
   */
  readU64Decimal(offset: number): string {
    const low = BigInt(this.view.getUint32(offset, true));
    const high = BigInt(this.view.getUint32(offset + 4, true));
    return ((high << 32n) | low).toString();
  }

  /**
   * Read an i64 as a decimal string. For signed TSN values — sign bit is the high bit of the high word. Same precision guarantee as
   * <see cref="readU64Decimal"/>.
   */
  readI64Decimal(offset: number): string {
    const low = BigInt(this.view.getUint32(offset, true));
    const high = BigInt(this.view.getInt32(offset + 4, true));
    return ((high << 32n) | low).toString();
  }

  /**
   * Decode a UTF-8 byte slice starting at <paramref name="offset"/> with <paramref name="byteLength"/> bytes. Uses a caller-provided
   * <see cref="TextDecoder"/> so the hot path can reuse a single instance across calls. Returns the empty string if byteLength is 0.
   */
  readUtf8(offset: number, byteLength: number, decoder: TextDecoder): string {
    if (byteLength <= 0) return '';
    const bytes = new Uint8Array(this.view.buffer, this.view.byteOffset + offset, byteLength);
    return decoder.decode(bytes);
  }
}
