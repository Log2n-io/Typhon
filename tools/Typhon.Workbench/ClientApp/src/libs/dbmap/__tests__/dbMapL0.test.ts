import { describe, expect, it } from 'vitest';
import { computeComposition, L0_SEGMENT_TOP_N } from '../dbMapL0';
import { FREE_RGB, PAGE_TYPE_RGB, USED_RGB } from '../dbMapColors';
import {
  DbPageType,
  NO_SEGMENT,
  PAGE_SIZE,
  type DbMapData,
  type DbMapEncoding,
  type StorageSegmentDto,
} from '../types';

function makeData(pageType: Uint8Array, ownerSegmentId?: Uint16Array, segments: StorageSegmentDto[] = []): DbMapData {
  const owners = ownerSegmentId ?? new Uint16Array(pageType.length).fill(NO_SEGMENT);
  return {
    databaseName: 'test',
    dataFileBytes: pageType.length * PAGE_SIZE,
    pageCount: pageType.length,
    downSampleFactor: 1,
    walBytes: 0,
    hilbertOrder: 4,
    checkpointLsn: 0,
    detailTileSize: 1024,
    segments,
    pageType,
    ownerSegmentId: owners,
    pageRank: new Uint8Array(pageType.length),
  };
}

function ofType(n: number, type: DbPageType): number[] {
  return new Array<number>(n).fill(type);
}

describe('computeComposition — pageType encoding', () => {
  it('emits stripes in stable order with Free last', () => {
    const types = Uint8Array.from([
      ...ofType(2, DbPageType.Root),
      ...ofType(1, DbPageType.Occupancy),
      ...ofType(4, DbPageType.Component),
      ...ofType(3, DbPageType.Index),
      ...ofType(2, DbPageType.Free),
    ]);
    const stripes = computeComposition(makeData(types), 'pageType');
    expect(stripes.map((s) => s.label)).toEqual(['Component', 'Index', 'Root', 'Occupancy', 'Free']);
    // Free is always last.
    expect(stripes[stripes.length - 1].label).toBe('Free');
  });

  it('omits zero-count types', () => {
    const types = Uint8Array.from([
      ...ofType(3, DbPageType.Component),
      ...ofType(1, DbPageType.Free),
    ]);
    const stripes = computeComposition(makeData(types), 'pageType');
    expect(stripes).toHaveLength(2);
    expect(stripes.map((s) => s.label)).toEqual(['Component', 'Free']);
  });

  it('fractions sum to 1 and byteCount sums to dataFileBytes', () => {
    const types = Uint8Array.from([
      ...ofType(7, DbPageType.Component),
      ...ofType(2, DbPageType.Index),
      ...ofType(3, DbPageType.Free),
    ]);
    const data = makeData(types);
    const stripes = computeComposition(data, 'pageType');
    const sumFrac = stripes.reduce((s, x) => s + x.fraction, 0);
    expect(sumFrac).toBeCloseTo(1, 6);
    const sumBytes = stripes.reduce((s, x) => s + x.byteCount, 0);
    expect(sumBytes).toBe(data.dataFileBytes);
  });

  it('uses the same colours as PAGE_TYPE_RGB so the L0→L1 crossfade does not flicker', () => {
    const types = Uint8Array.from(ofType(4, DbPageType.Cluster));
    const [stripe] = computeComposition(makeData(types), 'pageType');
    expect(stripe.color).toEqual(PAGE_TYPE_RGB[DbPageType.Cluster]);
  });

  it('respects the down-sample factor so bytes still sum to the real file size', () => {
    const types = Uint8Array.from([
      ...ofType(2, DbPageType.Component),
      ...ofType(2, DbPageType.Free),
    ]);
    const data = makeData(types);
    // Simulate a 4× down-sampled map of a 16-page file.
    data.downSampleFactor = 4;
    data.dataFileBytes = 16 * PAGE_SIZE;
    const stripes = computeComposition(data, 'pageType');
    const sumBytes = stripes.reduce((s, x) => s + x.byteCount, 0);
    expect(sumBytes).toBe(data.dataFileBytes);
  });
});

describe('computeComposition — detail-encoding fallback', () => {
  const detailEncodings: DbMapEncoding[] = [
    'fillDensity',
    'writeAge',
    'crc',
    'residency',
    'entropy',
    'byteClass',
  ];
  for (const enc of detailEncodings) {
    it(`falls back to the pageType layout under '${enc}'`, () => {
      const types = Uint8Array.from([
        ...ofType(3, DbPageType.Component),
        ...ofType(2, DbPageType.Free),
      ]);
      const data = makeData(types);
      const expected = computeComposition(data, 'pageType');
      const actual = computeComposition(data, enc);
      expect(actual.map((s) => s.key)).toEqual(expected.map((s) => s.key));
      expect(actual.map((s) => s.pageCount)).toEqual(expected.map((s) => s.pageCount));
    });
  }
});

describe('computeComposition — freeUsed encoding', () => {
  it('emits exactly Used then Free', () => {
    const types = Uint8Array.from([
      ...ofType(7, DbPageType.Component),
      ...ofType(3, DbPageType.Free),
    ]);
    const stripes = computeComposition(makeData(types), 'freeUsed');
    expect(stripes.map((s) => s.label)).toEqual(['Used', 'Free']);
    expect(stripes[0].color).toEqual(USED_RGB);
    expect(stripes[1].color).toEqual(FREE_RGB);
    expect(stripes[0].pageCount).toBe(7);
    expect(stripes[1].pageCount).toBe(3);
  });

  it('omits the Free stripe when nothing is free', () => {
    const types = Uint8Array.from(ofType(5, DbPageType.Component));
    const stripes = computeComposition(makeData(types), 'freeUsed');
    expect(stripes).toHaveLength(1);
    expect(stripes[0].label).toBe('Used');
  });

  it('omits the Used stripe when the whole file is free', () => {
    const types = Uint8Array.from(ofType(5, DbPageType.Free));
    const stripes = computeComposition(makeData(types), 'freeUsed');
    expect(stripes).toHaveLength(1);
    expect(stripes[0].label).toBe('Free');
  });
});

describe('computeComposition — segment encoding', () => {
  function withSegments(perSegment: number[], unowned: number, segMeta: StorageSegmentDto[] = []): DbMapData {
    const owners: number[] = [];
    perSegment.forEach((count, idx) => {
      const id = idx + 1;
      for (let i = 0; i < count; i++) {
        owners.push(id);
      }
    });
    for (let i = 0; i < unowned; i++) {
      owners.push(NO_SEGMENT);
    }
    const types = new Uint8Array(owners.length).fill(DbPageType.Component);
    for (let i = perSegment.reduce((a, b) => a + b, 0); i < owners.length; i++) {
      types[i] = DbPageType.Free;
    }
    return makeData(types, Uint16Array.from(owners), segMeta);
  }

  it('sorts segment stripes by count desc and emits the unowned stripe last', () => {
    const data = withSegments([2, 5, 1], 3);
    const stripes = computeComposition(data, 'segment');
    // Three segment stripes (id 2 has 5 pages, id 1 has 2, id 3 has 1) then Unowned.
    expect(stripes.map((s) => s.key)).toEqual(['seg:2', 'seg:1', 'seg:3', 'unowned']);
    expect(stripes[0].pageCount).toBe(5);
    expect(stripes[stripes.length - 1].label).toBe('Unowned');
  });

  it('uses the StorageSegmentDto label when available', () => {
    const data = withSegments([3], 0, [
      { id: 1, rootPageIndex: 0, kind: 'ComponentTable', pageCount: 3, typeName: 'UserRow' },
    ]);
    const stripes = computeComposition(data, 'segment');
    expect(stripes[0].label).toBe('ComponentTable UserRow');
  });

  it(`collapses past-top-${L0_SEGMENT_TOP_N} segments into one Other stripe ordered before Unowned`, () => {
    // 14 distinct segments with descending page counts.
    const counts = Array.from({ length: 14 }, (_, i) => 14 - i);
    const data = withSegments(counts, 0);
    const stripes = computeComposition(data, 'segment');
    // 12 named + 1 "Other (2)" — no unowned stripe in this fixture.
    expect(stripes).toHaveLength(L0_SEGMENT_TOP_N + 1);
    const other = stripes[stripes.length - 1];
    expect(other.key).toBe('other');
    expect(other.kind).toBe('other');
    expect(other.label).toBe('Other (2)');
    // The bucketed list is exactly the 2 smallest segments.
    expect(other.bucketed).toEqual([13, 14]);
    expect(other.pageCount).toBe(1 + 2);
  });

  it('breaks ties on segment id ascending for cross-render stability', () => {
    // Two segments with identical counts — the lower id must come first.
    const data = withSegments([3, 3], 0);
    const stripes = computeComposition(data, 'segment');
    expect(stripes[0].segmentId).toBe(1);
    expect(stripes[1].segmentId).toBe(2);
  });

  it('falls back to "segment #N" when no StorageSegmentDto is provided', () => {
    const data = withSegments([3], 0);
    const stripes = computeComposition(data, 'segment');
    expect(stripes[0].label).toBe('segment #1');
  });
});

describe('computeComposition — empty fixture', () => {
  it('returns no stripes for a zero-page map', () => {
    const empty = makeData(new Uint8Array(0));
    expect(computeComposition(empty, 'pageType')).toEqual([]);
    expect(computeComposition(empty, 'freeUsed')).toEqual([]);
    expect(computeComposition(empty, 'segment')).toEqual([]);
  });
});
