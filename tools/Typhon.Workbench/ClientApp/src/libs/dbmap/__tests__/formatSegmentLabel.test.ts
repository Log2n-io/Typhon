import { describe, expect, it } from 'vitest';
import { formatSegmentLabel } from '../types';

// Regression: File Map segment labels dropped the segment KIND once the short-name labeller resolved owners (they
// showed a bare "ClIdxUnit" instead of "Index · ClIdxUnit"). The kind must always prefix the rest. All label sites
// (canvas run labels, L0 stripes, Regions + Legend sidebars) route through this one formatter so they can't drift.
describe('formatSegmentLabel', () => {
  // A stand-in for the Query Console labeller: take the last dotted segment as the short name.
  const shorten = (typeName: string) => typeName.split('.').pop() ?? typeName;

  it('prefixes the kind before the resolved short name, space-separated (no dash, no id)', () => {
    expect(formatSegmentLabel('Index', 83, 'Typhon.Test.ClIdx.ClIdxUnit', shorten)).toBe('Index ClIdxUnit');
    expect(formatSegmentLabel('Cluster', 100, 'Game.ClMigUnit', shorten)).toBe('Cluster ClMigUnit');
  });

  it('falls back to "<Kind> #<id>" when the owner type does not resolve (empty typeName)', () => {
    expect(formatSegmentLabel('Index', 83, '', shorten)).toBe('Index #83');
    expect(formatSegmentLabel('Revision', 42, '', shorten)).toBe('Revision #42');
  });

  it('always keeps the kind visible — never returns the bare short name', () => {
    const label = formatSegmentLabel('EntityMap', 7, 'A.B.Unit', shorten);
    expect(label.startsWith('EntityMap')).toBe(true);
    expect(label).not.toBe('Unit');
  });
});
