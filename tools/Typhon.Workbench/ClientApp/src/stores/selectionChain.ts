import type { SelectionLeaf, SelectionObjectType, SelectionState } from './useSelectionStore';
import type { DbMapChunkSelection, DbMapPageSelection, DbMapSegmentSelection } from '@/libs/dbmap/dbMapSelection';

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
      // Storage drill: build whatever prefix of Segment ⊃ Page ⊃ Chunk the ref exposes. Each crumb carries the FULL
      // structured DbMap selection ref (not a bare id) so it round-trips through `select()` — a scalar id can't,
      // because the File Map detail switches on `.kind` and reads coordinates, so a number renders the empty Cell
      // fallback. (Component/system chains stay scalar; their detail cards consume the scalar ref directly.)
      const chain: SelectionRef[] = [];
      const segmentId = refField(leaf.ref, 'segmentId') as number | undefined;
      const pageIndex = refField(leaf.ref, 'pageIndex') as number | undefined;
      const chunkId = refField(leaf.ref, 'chunkId') as number | undefined;
      if (segmentId != null) {
        const ref: DbMapSegmentSelection = { kind: 'segment', segmentId };
        chain.push({ type: 'segment', ref });
      }
      if (pageIndex != null && leaf.type !== 'page') {
        const ref: DbMapPageSelection = { kind: 'page', pageIndex, segmentId };
        chain.push({ type: 'page', ref });
      }
      if (chunkId != null && segmentId != null && pageIndex != null && leaf.type === 'cell') {
        const ref: DbMapChunkSelection = { kind: 'chunk', pageIndex, segmentId, chunkId };
        chain.push({ type: 'chunk', ref });
      }
      return chain;
    }
    default:
      // system / component / archetype / query / resource / tick / timeRange / sourceLocation / segment:
      // top of their chain (or not a containment object) → no ancestors.
      return [];
  }
}

/**
 * Optional friendly-name resolvers for {@link selectionRefLabel}. A component / archetype crumb is a bare scalar
 * (the registered type name / the archetype id), which can't be shortened without the session's name maps — those
 * live behind React-Query hooks (`useComponentNames` / `useArchetypeNames`). The React-component callers (Context
 * Bar, Inspector ancestor header) pass these so the crumb reads the same friendly form as the rest of the UI;
 * omitted (e.g. for a React `key`, or in a pure test) the label degrades to the raw ref.
 */
export interface RefLabelResolvers {
  component?: (typeName: string) => string;
  archetype?: (archetypeId: string) => string;
}

/**
 * A short, human-readable label for a chain crumb / ancestor section header. Scalar refs (component name, archetype
 * id) render through the optional friendly resolvers when supplied, else verbatim; the structured File Map storage
 * refs render their most specific id (page/chunk/segment id, or a cell's slot / byte offset); rich profiler refs use
 * their `field`/`entityId`/`name`/`localId`. Falls back to the object type when nothing more specific is available.
 * Shared by the Context Bar breadcrumb and the Inspector ancestor headers so the two never drift.
 */
export function selectionRefLabel(node: SelectionRef, friendly?: RefLabelResolvers): string {
  const { ref } = node;
  if (typeof ref === 'string' || typeof ref === 'number') {
    const s = String(ref);
    if (node.type === 'component' && friendly?.component) {
      return friendly.component(s);
    }
    if (node.type === 'archetype' && friendly?.archetype) {
      return friendly.archetype(s);
    }
    return s;
  }
  if (ref !== null && typeof ref === 'object') {
    const r = ref as Record<string, unknown>;
    if ('kind' in r) {
      switch (r.kind) {
        case 'page':
          return `#${String(r.pageIndex)}`;
        case 'chunk':
          return `#${String(r.chunkId)}`;
        case 'segment':
          return `#${String(r.segmentId)}`;
        case 'cell':
          return r.slotIndex != null ? `slot ${String(r.slotIndex)}` : `@${String(r.cellOffset)}`;
      }
    }
    if ('field' in r) return String(r.field);
    if ('entityId' in r) return String(r.entityId);
    if ('name' in r) return String(r.name);
    if ('localId' in r) return `#${String(r.localId)}`;
  }
  return node.type;
}
