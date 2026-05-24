import type { ArchetypeInfo, ComponentSummary } from '@/hooks/schema/types';

// Pure data model for the Archetype Inspector (Stage 2, GAP-02). The deep view for one archetype; driven
// by the bus leaf (`archetype` → id). Logic kept here (resolve the archetype, join its component types to
// their summaries, derive the indexed subset) so it's unit-tested without rendering.

export interface ArchetypeComponentRow {
  fullName: string;
  typeName: string;
  /** Resolved component summary, or null when the type isn't in the component list (still listed). */
  summary: ComponentSummary | null;
}

function stripNamespace(fullName: string): string {
  const dot = fullName.lastIndexOf('.');
  return dot === -1 ? fullName : fullName.slice(dot + 1);
}

/** Resolve the bus archetype id against the loaded list. Null id / not found → null (drives the picker). */
export function findArchetype(list: ArchetypeInfo[], archetypeId: string | null): ArchetypeInfo | null {
  if (!archetypeId) return null;
  return list.find((a) => a.archetypeId === archetypeId) ?? null;
}

/** Join the archetype's component types to their summaries; unresolved types still render (fallback name). */
export function resolveArchetypeComponents(
  archetype: ArchetypeInfo,
  components: ComponentSummary[],
): ArchetypeComponentRow[] {
  const byFullName = new Map(components.map((c) => [c.fullName, c]));
  return archetype.componentTypes.map((fullName) => {
    const summary = byFullName.get(fullName) ?? null;
    return { fullName, typeName: summary?.typeName ?? stripNamespace(fullName), summary };
  });
}

/**
 * The archetype's components that carry an index. The Indexes-here tab lists these (drill into the
 * Component Inspector for field-level detail) — the index is type-global (one B+Tree per indexed field,
 * spanning all archetypes), so the archetype view shows *which* components are indexed, not a per-archetype
 * index. Components with an unresolved summary are excluded (unknown index count).
 */
export function indexedComponents(rows: ArchetypeComponentRow[]): ArchetypeComponentRow[] {
  return rows.filter((r) => (r.summary?.indexCount ?? 0) > 0);
}
