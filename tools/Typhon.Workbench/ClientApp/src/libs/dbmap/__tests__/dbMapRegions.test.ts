import { describe, expect, it } from 'vitest';
import { buildRegions, sortRegions, type DbMapRegion } from '../dbMapRegions';
import { DbPageType, NO_SEGMENT, PAGE_SIZE, type DbMapData } from '../types';

function makeData(pageType: number[], ownerSegmentId: number[]): DbMapData {
  return {
    databaseName: 'test',
    dataFileBytes: pageType.length * PAGE_SIZE,
    pageCount: pageType.length,
    downSampleFactor: 1,
    walBytes: 0,
    hilbertOrder: 4,
    checkpointLsn: 0,
    detailTileSize: 1024,
    segments: [],
    pageType: Uint8Array.from(pageType),
    ownerSegmentId: Uint16Array.from(ownerSegmentId),
  };
}

describe('buildRegions', () => {
  it('coalesces contiguous same-type, same-segment runs', () => {
    const data = makeData(
      [DbPageType.Root, DbPageType.Component, DbPageType.Component, DbPageType.Component, DbPageType.Free],
      [NO_SEGMENT, 1, 1, 2, NO_SEGMENT],
    );
    const regions = buildRegions(data, new Map());
    // Root | seg-1 Component (×2) | seg-2 Component | Free — a segment change breaks the run.
    expect(regions.map((r) => [r.startPage, r.pageCount])).toEqual([
      [0, 1],
      [1, 2],
      [3, 1],
      [4, 1],
    ]);
    expect(regions[1].byteSize).toBe(2 * PAGE_SIZE);
    expect(regions[1].ownerSegmentId).toBe(1);
  });

  it('is empty for an empty map', () => {
    expect(buildRegions(makeData([], []), new Map())).toEqual([]);
  });
});

describe('sortRegions', () => {
  const regions: DbMapRegion[] = [
    { startPage: 0, pageCount: 3, byteSize: 0, pageType: 1, ownerSegmentId: 1, fillAvg: 0.2 },
    { startPage: 3, pageCount: 1, byteSize: 0, pageType: 2, ownerSegmentId: 1, fillAvg: 0.9 },
    { startPage: 4, pageCount: 9, byteSize: 0, pageType: 1, ownerSegmentId: 2, fillAvg: null },
  ];

  it('sorts by size descending', () => {
    expect(sortRegions(regions, 'size', false).map((r) => r.startPage)).toEqual([4, 0, 3]);
  });

  it('sorts by fill ascending', () => {
    expect(sortRegions(regions, 'fill', true).map((r) => r.startPage)).toEqual([4, 0, 3]);
  });

  it('ranks fragmentation by the owning segment shard count', () => {
    // Segment 1 has two regions, segment 2 has one — segment 1's shards float to the top descending.
    expect(sortRegions(regions, 'fragmentation', false).map((r) => r.startPage)).toEqual([0, 3, 4]);
  });

  it('does not mutate the input', () => {
    sortRegions(regions, 'size', false);
    expect(regions[0].startPage).toBe(0);
  });
});
