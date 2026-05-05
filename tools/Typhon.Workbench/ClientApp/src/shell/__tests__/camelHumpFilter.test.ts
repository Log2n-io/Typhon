import { describe, expect, it } from 'vitest';
import { humpFilter, matchHighlight } from '../camelHumpFilter';

// "Toggle View Resource Tree"
// T(0)oggle V(7)iew R(12)esource T(21)ree
const TVRT = 'Toggle View Resource Tree';

// "Toggle View Component Browser"
// T(0)oggle V(7)iew C(12)omponent B(22)rowser
const TVCB = 'Toggle View Component Browser';

describe('matchHighlight', () => {
  it('returns empty array for empty query', () => {
    expect(matchHighlight(TVRT, '')).toEqual([]);
  });

  it('TVRT hump-matches Toggle View Resource Tree at word starts', () => {
    expect(matchHighlight(TVRT, 'TVRT')).toEqual([0, 7, 12, 21]);
  });

  it('tvrt (lowercase) hump-matches same positions', () => {
    expect(matchHighlight(TVRT, 'tvrt')).toEqual([0, 7, 12, 21]);
  });

  it('TV prefix hump-matches first two word starts', () => {
    expect(matchHighlight(TVRT, 'TV')).toEqual([0, 7]);
  });

  it('TVCB hump-matches Toggle View Component Browser', () => {
    expect(matchHighlight(TVCB, 'TVCB')).toEqual([0, 7, 12, 22]);
  });

  it('TVRT does not hump-match Toggle View Component Browser (no R initial)', () => {
    // Falls back to substring — "resource" not in TVCB, "tvrt" not a substring either
    expect(matchHighlight(TVCB, 'TVRT')).toBeNull();
  });

  it('substring "toggle" matches as consecutive run', () => {
    expect(matchHighlight(TVRT, 'toggle')).toEqual([0, 1, 2, 3, 4, 5]);
  });

  it('substring match is case-insensitive', () => {
    expect(matchHighlight(TVRT, 'VIEW')).toEqual([7, 8, 9, 10]);
  });

  it('returns null when neither hump nor substring matches', () => {
    expect(matchHighlight(TVRT, 'xyz')).toBeNull();
  });

  it('single-char hump match works', () => {
    expect(matchHighlight(TVRT, 'T')).toEqual([0]);
  });
});

describe('humpFilter', () => {
  const kw = (label: string, extra = '') => [label, extra];

  it('returns 1 for empty search', () => {
    expect(humpFilter('id', '', kw(TVRT))).toBe(1);
  });

  it('returns 1.0 for hump match', () => {
    expect(humpFilter('id', 'TVRT', kw(TVRT))).toBe(1.0);
  });

  it('returns 1.0 for lowercase hump match', () => {
    expect(humpFilter('id', 'tvrt', kw(TVRT))).toBe(1.0);
  });

  it('returns 0.8 for label substring match', () => {
    expect(humpFilter('id', 'toggle', kw(TVRT))).toBe(0.8);
  });

  it('returns 0.5 for keyword-only match', () => {
    expect(humpFilter('id', 'inspector', kw('Toggle View Component Browser', 'schema components inspector'))).toBe(0.5);
  });

  it('returns 0 for no match', () => {
    expect(humpFilter('id', 'xyz', kw(TVRT, 'some keywords'))).toBe(0);
  });

  it('TVRT does not match Toggle View Component Browser', () => {
    expect(humpFilter('id', 'TVRT', kw(TVCB))).toBe(0);
  });

  it('TV matches both TVRT and TVCB labels', () => {
    expect(humpFilter('id', 'TV', kw(TVRT))).toBe(1.0);
    expect(humpFilter('id', 'TV', kw(TVCB))).toBe(1.0);
  });
});
