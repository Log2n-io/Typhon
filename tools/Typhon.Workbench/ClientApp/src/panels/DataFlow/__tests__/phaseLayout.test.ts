import { describe, expect, it } from 'vitest';
import { computePhaseLayout } from '../phaseLayout';

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
