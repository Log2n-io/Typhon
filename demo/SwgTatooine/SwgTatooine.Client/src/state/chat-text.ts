/**
 * What a typed line becomes on the wire (`Say`, SWG-09).
 *
 * <b>Clipped here, not refused there.</b> The command's `text` is a string capped at 256 UTF-8 bytes, and the SDK's
 * writer THROWS on one past its cap — inside the send, which would take the frame down for a long paste. So the line is
 * cut to the cap before it is enqueued, on a code point boundary: a cut through a multi-byte character would put
 * invalid UTF-8 on the wire, and the server's decoder is right to refuse that.
 */

/** The most bytes one `Say` carries: the server's `Utf8Text256.Capacity`. */
export const SAY_MAX_BYTES = 256;

/** How many UTF-8 bytes a code point takes. */
function utf8Bytes(codePoint: number): number {
  return codePoint < 0x80 ? 1 : codePoint < 0x800 ? 2 : codePoint < 0x10000 ? 3 : 4;
}

/**
 * The longest prefix of `text` that fits `maxBytes` of UTF-8, whole code points only.
 *
 * @param text Any string; a lone surrogate counts as the three bytes of the replacement character it is encoded as.
 * @param maxBytes The cap.
 */
export function clipUtf8(text: string, maxBytes = SAY_MAX_BYTES): string {
  let bytes = 0;
  let end = 0;
  for (const ch of text) {
    const cp = ch.codePointAt(0) ?? 0;
    const n = cp >= 0xd800 && cp <= 0xdfff ? 3 : utf8Bytes(cp);
    if (bytes + n > maxBytes) {
      break;
    }

    bytes += n;
    end += ch.length;
  }

  return end === text.length ? text : text.slice(0, end);
}

/** The most lines a line keeps: the server's `ChatLines.MaxLines`, what a bubble over a head can hold. */
export const SAY_MAX_LINES = 4;

/**
 * The line to send for what is in the box: control characters as spaces, blank lines and spaces at either end dropped,
 * runs of new lines as one, no more than {@link SAY_MAX_LINES} lines (the breaks past that become spaces), then clipped.
 * The server's `ChatLines.Normalize` does the same to whatever arrives; doing it here too means the speaker sends what
 * everyone will see. An empty result means "nothing to say", which closes the box without sending.
 */
export function composeSay(draft: string): string {
  const lines = draft
    .replace(/\r\n?/g, '\n')
    // eslint-disable-next-line no-control-regex -- control characters are exactly what is being removed
    .replace(/[\u0000-\u0009\u000b-\u001f\u007f]/g, ' ')
    .trim()
    .split(/\n+/);
  const kept =
    lines.length <= SAY_MAX_LINES
      ? lines
      : [...lines.slice(0, SAY_MAX_LINES - 1), lines.slice(SAY_MAX_LINES - 1).join(' ')];
  return clipUtf8(kept.join('\n'));
}
