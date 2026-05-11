import { useGetApiSessionsSessionIdProfilerQueriesKindLocalId } from '@/api/generated/profiler/profiler';
import { useSessionStore } from '@/stores/useSessionStore';
import type { QueryDefinitionDto } from '@/api/generated/model/queryDefinitionDto';
import type { QueryPlanFocus } from './useQueryPlanStore';

interface QueryPlanResult {
  definition: QueryDefinitionDto | null;
  isLoading: boolean;
  isError: boolean;
}

/**
 * Fetches a single query definition for the focused (kind, localId). When no focus is set the
 * underlying hook is disabled — TanStack Query simply returns idle data and no request is fired.
 */
export function useQueryPlan(focus: QueryPlanFocus | null): QueryPlanResult {
  const sessionId = useSessionStore((s) => s.sessionId);
  const enabled = !!sessionId && focus !== null;
  const query = useGetApiSessionsSessionIdProfilerQueriesKindLocalId(
    sessionId ?? '',
    focus?.kind ?? 0,
    focus?.localId ?? 0,
    { query: { enabled, staleTime: Infinity } },
  );
  return {
    definition: query.data?.data ?? null,
    isLoading: query.isLoading,
    isError: query.isError,
  };
}
