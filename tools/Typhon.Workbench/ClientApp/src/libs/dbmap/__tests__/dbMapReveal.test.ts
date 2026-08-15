import { describe, expect, it } from 'vitest';
import { resolveRevealTarget, type RevealSegment } from '../dbMapReveal';

/**
 * #619 §4.2 — what a "Reveal in File Map" actually frames.
 *
 * The pre-#619 effect did `segments.find(...)`: one segment, always. An archetype owns up to three, so the design's
 * "`Unit`'s segments" could not be expressed — and the Archetype Inspector's button, which passed the archetype's
 * first *component*, matched nothing at all for a cluster archetype and silently did nothing.
 */

const seg = (id: number, kind: string, typeName: string, rootPageIndex: number, pageCount: number): RevealSegment =>
  ({ id, kind, typeName, rootPageIndex, pageCount });

const SEGMENTS: RevealSegment[] = [
  seg(0, 'Cluster', 'Swg.Shard.Unit', 100, 1_200),
  seg(1, 'EntityMap', 'Swg.Shard.Unit', 9_000, 4),
  seg(2, 'Index', 'Swg.Shard.Unit', 9_100, 6),
  seg(3, 'Component', 'Swg.Shard.Transform', 20_000, 900),
  seg(4, 'Cluster', 'Swg.Shard.Structure', 40_000, 50),
];

describe('resolveRevealTarget (#619 §4.2)', () => {
  it('frames every segment an archetype owns, not just the first', () => {
    const t = resolveRevealTarget({ kind: 'archetype', name: 'Swg.Shard.Unit' }, SEGMENTS);

    expect(t.segments.map((s) => s.id)).toEqual([0, 1, 2]);
    expect(t.label).toBe('Archetype Swg.Shard.Unit');
  });

  it('leaves shared component tables out of an archetype reveal', () => {
    // Transform's table is used by every archetype carrying Transform. Framing it here would put another
    // archetype's pages inside a box captioned with this one's name.
    const t = resolveRevealTarget({ kind: 'archetype', name: 'Swg.Shard.Unit' }, SEGMENTS);

    expect(t.segments.some((s) => s.typeName === 'Swg.Shard.Transform')).toBe(false);
  });

  it('does not frame another archetype’s segments', () => {
    const t = resolveRevealTarget({ kind: 'archetype', name: 'Swg.Shard.Unit' }, SEGMENTS);

    expect(t.segments.some((s) => s.typeName === 'Swg.Shard.Structure')).toBe(false);
  });

  it('picks the largest segment to select and pulse', () => {
    // The cluster rows in every real case — the entity map and index are a handful of pages and pulsing one of
    // those would draw the eye away from where the data is.
    const t = resolveRevealTarget({ kind: 'archetype', name: 'Swg.Shard.Unit' }, SEGMENTS);

    expect(t.primary?.id).toBe(0);
  });

  it('a component reveal still resolves to exactly one segment', () => {
    const t = resolveRevealTarget({ kind: 'component', name: 'Swg.Shard.Transform' }, SEGMENTS);

    expect(t.segments.map((s) => s.id)).toEqual([3]);
    expect(t.primary?.id).toBe(3);
    expect(t.label).toBe('Component Swg.Shard.Transform');
  });

  it('an archetype the file has no segment for resolves to nothing — the camera stays put', () => {
    const t = resolveRevealTarget({ kind: 'archetype', name: 'Swg.Shard.Vanished' }, SEGMENTS);

    expect(t.segments).toEqual([]);
    expect(t.primary).toBeNull();
    expect(t.label).toBe('');
  });

  it('a null request, or an unloaded segment table, resolves to nothing', () => {
    expect(resolveRevealTarget(null, SEGMENTS).primary).toBeNull();
    expect(resolveRevealTarget({ kind: 'archetype', name: 'Swg.Shard.Unit' }, []).primary).toBeNull();
    expect(resolveRevealTarget({ kind: 'archetype', name: 'Swg.Shard.Unit' }, null).primary).toBeNull();
  });
});
