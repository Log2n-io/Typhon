import { describe, expect, it } from 'vitest';
import {
  drawnUsAt,
  easeOutCubic,
  interpZoomWindow,
  pushViewHistory,
  stepViewHistory,
  viewParamsForWindow,
  visibleWindow,
  type ViewHistory,
} from '../criticalPathZoom';

const W = (start: number, end: number) => ({ start, end });

describe('drawnUsAt', () => {
  it('inverts the majorPx mapping at the content edge', () => {
    // majorPx(d) = gutterPx + d*pxPerUs - panMajor → at majorPx = gutterPx, d = panMajor / pxPerUs.
    expect(drawnUsAt(92, /*panMajor*/ 50, /*pxPerUs*/ 0.5, /*gutterPx*/ 92)).toBe(100);
  });

  it('accounts for pan offset', () => {
    expect(drawnUsAt(192, /*panMajor*/ 100, /*pxPerUs*/ 2, /*gutterPx*/ 0)).toBe(146);
  });
});

describe('visibleWindow', () => {
  it('reports the framed drawn-µs interval', () => {
    // pan 0, 1 px/µs, no gutter, 800 px viewport → [0, 800) drawn-µs.
    expect(visibleWindow(0, 1, 0, 800)).toEqual({ start: 0, end: 800 });
  });

  it('subtracts the gutter from the content extent', () => {
    // 1000 px viewport - 92 px gutter = 908 px content; pan 184 px at 2 px/µs → start 92, span 454.
    expect(visibleWindow(184, 2, 92, 1000)).toEqual({ start: 92, end: 92 + 454 });
  });
});

describe('viewParamsForWindow', () => {
  it('frames a window into the content area exactly', () => {
    const { pxPerUs, panMajor } = viewParamsForWindow(200, 600, 0, 800);
    expect(pxPerUs).toBe(2);          // 800 px / 400 µs
    expect(panMajor).toBe(400);        // start 200 µs * 2 px/µs
  });

  it('round-trips with visibleWindow', () => {
    const target = { start: 123, end: 456 };
    const { pxPerUs, panMajor } = viewParamsForWindow(target.start, target.end, 92, 1000);
    const back = visibleWindow(panMajor, pxPerUs, 92, 1000);
    expect(back.start).toBeCloseTo(target.start, 6);
    expect(back.end).toBeCloseTo(target.end, 6);
  });

  it('floors a degenerate window so pxPerUs stays finite', () => {
    const { pxPerUs } = viewParamsForWindow(100, 100, 0, 800);
    expect(Number.isFinite(pxPerUs)).toBe(true);
    expect(pxPerUs).toBeGreaterThan(0);
  });
});

describe('easeOutCubic', () => {
  it('pins the endpoints', () => {
    expect(easeOutCubic(0)).toBe(0);
    expect(easeOutCubic(1)).toBe(1);
  });

  it('is past the midpoint at t=0.5 (ease-out)', () => {
    expect(easeOutCubic(0.5)).toBeGreaterThan(0.5);
  });

  it('clamps out-of-range t', () => {
    expect(easeOutCubic(-1)).toBe(0);
    expect(easeOutCubic(2)).toBe(1);
  });
});

describe('interpZoomWindow', () => {
  const width = (w: { start: number; end: number }) => w.end - w.start;

  it('returns the source window at t=0', () => {
    const r = interpZoomWindow({ start: 0, end: 100 }, { start: 50, end: 80 }, 0);
    expect(r.start).toBeCloseTo(0, 6);
    expect(r.end).toBeCloseTo(100, 6);
  });

  it('returns the target window at t=1', () => {
    const r = interpZoomWindow({ start: 0, end: 100 }, { start: 50, end: 80 }, 1);
    expect(r.start).toBeCloseTo(50, 6);
    expect(r.end).toBeCloseTo(80, 6);
  });

  it('ramps width geometrically — midpoint width is the geometric mean', () => {
    // from width 10, to width 1000 → t=0.5 width = sqrt(10*1000) = 100, not the linear mean 505.
    expect(width(interpZoomWindow({ start: 0, end: 10 }, { start: 0, end: 1000 }, 0.5))).toBeCloseTo(100, 4);
  });

  it('does not lurch on a far-left zoom-out — the first frame stays near the source width', () => {
    // Linear width would put frame-1 (t≈0.06) at width ≈ 1 + 999*0.06 ≈ 61, collapsing pxPerUs ~60×.
    const r = interpZoomWindow({ start: 0, end: 1 }, { start: 0, end: 1000 }, 0.06);
    expect(r.start).toBe(0);                 // far-left start stays pinned
    expect(width(r)).toBeLessThan(2);        // ≈1.5 — a gentle first step, not a jump to ~61
  });

  it('pins a shared right edge — a right-aligned selection never overshoots', () => {
    // from [0,1000] → to [900,1000]: the right edge is common, must stay at 1000 the whole tween.
    for (const t of [0, 0.1, 0.3, 0.5, 0.8, 1]) {
      expect(interpZoomWindow({ start: 0, end: 1000 }, { start: 900, end: 1000 }, t).end).toBeCloseTo(1000, 4);
    }
  });

  it('keeps both endpoints monotonic — no edge swings past its target', () => {
    const from = { start: 0, end: 1000 };
    const to = { start: 900, end: 1000 };
    let prevStart = -Infinity;
    for (let t = 0; t <= 1.0001; t += 0.05) {
      const r = interpZoomWindow(from, to, Math.min(t, 1));
      expect(r.start).toBeGreaterThanOrEqual(prevStart - 1e-6); // start advances monotonically
      expect(r.start).toBeGreaterThanOrEqual(-1e-6);            // never past from.start
      expect(r.start).toBeLessThanOrEqual(900 + 1e-6);          // never past to.start
      prevStart = r.start;
    }
  });

  it('degenerates to a linear pan when the width is constant', () => {
    // equal widths (100) → pure pan → τ = t → start lerps linearly.
    expect(interpZoomWindow({ start: 0, end: 100 }, { start: 500, end: 600 }, 0.5).start).toBeCloseTo(250, 6);
  });
});

describe('pushViewHistory', () => {
  it('appends and advances the pointer onto the new entry', () => {
    const h: ViewHistory = { entries: [W(0, 100)], pointer: 0 };
    const r = pushViewHistory(h, W(10, 20), 50);
    expect(r.entries).toEqual([W(0, 100), W(10, 20)]);
    expect(r.pointer).toBe(1);
  });

  it('drops forward entries — a new gesture forks the timeline', () => {
    // pointer is mid-stack (1 of 3); a push truncates the [2] entry before appending.
    const h: ViewHistory = { entries: [W(0, 100), W(10, 90), W(20, 80)], pointer: 1 };
    const r = pushViewHistory(h, W(30, 70), 50);
    expect(r.entries).toEqual([W(0, 100), W(10, 90), W(30, 70)]);
    expect(r.pointer).toBe(2);
  });

  it('caps length, dropping the oldest entry', () => {
    const h: ViewHistory = { entries: [W(0, 1), W(0, 2), W(0, 3)], pointer: 2 };
    const r = pushViewHistory(h, W(0, 4), 3);
    expect(r.entries).toEqual([W(0, 2), W(0, 3), W(0, 4)]);
    expect(r.pointer).toBe(2);
  });
});

describe('stepViewHistory', () => {
  const h: ViewHistory = { entries: [W(0, 1), W(0, 2), W(0, 3)], pointer: 1 };

  it('moves the pointer back', () => {
    expect(stepViewHistory(h, -1).pointer).toBe(0);
  });

  it('moves the pointer forward', () => {
    expect(stepViewHistory(h, 1).pointer).toBe(2);
  });

  it('returns the same object at the start (no-op signal)', () => {
    const atStart: ViewHistory = { entries: h.entries, pointer: 0 };
    expect(stepViewHistory(atStart, -1)).toBe(atStart);
  });

  it('returns the same object at the end (no-op signal)', () => {
    const atEnd: ViewHistory = { entries: h.entries, pointer: 2 };
    expect(stepViewHistory(atEnd, 1)).toBe(atEnd);
  });
});
