import { describe, it, expect } from 'vitest';
import { sortHealthSegments, type HealthSortKey } from '../storageHealthModel';
import type { HealthSegment } from '@/hooks/dbmap/useDbMapHealth';

function seg(over: Partial<HealthSegment> & { id: number }): HealthSegment {
  return {
    kind: 'Cluster',
    typeName: '',
    pageCount: 10,
    allocatedChunkCount: 5,
    chunkCapacity: 10,
    chunkFillPct: 50,
    reclaimableBytes: 1000,
    entityCount: 100,
    occupancyPct: 50,
    ...over,
  };
}

describe('sortHealthSegments', () => {
  const rows = [
    seg({ id: 1, occupancyPct: 30, reclaimableBytes: 500 }),
    seg({ id: 2, occupancyPct: 90, reclaimableBytes: 100 }),
    seg({ id: 3, occupancyPct: 60, reclaimableBytes: 900 }),
  ];

  it('sorts numerically descending (worst offenders first)', () => {
    expect(sortHealthSegments(rows, 'occupancyPct', 'desc').map((s) => s.id)).toEqual([2, 3, 1]);
  });

  it('sorts numerically ascending', () => {
    expect(sortHealthSegments(rows, 'reclaimableBytes', 'asc').map((s) => s.id)).toEqual([2, 1, 3]);
  });

  it('sorts strings lexically', () => {
    const named = [seg({ id: 1, typeName: 'Zeta' }), seg({ id: 2, typeName: 'Alpha' })];
    expect(sortHealthSegments(named, 'typeName' as HealthSortKey, 'asc').map((s) => s.id)).toEqual([2, 1]);
  });

  it('does not mutate the input', () => {
    const copy = [...rows];
    sortHealthSegments(rows, 'occupancyPct', 'desc');
    expect(rows).toEqual(copy);
  });
});
