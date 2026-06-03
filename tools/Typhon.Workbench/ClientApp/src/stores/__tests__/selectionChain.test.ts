import { describe, expect, it } from 'vitest';
import { resolveChain, selectionRefLabel } from '@/stores/selectionChain';
import type { SelectionLeaf, SelectionObjectType, SelectionState } from '@/stores/useSelectionStore';

// Regression for the File Map breadcrumb bug: ancestor crumbs used to carry a bare id (e.g. the pageIndex number),
// which can't round-trip through select() — the File Map detail switches on `.kind` and a number rendered the empty
// Cell fallback. resolveChain now emits the FULL structured DbMap selection on every storage crumb, so each crumb
// re-selects its own node.

const ctx = {} as unknown as SelectionState; // storage chains don't use the scalar-context fallback

function leaf(type: SelectionObjectType, ref: unknown): SelectionLeaf {
  return { type, ref, touchedAt: 0 };
}

describe('resolveChain — File Map storage chain carries re-selectable structured refs', () => {
  it('a cell leaf yields [segment, page, chunk], each a full DbMap selection (not a bare id)', () => {
    const chain = resolveChain(leaf('cell', { kind: 'cell', pageIndex: 101, segmentId: 31, chunkId: 11, cellOffset: 64 }), ctx);
    expect(chain.map((c) => c.type)).toEqual(['segment', 'page', 'chunk']);
    expect(chain[0].ref).toEqual({ kind: 'segment', segmentId: 31 });
    expect(chain[1].ref).toEqual({ kind: 'page', pageIndex: 101, segmentId: 31 });
    expect(chain[2].ref).toEqual({ kind: 'chunk', pageIndex: 101, segmentId: 31, chunkId: 11 });
  });

  it('a chunk leaf yields [segment, page] (no self-crumb), both structured', () => {
    const chain = resolveChain(leaf('chunk', { kind: 'chunk', pageIndex: 101, segmentId: 31, chunkId: 11 }), ctx);
    expect(chain.map((c) => c.type)).toEqual(['segment', 'page']);
    expect(chain[1].ref).toEqual({ kind: 'page', pageIndex: 101, segmentId: 31 });
  });

  it('a page leaf yields just [segment]; a free (owner-less) page yields no ancestors', () => {
    expect(resolveChain(leaf('page', { kind: 'page', pageIndex: 5, segmentId: 3 }), ctx).map((c) => c.type)).toEqual(['segment']);
    expect(resolveChain(leaf('page', { kind: 'page', pageIndex: 9 }), ctx)).toEqual([]);
  });

  it('every storage crumb round-trips: its ref carries the `.kind` the File Map detail switches on', () => {
    const chain = resolveChain(leaf('cell', { kind: 'cell', pageIndex: 1, segmentId: 2, chunkId: 3, cellOffset: 0 }), ctx);
    for (const c of chain) {
      expect((c.ref as { kind?: string }).kind).toBe(c.type);
    }
  });
});

describe('selectionRefLabel', () => {
  it('labels storage refs by their most specific id', () => {
    expect(selectionRefLabel({ type: 'segment', ref: { kind: 'segment', segmentId: 7 } })).toBe('#7');
    expect(selectionRefLabel({ type: 'page', ref: { kind: 'page', pageIndex: 42 } })).toBe('#42');
    expect(selectionRefLabel({ type: 'chunk', ref: { kind: 'chunk', pageIndex: 1, segmentId: 2, chunkId: 9 } })).toBe('#9');
    expect(selectionRefLabel({ type: 'cell', ref: { kind: 'cell', cellOffset: 128 } })).toBe('@128');
    expect(selectionRefLabel({ type: 'cell', ref: { kind: 'cell', cellOffset: 128, slotIndex: 4 } })).toBe('slot 4');
  });

  it('labels scalar and rich profiler refs', () => {
    expect(selectionRefLabel({ type: 'component', ref: 'Position' })).toBe('Position');
    expect(selectionRefLabel({ type: 'field', ref: { component: 'Position', field: 'X' } })).toBe('X');
    expect(selectionRefLabel({ type: 'system', ref: { name: 'Movement' } })).toBe('Movement');
  });

  it('routes component / archetype scalar crumbs through the friendly resolvers when supplied', () => {
    // selectionRefLabel passes the RAW ref to the resolver (the '#<id>' formatting is the caller's job).
    const friendly = {
      component: (n: string) => (n === 'Typhon.ARPG.Combat.StatusEffects' ? 'StatusEffects' : n),
      archetype: (id: string) => (id === '800' ? 'PlayerArch' : id),
    };
    expect(selectionRefLabel({ type: 'component', ref: 'Typhon.ARPG.Combat.StatusEffects' }, friendly)).toBe('StatusEffects');
    expect(selectionRefLabel({ type: 'archetype', ref: '800' }, friendly)).toBe('PlayerArch');
    // Only component/archetype scalars are routed — other ref types are untouched by the resolvers.
    expect(selectionRefLabel({ type: 'field', ref: { component: 'Typhon.ARPG.Combat.StatusEffects', field: 'X' } }, friendly)).toBe('X');
    expect(selectionRefLabel({ type: 'segment', ref: { kind: 'segment', segmentId: 7 } }, friendly)).toBe('#7');
  });

  it('falls back to the raw ref when no resolver is supplied (e.g. for a React key)', () => {
    expect(selectionRefLabel({ type: 'component', ref: 'Typhon.ARPG.Combat.StatusEffects' })).toBe('Typhon.ARPG.Combat.StatusEffects');
    expect(selectionRefLabel({ type: 'archetype', ref: '800' })).toBe('800');
  });
});
