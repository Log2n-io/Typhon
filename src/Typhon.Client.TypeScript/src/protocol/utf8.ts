/**
 * UTF-8 through the platform's `TextDecoder` / `TextEncoder`, which every supported runtime has (Node ≥ 22, every
 * browser). They are typed locally because the package compiles against `ES2022` alone: pulling in the DOM or Node
 * typings would let platform-specific globals slip into a library that must run on both.
 */

interface Utf8Decoder {
  decode(input: Uint8Array): string;
}

interface Utf8Encoder {
  encode(input: string): Uint8Array;
}

interface TextCodecGlobals {
  TextDecoder: new (label: string, options: { fatal: boolean; ignoreBOM: boolean }) => Utf8Decoder;
  TextEncoder: new () => Utf8Encoder;
}

const globals = globalThis as unknown as TextCodecGlobals;

/** Strict: invalid UTF-8 throws instead of decoding to U+FFFD. `ignoreBOM` keeps a leading U+FEFF, as C# does. */
const strictDecoder = new globals.TextDecoder('utf-8', { fatal: true, ignoreBOM: true });
const encoder = new globals.TextEncoder();

/** Decodes UTF-8, or returns `null` when the bytes are not valid UTF-8. */
export function decodeUtf8(bytes: Uint8Array): string | null {
  try {
    return strictDecoder.decode(bytes);
  } catch (e) {
    // A fatal decoder reports invalid UTF-8 as a TypeError; anything else is not a verdict on the bytes.
    if (e instanceof TypeError) {
      return null;
    }

    throw e;
  }
}

/** Encodes a string as UTF-8. A lone surrogate becomes U+FFFD, as .NET's `Encoding.UTF8` does. */
export function encodeUtf8(text: string): Uint8Array {
  return encoder.encode(text);
}
