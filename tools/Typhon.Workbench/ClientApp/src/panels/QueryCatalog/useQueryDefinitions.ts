import { useMemo } from 'react';
import { useGetApiSessionsSessionIdProfilerQueries } from '@/api/generated/profiler/profiler';
import type { QueryDefinitionDto } from '@/api/generated/model';
import { useSessionStore } from '@/stores/useSessionStore';

/**
 * TanStack Query wrapper around the P4 `/api/sessions/{id}/profiler/queries` endpoint.
 * The catalog is stable for the trace session lifetime (it's reconstructed once on first endpoint
 * hit and cached server-side via <c>QueryCatalogService</c>), so we use `Infinity` staleTime —
 * no refetch on tab focus / interval.
 *
 * Issue #338 (P5 of #342).
 */
export function useQueryDefinitions() {
  const sessionId = useSessionStore((s) => s.sessionId);

  const query = useGetApiSessionsSessionIdProfilerQueries(
    sessionId ?? '',
    {
      query: {
        enabled: !!sessionId,
        staleTime: Infinity,
        // 202 while the trace cache build is still running — poll every 1 s until it lands.
        refetchInterval: (q) => (q.state.data && q.state.data.data === undefined ? 1_000 : false),
      },
    },
  );

  const definitions: QueryDefinitionDto[] = useMemo(
    () => query.data?.data ?? [],
    [query.data],
  );

  return {
    definitions,
    isLoading: query.isLoading,
    isError: query.isError,
    error: query.error,
  };
}
