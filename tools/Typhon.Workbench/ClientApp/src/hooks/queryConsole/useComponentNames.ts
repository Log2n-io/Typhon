import { useMemo } from 'react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useGetApiSessionsSessionIdSchemaComponents } from '@/api/generated/schema/schema';
import { buildComponentNameMap, leafSegment } from '@/libs/componentNames';

/**
 * Session-wide "smart" component-name labeller for the Query Console. Loads the full component summary list once
 * (cached 30 s like the rest of the schema reads — and de-duped by React Query against the same call in
 * {@link useArchetypeComponents}, so no extra request) and derives a stable label map by common-prefix stripping
 * over *all* registered typeNames. The result: a component shows the same short label everywhere — picker, chips,
 * predicates, ORDER BY, result-grid headers, and the archetype combobox — regardless of which archetype or result
 * row it appears in.
 *
 * `label(name)` resolves three input shapes so a single function serves every call site:
 *   1. a registered typeName  (`ARPG.StatusEffects`)            → smart label
 *   2. a CLR full name        (`Typhon.ARPG.Schema...Effects`)  → mapped to its typeName, then smart label
 *   3. anything unknown / pre-load                              → its last dot-segment (graceful fallback)
 *
 * The label is display-only; the raw name remains the identity stored in the spec and sent to the parser.
 */
export function useComponentNames(): {
  label: (name: string | null | undefined) => string;
  isLoading: boolean;
} {
  const sessionId = useSessionStore((s) => s.sessionId);
  const query = useGetApiSessionsSessionIdSchemaComponents(sessionId ?? '', {
    query: { enabled: !!sessionId, staleTime: 30_000 },
  });

  const { labelByTypeName, typeNameByFullName } = useMemo(() => {
    const components = query.data?.data ?? [];
    const typeNames: string[] = [];
    const fullToType = new Map<string, string>();
    for (const c of components) {
      if (!c.typeName) continue;
      typeNames.push(c.typeName);
      if (c.fullName) fullToType.set(c.fullName, c.typeName);
    }
    return {
      labelByTypeName: buildComponentNameMap(typeNames),
      typeNameByFullName: fullToType,
    };
  }, [query.data]);

  const label = useMemo(
    () =>
      (name: string | null | undefined): string => {
        if (!name) return '';
        const direct = labelByTypeName.get(name);
        if (direct !== undefined) return direct;
        const typeName = typeNameByFullName.get(name);
        if (typeName !== undefined) {
          const viaFull = labelByTypeName.get(typeName);
          if (viaFull !== undefined) return viaFull;
        }
        return leafSegment(name);
      },
    [labelByTypeName, typeNameByFullName],
  );

  return { label, isLoading: query.isLoading };
}
