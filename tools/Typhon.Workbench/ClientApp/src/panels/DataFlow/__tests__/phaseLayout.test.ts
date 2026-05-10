import { describe, expect, it } from 'vitest';
import {
  AUTO_COLLAPSE_THRESHOLD,
  COLLAPSED_SEGMENT_WIDTH,
  applyPhaseCollapse,
  computePhaseLayout,
  type PhaseAxis,
  tickOffsetToNormalizedX,
} from '../phaseLayout';

describe('computePhaseLayout — empty / degenerate', () => {
  it('returns [] for empty input', () => {
    expect(computePhaseLayout([], 'uniform')).toEqual([]);
    expect(computePhaseLayout([], 'equal')).toEqual([]);
    expect(computePhaseLayout([], 'log')).toEqual([]);
  });

  it('uniform with zero total falls back to equal', () => {
    const result = computePhaseLayout(
      [{ name: 'A', wallClockUs: 0 }, { name: 'B', wallClockUs: 0 }],
      'uniform',
    );
    expect(result.map((s) => [s.xStart, s.xEnd])).toEqual([[0, 0.5], [0.5, 1]]);
  });

  it('log with zero total falls back to equal', () => {
    const result = computePhaseLayout(
      [{ name: 'A', wallClockUs: 0 }, { name: 'B', wallClockUs: 0 }],
      'log',
    );
    expect(result.map((s) => [s.xStart, s.xEnd])).toEqual([[0, 0.5], [0.5, 1]]);
  });
});

describe('computePhaseLayout — uniform mode', () => {
  it('column widths are proportional to wall-clock contribution', () => {
    const result = computePhaseLayout(
      [
        { name: 'Input',     wallClockUs: 100 },
        { name: 'Sim',       wallClockUs: 800 },
        { name: 'Output',    wallClockUs: 100 },
      ],
      'uniform',
    );
    expect(result[0].xStart).toBe(0);
    expect(result[0].xEnd).toBeCloseTo(0.1);
    expect(result[1].xStart).toBeCloseTo(0.1);
    expect(result[1].xEnd).toBeCloseTo(0.9);
    expect(result[2].xStart).toBeCloseTo(0.9);
    expect(result[2].xEnd).toBe(1);
  });

  it('last segment always lands exactly at 1.0 (no fp drift)', () => {
    const result = computePhaseLayout(
      [
        { name: 'A', wallClockUs: 333 },
        { name: 'B', wallClockUs: 333 },
        { name: 'C', wallClockUs: 333 },
      ],
      'uniform',
    );
    expect(result[2].xEnd).toBe(1);
  });
});

describe('computePhaseLayout — equal mode', () => {
  it('every column gets 1/N width regardless of contribution', () => {
    const result = computePhaseLayout(
      [
        { name: 'A', wallClockUs: 1 },
        { name: 'B', wallClockUs: 999_999 },
        { name: 'C', wallClockUs: 50 },
      ],
      'equal',
    );
    expect(result[0]).toMatchObject({ xStart: 0, xEnd: 1 / 3 });
    expect(result[1]).toMatchObject({ xStart: 1 / 3, xEnd: 2 / 3 });
    expect(result[2]).toMatchObject({ xStart: 2 / 3, xEnd: 1 });
  });
});

describe('computePhaseLayout — log mode', () => {
  it('compresses the dominant phase relative to uniform', () => {
    const phases = [
      { name: 'Input',     wallClockUs: 100 },
      { name: 'Simulation', wallClockUs: 100_000 },
      { name: 'Output',    wallClockUs: 100 },
    ];
    const uniform = computePhaseLayout(phases, 'uniform');
    const log = computePhaseLayout(phases, 'log');
    // Sim takes nearly all space in uniform mode.
    expect(uniform[1].xEnd - uniform[1].xStart).toBeGreaterThan(0.99);
    // In log mode, Sim still dominates but less aggressively — leaves room for Input/Output.
    const simShareLog = log[1].xEnd - log[1].xStart;
    expect(simShareLog).toBeLessThan(0.95);
    expect(simShareLog).toBeGreaterThan(0.4);
  });
});

describe('computePhaseLayout — invariants', () => {
  const phases = [
    { name: 'A', wallClockUs: 100 },
    { name: 'B', wallClockUs: 5_000 },
    { name: 'C', wallClockUs: 250 },
    { name: 'D', wallClockUs: 0 },
  ];

  it.each(['uniform', 'equal', 'log'] as const)('%s mode: segments are contiguous + cover [0, 1]', (mode) => {
    const result = computePhaseLayout(phases, mode);
    expect(result[0].xStart).toBe(0);
    expect(result[result.length - 1].xEnd).toBe(1);
    for (let i = 0; i < result.length - 1; i++) {
      expect(result[i + 1].xStart).toBe(result[i].xEnd);
      expect(result[i].xEnd).toBeGreaterThanOrEqual(result[i].xStart);
    }
  });
});

describe('applyPhaseCollapse', () => {
  const segs = computePhaseLayout(
    [
      { name: 'A', wallClockUs: 950 },
      { name: 'B', wallClockUs: 30 },  // < 5% of 1000 → auto-collapses
      { name: 'C', wallClockUs: 20 },  // also auto-collapses
    ],
    'uniform',
  );

  it('returns input unchanged when nothing collapses', () => {
    const out = applyPhaseCollapse(segs, 1000, new Set(), new Set(['B', 'C']));
    expect(out).toBe(segs);  // reference-equal: pure short-circuit
  });

  it('auto-collapses phases below threshold', () => {
    const out = applyPhaseCollapse(segs, 1000, new Set(), new Set());
    const widthB = out[1].xEnd - out[1].xStart;
    const widthC = out[2].xEnd - out[2].xStart;
    expect(widthB).toBeCloseTo(COLLAPSED_SEGMENT_WIDTH, 5);
    expect(widthC).toBeCloseTo(COLLAPSED_SEGMENT_WIDTH, 5);
    expect(out[0].xEnd).toBeCloseTo(1 - 2 * COLLAPSED_SEGMENT_WIDTH, 5);
    // Last segment locks to 1 to avoid the right-edge floating-point sliver.
    expect(out[out.length - 1].xEnd).toBe(1);
  });

  it('manuallyExpanded overrides auto-collapse', () => {
    const out = applyPhaseCollapse(segs, 1000, new Set(), new Set(['B']));
    const widthB = out[1].xEnd - out[1].xStart;
    expect(widthB).toBeGreaterThan(COLLAPSED_SEGMENT_WIDTH * 2);  // B retains its share
  });

  it('manuallyCollapsed overrides "wide phase wants to be expanded"', () => {
    // Topology where only one phase auto-collapses; manually collapsing the wide one shrinks it to the strip
    // width and the released width flows to the still-expanded phases.
    const wideCSegs = computePhaseLayout(
      [{ name: 'A', wallClockUs: 950 }, { name: 'B', wallClockUs: 30 }, { name: 'C', wallClockUs: 200 }],
      'uniform',
    );
    const out = applyPhaseCollapse(wideCSegs, 1180, new Set(['A']), new Set());
    const widthA = out[0].xEnd - out[0].xStart;
    expect(widthA).toBeCloseTo(COLLAPSED_SEGMENT_WIDTH, 5);
    // Two collapsed (A, B), C left expanded → C absorbs released width.
    const widthC = out[2].xEnd - out[2].xStart;
    expect(widthC).toBeCloseTo(1 - 2 * COLLAPSED_SEGMENT_WIDTH, 5);
  });

  it('every-segment-collapsed degenerate case: each gets 1/N', () => {
    const out = applyPhaseCollapse(segs, 1000, new Set(['A']), new Set());
    // A manually + B/C auto = all three collapsed. Width spec falls through to 1/N each.
    expect(out[0].xEnd - out[0].xStart).toBeCloseTo(1 / 3, 5);
    expect(out[1].xEnd - out[1].xStart).toBeCloseTo(1 / 3, 5);
    expect(out[2].xEnd - out[2].xStart).toBeCloseTo(1 / 3, 5);
  });

  it('AUTO_COLLAPSE_THRESHOLD is 5% per design D10', () => {
    expect(AUTO_COLLAPSE_THRESHOLD).toBe(0.05);
  });
});

describe('tickOffsetToNormalizedX', () => {
  const axis: PhaseAxis = {
    segments: [
      { name: 'A', wallClockUs: 1000, xStart: 0,   xEnd: 0.6 },
      { name: 'B', wallClockUs: 500,  xStart: 0.6, xEnd: 1   },
    ],
    tickPhaseSpans: new Map([
      [10, new Map([
        ['A', { startUs: 0,    endUs: 1000 }],
        ['B', { startUs: 1000, endUs: 1500 }],
      ])],
    ]),
  };

  it('returns null for unknown axis', () => {
    expect(tickOffsetToNormalizedX(null, 10, 'A', 0, 100)).toBeNull();
  });

  it('returns null for unknown phase', () => {
    expect(tickOffsetToNormalizedX(axis, 10, 'Z', 0, 100)).toBeNull();
  });

  it('maps a bar at start of phase A to the segment start', () => {
    const out = tickOffsetToNormalizedX(axis, 10, 'A', 0, 100);
    expect(out).not.toBeNull();
    expect(out!.xStart).toBeCloseTo(0, 5);
    expect(out!.xEnd).toBeCloseTo(0.06, 5); // 100/1000 * 0.6
  });

  it('maps a bar at end of phase B to the segment end', () => {
    const out = tickOffsetToNormalizedX(axis, 10, 'B', 1400, 1500);
    expect(out).not.toBeNull();
    expect(out!.xStart).toBeCloseTo(0.92, 5); // (1400-1000)/500 * 0.4 + 0.6
    expect(out!.xEnd).toBeCloseTo(1.0, 5);
  });

  it('clamps a bar that extends beyond the phase span', () => {
    const out = tickOffsetToNormalizedX(axis, 10, 'A', -50, 1500);
    expect(out!.xStart).toBeGreaterThanOrEqual(0);
    expect(out!.xEnd).toBeLessThanOrEqual(0.6);
  });

  it('falls back to the full segment when tick has no recorded span', () => {
    const out = tickOffsetToNormalizedX(axis, 999, 'A', 0, 100);
    expect(out!.xStart).toBe(0);
    expect(out!.xEnd).toBe(0.6);
  });

  it('returns a zero-width point for collapsed segments', () => {
    const collapsedAxis: PhaseAxis = {
      segments: [{ name: 'A', wallClockUs: 0, xStart: 0.5, xEnd: 0.5 }],
      tickPhaseSpans: axis.tickPhaseSpans,
    };
    const out = tickOffsetToNormalizedX(collapsedAxis, 10, 'A', 0, 100);
    expect(out!.xStart).toBe(0.5);
    expect(out!.xEnd).toBe(0.5);
  });
});
