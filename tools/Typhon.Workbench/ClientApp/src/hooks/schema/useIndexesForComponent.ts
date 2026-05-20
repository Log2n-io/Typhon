import { useMemo } from 'react';
import { useGetApiSessionsSessionIdSchemaComponentsTypeNameIndexes } from '@/api/generated/schema/schema';
import { useSessionStore } from '@/stores/useSessionStore';
import { FetchError } from '@/api/client';
import { normalizeIndex, type IndexInfo } from './types';

/**
 * Indexes covering fields of the given component type. Schema-stable for the session lifetime so
 * we use Infinity staleTime — no refetch on selection churn.
 */
export function useIndexesForComponent(typeName: string | null) {
  const sessionId = useSessionStore((s) => s.sessionId);

  const query = useGetApiSessionsSessionIdSchemaComponentsTypeNameIndexes(
    sessionId ?? '',
    typeName ?? '',
    {
      query: {
        enabled: !!sessionId && !!typeName,
        staleTime: Infinity,
        // 202 while the trace cache build is still running — poll every 1 s until it lands.
        refetchInterval: (q) => (q.state.data && q.state.data.data === undefined ? 1_000 : false),
        // 404 when the URL-restored selection isn't in this session's schema — handled by the
        // panel's empty state, no Logs entry. Other failures still log.
        meta: { silenceErrors: (err: unknown) => err instanceof FetchError && err.status === 404 },
      },
    },
  );

  const indexes: IndexInfo[] = useMemo(
    () => (query.data?.data ?? []).map(normalizeIndex),
    [query.data],
  );

  return {
    indexes,
    isLoading: query.isLoading,
    isError: query.isError,
  };
}
