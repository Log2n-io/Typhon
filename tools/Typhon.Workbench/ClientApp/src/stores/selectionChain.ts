import type { SelectionLeaf, SelectionObjectType, SelectionState } from './useSelectionStore';

/**
 * A non-leaf object reference in the Inspector's containment context-stack — rendered as a collapsible
 * **summary** section above the leaf (IA §2.5). Unlike {@link SelectionLeaf} it carries no recency.
 */
export interface SelectionRef {
  readonly type: SelectionObjectType;
  readonly ref: unknown;
}

/** Reads an optional numeric/string field off a rich selection ref without assuming its full shape. */
function refField(ref: unknown, key: string): unknown {
  if (ref !== null && typeof ref === 'object' && key in (ref as Record<string, unknown>)) {
    return (ref as Record<string, unknown>)[key];
  }
  return undefined;
}

/**
 * Resolve the leaf's containment ancestors (root → immediate parent), per the IA §2.5 chains:
 * `Archetype ⊃ Component ⊃ Field` (also `Component ⊃ Index`), `Segment ⊃ Page ⊃ Chunk ⊃ Cell`,
 * `Query ⊃ Execution`, `Archetype ⊃ Entity`, `System ⊃ Span`.
 *
 * Stage-1 resolution is **structural-with-context**: ancestors come from the leaf's own ref where it
 * carries them (e.g. a storage address), falling back to the current bus scalar context (the component
 * a field was reached through, the system a span projects). Nav-path-driven M:N resolution is layered
 * on in Phase 2 (nav history); the empty/structural fallback here is the documented base case.
 */
export function resolveChain(leaf: SelectionLeaf | null, ctx: SelectionState): SelectionRef[] {
  if (leaf === null) {
    return [];
  }
  switch (leaf.type) {
    case 'field':
    case 'index': {
      const component = (refField(leaf.ref, 'component') as string) ?? ctx.component;
      return component != null ? [{ type: 'component', ref: component }] : [];
    }
    case 'entity': {
      const archetypeId = refField(leaf.ref, 'archetypeId');
      return archetypeId != null ? [{ type: 'archetype', ref: archetypeId }] : [];
    }
    case 'span': {
      const system = (refField(leaf.ref, 'system') as string) ?? ctx.system;
      return system != null ? [{ type: 'system', ref: system }] : [];
    }
    case 'execution': {
      const query = refField(leaf.ref, 'query');
      return query != null ? [{ type: 'query', ref: query }] : [];
    }
    case 'cell':
    case 'chunk':
    case 'page': {
      // Storage drill: build whatever prefix of Segment ⊃ Page ⊃ Chunk the ref exposes.
      const chain: SelectionRef[] = [];
      const segment = refField(leaf.ref, 'segmentId');
      if (segment != null) {
        chain.push({ type: 'segment', ref: segment });
      }
      const page = refField(leaf.ref, 'pageIndex');
      if (page != null && leaf.type !== 'page') {
        chain.push({ type: 'page', ref: page });
      }
      const chunk = refField(leaf.ref, 'chunkId');
      if (chunk != null && leaf.type === 'cell') {
        chain.push({ type: 'chunk', ref: chunk });
      }
      return chain;
    }
    default:
      // system / component / archetype / query / resource / tick / timeRange / sourceLocation / segment:
      // top of their chain (or not a containment object) → no ancestors.
      return [];
  }
}
