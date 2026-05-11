import { describe, expect, it } from 'vitest';
import type { QueryExecutionDto } from '@/api/generated/model/queryExecutionDto';
import type { QueryExecutionPhaseDto } from '@/api/generated/model/queryExecutionPhaseDto';
import { buildPhaseRows, formatCount, formatDelta, formatNs, totalWallNs } from '../phaseRows';

function phase(opts: {
  name?: string;
  estimate?: number | null;
  actual?: number | null;
  wallNs?: number;
  notes?: string | null;
}): QueryExecutionPhaseDto {
  return {
    phaseName: opts.name ?? 'Phase',
    estimate: opts.estimate === undefined ? null : opts.estimate,
    actual: opts.actual === undefined ? null : opts.actual,
    wallNs: opts.wallNs ?? 0,
    notes: opts.notes === undefined ? null : opts.notes,
  };
}

function execution(phases: QueryExecutionPhaseDto[]): QueryExecutionDto {
  return {
    definitionId: { kind: 0, localId: 1 },
    spanId: 0,
    parentSpanId: 0,
    tickIndex: 0,
    systemId: -1,
    startTs: 0,
    endTs: 0,
    args: null,
    phases,
  };
}

describe('buildPhaseRows', () => {
  it('null execution → empty', () => {
    expect(buildPhaseRows(null)).toEqual([]);
  });

  it('empty phases array → empty', () => {
    expect(buildPhaseRows(execution([]))).toEqual([]);
  });

  it('preserves trace order', () => {
    const rows = buildPhaseRows(execution([
      phase({ name: 'Parse' }),
      phase({ name: 'DNF' }),
      phase({ name: 'Plan' }),
    ]));
    expect(rows.map((r) => r.phaseName)).toEqual(['Parse', 'DNF', 'Plan']);
  });

  it('computes Δ as (actual - estimate) / estimate', () => {
    const rows = buildPhaseRows(execution([
      phase({ name: 'IndexScan', estimate: 1200, actual: 934 }),
    ]));
    expect(rows[0].delta).toBeCloseTo((934 - 1200) / 1200, 5);
  });

  it('Δ is null when estimate is missing', () => {
    const rows = buildPhaseRows(execution([phase({ actual: 50 })]));
    expect(rows[0].delta).toBeNull();
  });

  it('Δ is null when actual is missing', () => {
    const rows = buildPhaseRows(execution([phase({ estimate: 1000 })]));
    expect(rows[0].delta).toBeNull();
  });

  it('Δ is null when estimate is zero (avoid divide-by-zero)', () => {
    const rows = buildPhaseRows(execution([phase({ estimate: 0, actual: 0 })]));
    expect(rows[0].delta).toBeNull();
  });

  it('phase notes propagate', () => {
    const rows = buildPhaseRows(execution([phase({ name: 'Sort', notes: 'spilled' })]));
    expect(rows[0].notes).toBe('spilled');
  });

  it('phaseName falls back to <unnamed> when null', () => {
    const rows = buildPhaseRows(execution([phase({ name: undefined as unknown as string })]));
    expect(rows[0].phaseName).toBeDefined();
  });
});

describe('totalWallNs', () => {
  it('sums wallNs across all rows', () => {
    const rows = buildPhaseRows(execution([
      phase({ wallNs: 1200 }),
      phase({ wallNs: 400 }),
      phase({ wallNs: 3100 }),
    ]));
    expect(totalWallNs(rows)).toBe(4700);
  });

  it('returns 0 for empty', () => {
    expect(totalWallNs([])).toBe(0);
  });
});

describe('formatNs', () => {
  it('formats nanoseconds with adaptive precision', () => {
    expect(formatNs(0)).toBe('0');
    expect(formatNs(500)).toBe('500 ns');
    expect(formatNs(1_500)).toBe('1.5 µs');
    expect(formatNs(1_500_000)).toBe('1.50 ms');
    expect(formatNs(2_500_000_000)).toBe('2.50 s');
  });
});

describe('formatDelta', () => {
  it('renders negative percentages with sign', () => {
    expect(formatDelta(-0.22)).toBe('-22%');
  });
  it('renders positive percentages with explicit +', () => {
    expect(formatDelta(0.15)).toBe('+15%');
  });
  it('renders null as em-dash', () => {
    expect(formatDelta(null)).toBe('—');
  });
});

describe('formatCount', () => {
  it('null → em-dash', () => {
    expect(formatCount(null)).toBe('—');
  });
  it('formats with locale separators', () => {
    expect(formatCount(1_234_567)).toBe('1,234,567');
  });
});
