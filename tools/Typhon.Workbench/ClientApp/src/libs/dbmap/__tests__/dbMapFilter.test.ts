import { describe, expect, it } from 'vitest';
import { buildFilterMask } from '../dbMapFilter';
import { DbPageType, PAGE_SIZE, type DbMapData } from '../types';

// Covers the filter-to-dim mask builder (Module 15, A4 — §4.6 / §13 A4 AC4).

function makeData(types: number[]): DbMapData {
  return {
    databaseName: 'test',
    dataFileBytes: types.length * PAGE_SIZE,
    pageCount: types.length,
    downSampleFactor: 1,
    walBytes: 0,
    hilbertOrder: 4,
    checkpointLsn: 0,
    detailTileSize: 1024,
    segments: [],
    pageType: Uint8Array.from(types),
    ownerSegmentId: new Uint16Array(types.length),
    pageRank: new Uint8Array(types.length),
  };
}

describe('buildFilterMask', () => {
  it('returns null for a null filter — there is nothing to dim', () => {
    expect(buildFilterMask(makeData([DbPageType.Free, DbPageType.Component]), null)).toBeNull();
  });

  it('returns null when the filter selects no page type', () => {
    expect(buildFilterMask(makeData([DbPageType.Free]), { pageTypes: [] })).toBeNull();
  });

  it('passes only cells whose page type is in the filter', () => {
    const data = makeData([DbPageType.Free, DbPageType.Component, DbPageType.Index, DbPageType.Component]);
    const mask = buildFilterMask(data, { pageTypes: [DbPageType.Component] });
    expect(mask).not.toBeNull();
    expect(Array.from(mask!)).toEqual([0, 1, 0, 1]);
  });

  it('passes a cell matching any of several selected types', () => {
    const data = makeData([DbPageType.Free, DbPageType.Component, DbPageType.Index]);
    const mask = buildFilterMask(data, { pageTypes: [DbPageType.Component, DbPageType.Index] });
    expect(Array.from(mask!)).toEqual([0, 1, 1]);
  });
});
