import { describe, expect, it } from 'vitest';
import { resolveFrameRootForSite } from '@/libs/profiler/resolveFrameRoot';
import type { CpuFrameSymbol } from '@/stores/useCpuFrameStore';
import type { ResolvedSourceLocation } from '@/stores/useSourceLocationStore';

/**
 * Pure-logic tests for `resolveFrameRootForSite` — mapping a span / chunk source site to the CPU frame
 * of its enclosing method by file + line containment (#351 §8.2).
 */

function frame(over: Partial<CpuFrameSymbol>): CpuFrameSymbol {
  return { frameId: 0, method: 'M', file: 'a.cs', line: 1, categoryId: -1, ...over };
}

function site(over: Partial<ResolvedSourceLocation>): ResolvedSourceLocation {
  return { file: 'a.cs', line: 50, method: 'M', kind: 0, ...over };
}

function frameMap(...frames: CpuFrameSymbol[]): Map<number, CpuFrameSymbol> {
  return new Map(frames.map((f) => [f.frameId, f]));
}

describe('resolveFrameRootForSite', () => {
  it('returns null for a null site', () => {
    expect(resolveFrameRootForSite(null, frameMap(frame({ frameId: 1 })))).toBeNull();
  });

  it('returns null when the site has no resolved line', () => {
    expect(resolveFrameRootForSite(site({ line: 0 }), frameMap(frame({ frameId: 1, line: 1 })))).toBeNull();
  });

  it('matches a frame whose method-entry line is at or before the site line', () => {
    const result = resolveFrameRootForSite(site({ line: 50 }), frameMap(frame({ frameId: 7, line: 40 })));
    expect(result).toBe(7);
  });

  it('picks the deepest enclosing method — greatest entry line ≤ site line', () => {
    // Three methods in a.cs starting at lines 10 / 40 / 90; the site at line 55 sits inside the 40-method.
    const result = resolveFrameRootForSite(
      site({ file: 'a.cs', line: 55 }),
      frameMap(
        frame({ frameId: 1, file: 'a.cs', line: 10 }),
        frame({ frameId: 2, file: 'a.cs', line: 40 }),
        frame({ frameId: 3, file: 'a.cs', line: 90 }),
      ),
    );
    expect(result).toBe(2);
  });

  it('ignores frames in a different file', () => {
    const result = resolveFrameRootForSite(
      site({ file: 'a.cs', line: 50 }),
      frameMap(frame({ frameId: 1, file: 'b.cs', line: 10 })),
    );
    expect(result).toBeNull();
  });

  it('ignores frames whose method starts after the site, and source-less frames (line 0)', () => {
    const result = resolveFrameRootForSite(
      site({ file: 'a.cs', line: 50 }),
      frameMap(
        frame({ frameId: 1, file: 'a.cs', line: 80 }), // starts after the site
        frame({ frameId: 2, file: 'a.cs', line: 0 }),  // BCL / native — no source
      ),
    );
    expect(result).toBeNull();
  });

  it('matches when the method entry line equals the site line', () => {
    const result = resolveFrameRootForSite(site({ line: 30 }), frameMap(frame({ frameId: 5, line: 30 })));
    expect(result).toBe(5);
  });
});
