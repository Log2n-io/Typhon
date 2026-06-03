import { useMemo } from 'react';
import { useSessionStore } from '@/stores/useSessionStore';
import {
  useGetApiSessionsSessionIdSchemaArchetypes,
  useGetApiSessionsSessionIdSchemaComponents,
} from '@/api/generated/schema/schema';

/**
 * Components available on the currently-selected archetype, returned in a chip-popover-friendly shape. Joins the
 * archetype's `componentTypes` (full names) with the component summary list (typeName, fieldCount, etc.) so the
 * picker can show friendly names without round-tripping the server.
 *
 * The archetype reference is whatever the FROM chip stores — accepts the canonical `#<id>` form, the bare numeric
 * id, or a CLR class name. Returns `[]` until both the archetype list AND the component list are loaded.
 */
export interface ArchetypeComponent {
  /** Short component type name — what the chip stores + the parser accepts (e.g. "QCompA"). */
  readonly typeName: string;
  /** Fully-qualified CLR name — shown as the popover's secondary line (e.g. "Workbench.Test.QCompA"). */
  readonly fullName: string;
  /** How many indexed fields the component declares (drives the chip's "N indexed" badge). */
  readonly indexCount: number;
  /** Total field count (informational). */
  readonly fieldCount: number;
  /** True when the component has a <c>[SpatialIndex]</c> field — the SPATIAL chip's picker filters on this. */
  readonly hasSpatialIndex: boolean;
}

export function useArchetypeComponents(archetypeRef: string | null | undefined): {
  components: ArchetypeComponent[];
  isLoading: boolean;
} {
  const sessionId = useSessionStore((s) => s.sessionId);
  const archetypesQuery = useGetApiSessionsSessionIdSchemaArchetypes(sessionId ?? '', {
    query: { enabled: !!sessionId, staleTime: 30_000 },
  });
  const componentsQuery = useGetApiSessionsSessionIdSchemaComponents(sessionId ?? '', {
    query: { enabled: !!sessionId, staleTime: 30_000 },
  });

  const components = useMemo<ArchetypeComponent[]>(() => {
    if (!archetypeRef) return [];
    const archetypes = archetypesQuery.data?.data ?? [];
    const allComponents = componentsQuery.data?.data ?? [];
    if (!archetypes.length || !allComponents.length) return [];

    // Match the archetype: '#2001' / '2001' → ArchetypeId; otherwise treat as CLR name (not exposed via the
    // schema endpoint today, so falls through to "no match" silently — users on numeric ids see results, users
    // on CLR-name FROMs get an empty picker but can still type free predicates).
    const ref = archetypeRef.startsWith('#') ? archetypeRef.slice(1) : archetypeRef;
    const arch = archetypes.find((a) => a.archetypeId === ref);
    if (!arch) return [];

    // Index the component-summary list by fullName for O(1) lookup.
    const byFullName = new Map<string, (typeof allComponents)[number]>();
    for (const c of allComponents) {
      if (c.fullName) byFullName.set(c.fullName, c);
    }

    const out: ArchetypeComponent[] = [];
    for (const fullName of arch.componentTypes ?? []) {
      const c = byFullName.get(fullName);
      if (!c || !c.typeName) continue;
      out.push({
        typeName: c.typeName,
        fullName,
        indexCount: Number(c.indexCount ?? 0),
        fieldCount: Number(c.fieldCount ?? 0),
        hasSpatialIndex: c.hasSpatialIndex ?? false,
      });
    }
    return out;
  }, [archetypeRef, archetypesQuery.data, componentsQuery.data]);

  return {
    components,
    isLoading: archetypesQuery.isLoading || componentsQuery.isLoading,
  };
}
