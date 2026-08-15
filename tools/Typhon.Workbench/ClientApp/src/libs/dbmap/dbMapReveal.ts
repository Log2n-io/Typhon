import { isOwnedByArchetype } from '@/libs/schema/archetypeStorage';

/**
 * Resolving a cross-panel reveal request to the segments the File Map should frame (#619 §4.2).
 *
 * Pure, because the panel that consumes it is canvas-bound and effectively untestable, while *which* segments a
 * reveal frames is exactly the part that can be quietly wrong — and was: before #619 the effect took the first
 * match only, so an archetype (which owns up to three segments) could not be framed as the set it is.
 */

/** The segment fields a reveal needs. Matches `StorageSegmentDto` as normalised by `useDbMap`. */
export interface RevealSegment {
  id: number;
  kind: string;
  typeName: string;
  rootPageIndex: number;
  pageCount: number;
}

/** What the map should do about a pending focus request: nothing, or frame these segments around this one. */
export interface RevealTarget<T extends RevealSegment> {
  /** Every segment to frame. Empty when the request resolves to nothing — the map then stays where it is. */
  segments: T[];
  /** The one to select on the bus and pulse — the largest, which is the cluster rows in every real case. */
  primary: T | null;
  /** Human label for the nav-history entry. Empty when nothing resolved. */
  label: string;
}

/**
 * Resolve a pending focus request against the map's own segment table.
 *
 * A **component** owns one segment, so the first match is the answer. An **archetype** owns its cluster rows, its
 * entity map and its cluster index; all three are framed. Component tables are deliberately excluded from the
 * archetype case — they are type-global, shared with every other archetype carrying that component, so framing
 * them would present another archetype's pages as this one's.
 */
export function resolveRevealTarget<T extends RevealSegment>(
  focus: { kind: 'component' | 'archetype'; name: string } | null,
  segments: readonly T[] | null | undefined,
): RevealTarget<T> {
  const none: RevealTarget<T> = { segments: [], primary: null, label: '' };
  if (!focus || !segments || segments.length === 0) {
    return none;
  }

  const matched = focus.kind === 'archetype'
    ? segments.filter((s) => isOwnedByArchetype(s, focus.name))
    : segments.filter((s) => s.typeName === focus.name).slice(0, 1);

  if (matched.length === 0) {
    return none;
  }

  return {
    segments: matched,
    primary: matched.reduce((a, b) => (b.pageCount > a.pageCount ? b : a)),
    label: `${focus.kind === 'archetype' ? 'Archetype' : 'Component'} ${focus.name}`,
  };
}
