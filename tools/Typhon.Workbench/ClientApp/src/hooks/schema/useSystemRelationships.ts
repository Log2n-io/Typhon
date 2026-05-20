import { useMemo } from 'react';
import { useGetApiSessionsSessionIdSchemaComponentsTypeNameSystems } from '@/api/generated/schema/schema';
import { useSessionStore } from '@/stores/useSessionStore';
import { FetchError } from '@/api/client';
import {
  normalizeSystemRelationshipsResponse,
  type SystemRelationshipsResponse,
} from './types';

const EMPTY: SystemRelationshipsResponse = { runtimeHosted: false, systems: [] };

/**
 * Systems that read or reactively trigger on the given component type. Runtime-gated — the
 * envelope's <c>runtimeHosted</c> flag lets the panel distinguish "no relationships" from
 * "runtime not hosted". Until runtime hosting lands in the Workbench, the flag is always false.
 */
export function useSystemRelationships(typeName: string | null) {
  const sessionId = useSessionStore((s) => s.sessionId);

  const query = useGetApiSessionsSessionIdSchemaComponentsTypeNameSystems(
    sessionId ?? '',
    typeName ?? '',
    {
      query: {
        enabled: !!sessionId && !!typeName,
        staleTime: 30_000,
        // 202 while the trace cache build is still running — poll every 1 s until it lands.
        refetchInterval: (q) => (q.state.data && q.state.data.data === undefined ? 1_000 : false),
        // 404 when the URL-restored selection isn't in this session's schema — handled by the
        // panel's empty state, no Logs entry. Other failures still log.
        meta: { silenceErrors: (err: unknown) => err instanceof FetchError && err.status === 404 },
      },
    },
  );

  const response: SystemRelationshipsResponse = useMemo(
    () => (query.data?.data ? normalizeSystemRelationshipsResponse(query.data.data) : EMPTY),
    [query.data],
  );

  return {
    response,
    isLoading: query.isLoading,
    isError: query.isError,
  };
}
