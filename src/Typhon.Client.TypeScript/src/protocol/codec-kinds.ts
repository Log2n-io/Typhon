/** The closed set of wire codecs (`03-wire-protocol.md` § 4) and their wire tokens. */

/**
 * The closed set of wire codecs (§ 4), as small integers for a `switch` interpreter. `Unknown` is a newer server's
 * token.
 */
export const CodecKind = {
  Unknown: 0,
  Bool: 1,
  U8: 2,
  I8: 3,
  U16: 4,
  I16: 5,
  U32: 6,
  I32: 7,
  Varu: 8,
  Vari: 9,
  F32: 10,
  F16: 11,
  Quant: 12,
  Pos2: 13,
  Pos3: 14,
  Vec2: 15,
  Vec3: 16,
  Vel2: 17,
  Vel3: 18,
  Unorm: 19,
  Snorm: 20,
  Angle: 21,
  Quat3: 22,
  Bits: 23,
  EntityRef: 24,
  Str: 25,
  Bytes: 26,
  Blob: 27,
  TickLo: 28,
  List: 29,
} as const;

export type CodecKind = (typeof CodecKind)[keyof typeof CodecKind];

/** The wire tokens, indexed by {@link CodecKind}: the contract the codec table is keyed by. */
export const CODEC_TOKENS: readonly string[] = [
  '',
  'bool',
  'u8',
  'i8',
  'u16',
  'i16',
  'u32',
  'i32',
  'varu',
  'vari',
  'f32',
  'f16',
  'quant',
  'pos2',
  'pos3',
  'vec2',
  'vec3',
  'vel2',
  'vel3',
  'unorm',
  'snorm',
  'angle',
  'quat3',
  'bits',
  'entityRef',
  'str',
  'bytes',
  'blob',
  'tickLo',
  'list',
];

const kindByToken = new Map<string, CodecKind>(CODEC_TOKENS.map((token, kind) => [token, kind as CodecKind]));

export function codecKindOf(token: string): CodecKind {
  return token === '' ? CodecKind.Unknown : (kindByToken.get(token) ?? CodecKind.Unknown);
}

/** Whether a codec lives in its section's leading bit pack (W12). */
export function isPacked(kind: CodecKind): boolean {
  return kind === CodecKind.Bits || kind === CodecKind.Bool;
}

const LIST_ELEMENTS: ReadonlySet<CodecKind> = new Set([
  CodecKind.U8,
  CodecKind.I8,
  CodecKind.U16,
  CodecKind.I16,
  CodecKind.U32,
  CodecKind.I32,
  CodecKind.Varu,
  CodecKind.Vari,
  CodecKind.EntityRef,
  CodecKind.F32,
  CodecKind.F16,
  CodecKind.Quant,
  CodecKind.Pos2,
  CodecKind.Pos3,
  CodecKind.Vec2,
  CodecKind.Vec3,
  CodecKind.Unorm,
  CodecKind.Snorm,
  CodecKind.Angle,
  CodecKind.Quat3,
]);

/** Whether a codec may be a list element: numeric, byte-aligned, and independent of the frame (W28). */
export function isListElement(kind: CodecKind): boolean {
  return LIST_ELEMENTS.has(kind);
}
