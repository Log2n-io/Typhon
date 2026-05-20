import { useMemo } from 'react';
import { useGetApiSessionsSessionIdSchemaComponents } from '@/api/generated/schema/schema';
import { useSessionStore } from '@/stores/useSessionStore';
import { normalizeSummary, type ComponentSummary } from './types';

/**
 * List of every component type registered in the current session, with triage-friendly metadata.
 * Powers the Schema Browser table and the palette's `#schema` fuzzy match feed.
 *
 * Runtime counts (entityCount, archetypeCount) drift during a live session — we cache with a short
 * staleTime so a user toggling between panels sees fresh numbers without forcing a roundtrip on
 * every render.
 */
export function useComponentList() {
  const sessionId = useSessionStore((s) => s.sessionId);

  const query = useGetApiSessionsSessionIdSchemaComponents(sessionId ?? '', {
    query: {
      enabled: !!sessionId,
      staleTime: 5_000,
      // Server returns 202 while the trace cache build is still running — customFetch hands us
      // an envelope with `data: undefined`. Poll every 1 s until the build completes.
      refetchInterval: (q) => (q.state.data && q.state.data.data === undefined ? 1_000 : false),
    },
  });

  const list: ComponentSummary[] = useMemo(
    () => (query.data?.data ?? []).map(normalizeSummary),
    [query.data],
  );

  return {
    list,
    isLoading: query.isLoading,
    isError: query.isError,
    isFetching: query.isFetching,
    refetch: query.refetch,
  };
}
