import { useMemo } from 'react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useGetApiSessionsSessionIdSchemaArchetypes } from '@/api/generated/schema/schema';
import { buildComponentNameMap, leafSegment } from '@/libs/componentNames';

/**
 * Session-wide "smart" archetype-name labeller — the archetype counterpart to {@link useComponentNames}. Loads the
 * archetype list once (cached 30 s, deduped by React Query with the FROM picker and the Archetype Inspector) and
 * derives a stable id→label map by shortening every archetype's CLR name to its shortest unique suffix. So
 * `Typhon.Workbench.Fixtures.PlayerArch` shows as `PlayerArch` everywhere it appears.
 *
 * `label(archetypeRef)` accepts the canonical `#<id>` form, a bare numeric id, or a raw free-typed value:
 *   - a known id (`#823` / `823`) → the shortened archetype class name
 *   - anything else               → the input returned unchanged (free-typed CLR names / pasted refs render as-is)
 *
 * Display-only: the FROM clause keeps storing `#<id>` (the value the parser resolves); the label is cosmetic.
 */
export function useArchetypeNames(): {
  label: (archetypeRef: string | null | undefined) => string;
  isLoading: boolean;
} {
  const sessionId = useSessionStore((s) => s.sessionId);
  const query = useGetApiSessionsSessionIdSchemaArchetypes(sessionId ?? '', {
    query: { enabled: !!sessionId, staleTime: 30_000 },
  });

  const shortById = useMemo(() => {
    const archetypes = query.data?.data ?? [];
    const names: string[] = [];
    const nameById = new Map<string, string>();
    for (const a of archetypes) {
      if (a.archetypeId == null || !a.name) continue;
      names.push(a.name);
      nameById.set(a.archetypeId, a.name);
    }
    const shortByName = buildComponentNameMap(names);
    const result = new Map<string, string>();
    for (const [id, name] of nameById) {
      result.set(id, shortByName.get(name) ?? leafSegment(name));
    }
    return result;
  }, [query.data]);

  const label = useMemo(
    () =>
      (archetypeRef: string | null | undefined): string => {
        if (!archetypeRef) return '';
        const id = archetypeRef.startsWith('#') ? archetypeRef.slice(1) : archetypeRef;
        return shortById.get(id) ?? archetypeRef;
      },
    [shortById],
  );

  return { label, isLoading: query.isLoading };
}
