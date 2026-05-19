import { describe, expect, it } from 'vitest';
import { regionsToCsv } from '../dbMapExport';
import { NO_SEGMENT } from '../types';
import type { DbMapRegion } from '../dbMapRegions';

// Covers the region-table CSV serialisation (Module 15, A4 — §4.6 / §13 A4 AC5).

describe('regionsToCsv', () => {
  it('emits a header row and one row per region, mapping enums and blanks', () => {
    const regions: DbMapRegion[] = [
      { startPage: 0, pageCount: 4, byteSize: 32768, pageType: 4, ownerSegmentId: 7, fillAvg: 0.5 },
      { startPage: 4, pageCount: 2, byteSize: 16384, pageType: 1, ownerSegmentId: NO_SEGMENT, fillAvg: null },
    ];
    const lines = regionsToCsv(regions).split('\n');
    expect(lines[0]).toBe('startPage,pageCount,byteSize,pageType,ownerSegmentId,fillAvgPercent');
    expect(lines).toHaveLength(3);
    // Component (type 4), owner 7, fill 0.5 → 50 %.
    expect(lines[1]).toBe('0,4,32768,Component,7,50');
    // Free (type 1), NO_SEGMENT → blank owner, null fill → blank percent.
    expect(lines[2]).toBe('4,2,16384,Free,,');
  });

  it('returns just the header for an empty region list', () => {
    expect(regionsToCsv([])).toBe('startPage,pageCount,byteSize,pageType,ownerSegmentId,fillAvgPercent');
  });
});
