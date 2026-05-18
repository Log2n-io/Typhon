import { describe, expect, it } from 'vitest';
import {
  contiguousRuns,
  fillDensity,
  fragmentationPercent,
  freeSpaceComposition,
  segmentReclaimableBytes,
} from '../dbMapMetrics';
import { DbPageType, PAGE_SIZE, type DbDetailTile, type DbMapData } from '../types';

function makeTile(firstPage: number, used: number[], total: number[]): DbDetailTile {
  const n = used.length;
  return {
    node: Math.floor(firstPage / 1024),
    firstPage,
    pageCount: n,
    fillRatio: new Uint8Array(n),
    changeRevision: new Int32Array(n),
    crcStatus: new Uint8Array(n),
    residency: new Uint8Array(n),
    chunkUsed: Uint16Array.from(used),
    chunkTotal: Uint16Array.from(total),
    maxChangeRevision: 1,
    entropy: new Uint8Array(n),
    byteClass: new Uint8Array(n),
  };
}

function makeData(pageType: Uint8Array): DbMapData {
  return {
    databaseName: 'test',
    dataFileBytes: pageType.length * PAGE_SIZE,
    pageCount: pageType.length,
    walBytes: 0,
    hilbertOrder: 4,
    checkpointLsn: 0,
    detailTileSize: 1024,
    segments: [],
    pageType,
    ownerSegmentId: new Uint16Array(pageType.length),
  };
}

describe('fragmentationPercent', () => {
  it('is 0 for a perfectly contiguous segment', () => {
    expect(fragmentationPercent([10, 11, 12, 13])).toBe(0);
  });

  it('is 1 when every directory successor is scattered', () => {
    expect(fragmentationPercent([0, 5, 2, 9])).toBe(1);
  });

  it('counts the fraction of broken successor pairs', () => {
    expect(fragmentationPercent([0, 1, 3, 4])).toBeCloseTo(1 / 3, 6);
  });

  it('is 0 for a trivial segment', () => {
    expect(fragmentationPercent([7])).toBe(0);
    expect(fragmentationPercent([])).toBe(0);
  });
});

describe('contiguousRuns', () => {
  it('splits a directory-ordered page list into contiguous runs', () => {
    expect(contiguousRuns([0, 1, 2, 5, 6, 9])).toEqual([3, 2, 1]);
  });

  it('is empty for an empty list', () => {
    expect(contiguousRuns([])).toEqual([]);
  });
});

describe('fillDensity', () => {
  it('aggregates used ÷ total over resident tiles', () => {
    const tiles = new Map([[0, makeTile(0, [4, 6], [8, 8])]]);
    const d = fillDensity([0, 1], tiles, 1024);
    expect(d.value).toBeCloseTo(10 / 16, 6);
    expect(d.sampledPages).toBe(2);
  });

  it('skips pages with no resident tile', () => {
    const d = fillDensity([0, 1], new Map(), 1024);
    expect(d.sampledPages).toBe(0);
    expect(d.value).toBe(0);
  });
});

describe('segmentReclaimableBytes', () => {
  it('is free chunk slots × stride', () => {
    const tiles = new Map([[0, makeTile(0, [2, 3], [8, 8])]]);
    const r = segmentReclaimableBytes([0, 1], tiles, 1024, 64);
    expect(r.value).toBe((6 + 5) * 64);
    expect(r.sampledPages).toBe(2);
  });
});

describe('freeSpaceComposition', () => {
  it('splits the file into parts that sum exactly to the file size', () => {
    const data = makeData(
      Uint8Array.from([
        DbPageType.Root,
        DbPageType.Occupancy,
        DbPageType.Component,
        DbPageType.Component,
        DbPageType.Free,
      ]),
    );
    const c = freeSpaceComposition(data);
    expect(c.totalBytes).toBe(5 * PAGE_SIZE);
    expect(c.liveBytes + c.overheadBytes + c.freeBytes).toBe(c.totalBytes);
    expect(c.freeBytes).toBe(PAGE_SIZE);
    expect(c.overheadBytes).toBe(2 * PAGE_SIZE);
    expect(c.liveBytes).toBe(2 * PAGE_SIZE);
  });
});
