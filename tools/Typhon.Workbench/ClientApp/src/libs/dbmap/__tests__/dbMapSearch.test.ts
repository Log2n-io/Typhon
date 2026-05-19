import { describe, expect, it } from 'vitest';
import { searchDbMap } from '../dbMapSearch';
import { NO_SEGMENT, PAGE_SIZE, type DbMapData, type StorageSegmentDto } from '../types';

const SEGMENTS: StorageSegmentDto[] = [
  { id: 0, rootPageIndex: 1, kind: 'Occupancy', pageCount: 1, typeName: '' },
  { id: 1, rootPageIndex: 5, kind: 'Component', pageCount: 3, typeName: 'Position' },
  { id: 2, rootPageIndex: 10, kind: 'Index', pageCount: 2, typeName: '' },
];

function makeData(): DbMapData {
  const owner = new Uint16Array(16).fill(NO_SEGMENT);
  owner[5] = 1;
  owner[6] = 1;
  owner[7] = 1;
  owner[10] = 2;
  owner[11] = 2;
  return {
    databaseName: 'test',
    dataFileBytes: 16 * PAGE_SIZE,
    pageCount: 16,
    downSampleFactor: 1,
    walBytes: 0,
    hilbertOrder: 2,
    checkpointLsn: 0,
    detailTileSize: 1024,
    segments: SEGMENTS,
    pageType: new Uint8Array(16),
    ownerSegmentId: owner,
  };
}

describe('searchDbMap', () => {
  const data = makeData();

  it('resolves page:N, #N and a bare integer to the same page', () => {
    expect(searchDbMap('page:6', data).map((m) => m.pageIndex)).toEqual([6]);
    expect(searchDbMap('#6', data).map((m) => m.pageIndex)).toEqual([6]);
    expect(searchDbMap('6', data).map((m) => m.pageIndex)).toEqual([6]);
  });

  it('rejects an out-of-range page', () => {
    expect(searchDbMap('page:99999', data)).toEqual([]);
  });

  it('resolves segment:N / seg:N to the segment pages in file order', () => {
    expect(searchDbMap('segment:1', data).map((m) => m.pageIndex)).toEqual([5, 6, 7]);
    expect(searchDbMap('seg:2', data).map((m) => m.pageIndex)).toEqual([10, 11]);
  });

  it('resolves chunk:S:C to the owning segment root page', () => {
    expect(searchDbMap('chunk:1:7', data).map((m) => m.pageIndex)).toEqual([5]);
  });

  it('matches a component type name case-insensitively', () => {
    expect(searchDbMap('position', data).map((m) => m.pageIndex)).toEqual([5]);
  });

  it('matches a segment kind as free text', () => {
    expect(searchDbMap('index', data).map((m) => m.pageIndex)).toEqual([10]);
  });

  it('returns nothing for an empty query', () => {
    expect(searchDbMap('   ', data)).toEqual([]);
  });
});
