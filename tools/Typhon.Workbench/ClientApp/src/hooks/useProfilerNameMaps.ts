import { useMemo } from 'react';
import { useGetApiSessionsSessionIdProfilerMetadata } from '@/api/generated/profiler/profiler';
import { useSessionStore } from '@/stores/useSessionStore';

/**
 * Resolved id→name lookups from the profiler metadata, shared by every query-analysis surface.
 *
 * Extracted from the (previously triplicated) block in QueryCatalogPanel / QueryPlanTreePanel /
 * ExecutionInspectorPanel — the Query Analyzer consolidation (#376 Phase 4) consumes this single
 * copy; the inline duplicates are removed with those panels in 4D.
 */
export interface ProfilerNameMaps {
  /**
   * A query definition's `TargetComponentType` is either a ComponentType id (Component-WHERE queries)
   * or an Archetype id (pull-mode views over a whole archetype). Both tables are merged into one map —
   * archetypes win on the (in-practice non-colliding) overlap because they're the more meaningful label.
   */
  archetypeNames: Map<number, string>;
  /** System index → display name. */
  systemNames: Map<number, string>;
  /**
   * The ids that are genuine ComponentType ids (not Archetype ids). A query's `TargetComponentType` is one or
   * the other; consumers branch on this to route a "Open target in …" hand-off to the Component vs Archetype
   * Inspector. (#376 Phase 4D.)
   */
  componentTypeIds: Set<number>;
}

/** Build the {@link ProfilerNameMaps} for the current profiler session (metadata is immutable per session). */
export function useProfilerNameMaps(): ProfilerNameMaps {
  const sessionId = useSessionStore((s) => s.sessionId);
  const metadataQuery = useGetApiSessionsSessionIdProfilerMetadata(
    sessionId ?? '',
    { query: { enabled: !!sessionId, staleTime: Infinity } },
  );
  const metadata = metadataQuery.data?.data;

  const archetypeNames = useMemo(() => {
    const m = new Map<number, string>();
    for (const ct of metadata?.componentTypes ?? []) {
      m.set(Number(ct.componentTypeId), ct.name ?? `Component[${ct.componentTypeId}]`);
    }
    for (const a of metadata?.archetypes ?? []) {
      m.set(Number(a.archetypeId), a.name ?? `Archetype[${a.archetypeId}]`);
    }
    return m;
  }, [metadata]);

  const systemNames = useMemo(() => {
    const m = new Map<number, string>();
    for (const sys of metadata?.systems ?? []) {
      m.set(Number(sys.index), sys.name ?? `System[${sys.index}]`);
    }
    return m;
  }, [metadata]);

  const componentTypeIds = useMemo(() => {
    const s = new Set<number>();
    for (const ct of metadata?.componentTypes ?? []) {
      s.add(Number(ct.componentTypeId));
    }
    return s;
  }, [metadata]);

  return { archetypeNames, systemNames, componentTypeIds };
}
