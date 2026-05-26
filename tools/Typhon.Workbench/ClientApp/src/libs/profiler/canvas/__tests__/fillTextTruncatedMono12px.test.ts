import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  fillTextTruncatedMono12px,
  _getMono12pxCharWidthForTest,
  _resetMono12pxCharWidthCacheForTest,
} from '../timeArea';

/**
 * Truncation logic of `fillTextTruncatedMono12px` — the helper that replaced the per-span
 * `ctx.save() + ctx.clip() + fillText() + ctx.restore()` pattern in `drawOneTickSpans` (the #1
 * Workbench main-thread hotspot at ~21% self before this change, #377 perf follow-up 2026-05-26).
 *
 * No real canvas in jsdom — we stub `CanvasRenderingContext2D` with a minimal fake recording each
 * call's args. The contract under test:
 *   - `availWidth <= 0` → no `fillText` call at all (avoid drawing into a zero-width strip).
 *   - `availWidth` covers the full text → full string written, no slicing.
 *   - `availWidth` covers only part of the text → string sliced to `floor(availWidth / charWidth)`.
 *   - Char width is cached after the first measurement.
 */

interface FakeCtx {
  font: string;
  measureCalls: string[];
  fillTextCalls: Array<{ text: string; x: number; y: number }>;
  measureText: (s: string) => { width: number };
  fillText: (text: string, x: number, y: number) => void;
}

const CHAR_WIDTH = 7.2; // canonical Chrome `12px monospace` width on most platforms; the fake fixes it.

function makeFakeCtx(): FakeCtx {
  const ctx: FakeCtx = {
    font: '',
    measureCalls: [],
    fillTextCalls: [],
    measureText(s: string) {
      this.measureCalls.push(s);
      // Every char is `CHAR_WIDTH` wide regardless of glyph — true monospace property.
      return { width: s.length * CHAR_WIDTH };
    },
    fillText(text: string, x: number, y: number) {
      this.fillTextCalls.push({ text, x, y });
    },
  };
  return ctx;
}

beforeEach(() => {
  _resetMono12pxCharWidthCacheForTest();
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('fillTextTruncatedMono12px', () => {
  it('writes nothing when availWidth is zero or negative', () => {
    const ctx = makeFakeCtx();
    fillTextTruncatedMono12px(ctx as unknown as CanvasRenderingContext2D, 'hello world', 0, 0, 0);
    fillTextTruncatedMono12px(ctx as unknown as CanvasRenderingContext2D, 'hello world', 0, 0, -42);
    expect(ctx.fillTextCalls).toEqual([]);
  });

  it('writes the full string when availWidth is wider than the text', () => {
    const ctx = makeFakeCtx();
    // "hello" = 5 chars × 7.2 = 36 px; 100 px is comfortably more.
    fillTextTruncatedMono12px(ctx as unknown as CanvasRenderingContext2D, 'hello', 10, 20, 100);
    expect(ctx.fillTextCalls).toEqual([{ text: 'hello', x: 10, y: 20 }]);
  });

  it('truncates to floor(availWidth / charWidth) characters when text overflows', () => {
    const ctx = makeFakeCtx();
    // availWidth 25 px → floor(25 / 7.2) = 3 chars. "hello world" → "hel".
    fillTextTruncatedMono12px(ctx as unknown as CanvasRenderingContext2D, 'hello world', 0, 0, 25);
    expect(ctx.fillTextCalls).toEqual([{ text: 'hel', x: 0, y: 0 }]);
  });

  it('writes nothing when not even one character fits', () => {
    const ctx = makeFakeCtx();
    // availWidth 5 px, char width 7.2 px → floor = 0 → no draw.
    fillTextTruncatedMono12px(ctx as unknown as CanvasRenderingContext2D, 'x', 0, 0, 5);
    expect(ctx.fillTextCalls).toEqual([]);
  });

  it('preserves the caller-established font (the helper does not flip 12px monospace itself)', () => {
    const ctx = makeFakeCtx();
    ctx.font = '12px monospace';
    fillTextTruncatedMono12px(ctx as unknown as CanvasRenderingContext2D, 'abc', 0, 0, 100);
    // The helper's only font interaction is via the cache lookup. The caller's font remains in place.
    expect(ctx.font).toBe('12px monospace');
  });

  it('caches the char-width measurement after the first call', () => {
    const ctx = makeFakeCtx();
    fillTextTruncatedMono12px(ctx as unknown as CanvasRenderingContext2D, 'first', 0, 0, 100);
    expect(ctx.measureCalls).toEqual(['M']);

    // Second call must NOT re-measure — the cached width is reused.
    fillTextTruncatedMono12px(ctx as unknown as CanvasRenderingContext2D, 'second', 0, 0, 100);
    expect(ctx.measureCalls).toEqual(['M']);
  });

  it('explicit char-width getter exposes the cached value for sanity-checks', () => {
    const ctx = makeFakeCtx();
    const w = _getMono12pxCharWidthForTest(ctx as unknown as CanvasRenderingContext2D);
    expect(w).toBeCloseTo(CHAR_WIDTH, 5);
  });
});
