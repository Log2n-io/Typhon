import { describe, expect, it } from 'vitest';
import { encodeUtf8 } from '@typhondb/client';

import { SAY_MAX_BYTES, clipUtf8, composeSay } from '../src/state/chat-text';
import {
  BUBBLE_FADE_MS,
  BUBBLE_MAX_MS,
  BUBBLE_MIN_MS,
  SpeechLog,
  bubbleLifetimeMs,
} from '../src/render/speech-bubbles';

/**
 * Chat as a person types it (2026-10-25, Loïc): a line clipped to what `Say` carries, and a bubble over the speaker's
 * head for as long as it takes to read.
 */

describe('a typed line', () => {
  it('fits the wire, cut on a whole character', () => {
    // The SDK's writer THROWS on a string past its cap, inside the send: the clip is what keeps a long paste from
    // taking the frame down.
    const ascii = 'a'.repeat(400);
    expect(clipUtf8(ascii)).toHaveLength(SAY_MAX_BYTES);

    // 'é' is two bytes: 255 a's and one é is 257, so the é goes rather than half of it.
    const cut = clipUtf8(`${'a'.repeat(255)}é`);
    expect(cut).toBe('a'.repeat(255));
    expect(encodeUtf8(cut).length).toBeLessThanOrEqual(SAY_MAX_BYTES);

    // A four-byte character is one code point, two UTF-16 units: never split between them.
    const emoji = clipUtf8('😀'.repeat(70));
    expect(encodeUtf8(emoji).length).toBe(256);
    expect(emoji).toBe('😀'.repeat(64));
  });

  it('is left alone when it fits', () => {
    const text = 'Utinni! 😀\nsecond line';
    expect(clipUtf8(text)).toBe(text);
  });

  it('keeps its new lines and drops blank ends', () => {
    expect(composeSay('  \n hello\r\nthere \n\n')).toBe('hello\nthere');
    expect(composeSay(' \n\t ')).toBe('');
  });

  it('keeps at most four lines, blank ones and control characters gone, as the server will', () => {
    expect(composeSay('a\n\n\nb')).toBe('a\nb');
    expect(composeSay('a\nb\nc\nd\ne\nf')).toBe('a\nb\nc\nd e f');
    expect(composeSay('tab\there\u0007bell')).toBe('tab here bell');
    expect(composeSay('\n'.repeat(256))).toBe('');
  });
});

describe('a speech bubble', () => {
  it('stays up longer for a longer line, within bounds', () => {
    expect(bubbleLifetimeMs('Hi')).toBeGreaterThanOrEqual(BUBBLE_MIN_MS);
    expect(bubbleLifetimeMs('a'.repeat(40))).toBeGreaterThan(bubbleLifetimeMs('Hi'));
    expect(bubbleLifetimeMs('a'.repeat(256))).toBe(BUBBLE_MAX_MS);
  });

  it('is one per speaker: a new line replaces the old one and restarts its clock', () => {
    const log = new SpeechLog();
    log.say(7, 'first', 0);
    log.say(8, 'other', 0);
    log.say(7, 'second', 1000);
    expect(log.size).toBe(2);
    const seven = [...log.active].find((s) => s.netId === 7);
    expect(seven?.text).toBe('second');
    expect(seven?.startMs).toBe(1000);
  });

  it('expires on its own clock and fades over its last moments', () => {
    const log = new SpeechLog();
    log.say(7, 'Hi', 0);
    const speech = [...log.active][0];
    expect(SpeechLog.opacityAt(speech, 0)).toBe(1);
    expect(SpeechLog.opacityAt(speech, speech.endMs - BUBBLE_FADE_MS / 2)).toBeCloseTo(0.5);
    log.expire(speech.endMs - 1);
    expect(log.has(7)).toBe(true);
    log.expire(speech.endMs);
    expect(log.has(7)).toBe(false);
  });

  it('has no head to go over for a speaker this session was never shown, nor for nothing said', () => {
    const log = new SpeechLog();
    log.say(0, 'from nowhere', 0);
    log.say(9, '', 0);
    expect(log.size).toBe(0);
  });
});
