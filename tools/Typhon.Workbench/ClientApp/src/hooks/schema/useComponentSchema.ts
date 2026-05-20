import { useMemo } from 'react';
import { useGetApiSessionsSessionIdSchemaComponentsTypeName } from '@/api/generated/schema/schema';
import { useSessionStore } from '@/stores/useSessionStore';
import { FetchError } from '@/api/client';
import { normalizeSchema, type ComponentSchema } from './types';

/**
 * Full byte-layout schema for one component type — the data the Layout view renders. Stable once
 * loaded (schema is immutable within a session), so we use Infinity staleTime to avoid refetch on
 * selection churn.
 */
export function useComponentSchema(typeName: string | null) {
  const sessionId = useSessionStore((s) => s.sessionId);

  const query = useGetApiSessionsSessionIdSchemaComponentsTypeName(
    sessionId ?? '',
    typeName ?? '',
    {
      query: {
        enabled: !!sessionId && !!typeName,
        staleTime: Infinity,
        // 202 while the trace cache build is still running — poll every 1 s until it lands.
        refetchInterval: (q) => (q.state.data && q.state.data.data === undefined ? 1_000 : false),
        // A URL-restored selection (`?component=...`) often points to a type that no longer
        // exists in the current session (different trace, different schema). The panel handles
        // 404 by falling through to its empty state — don't spam the Logs panel for that case,
        // but still surface 500s and friends.
        meta: { silenceErrors: (err: unknown) => err instanceof FetchError && err.status === 404 },
      },
    },
  );

  const schema: ComponentSchema | undefined = useMemo(
    () => (query.data?.data ? normalizeSchema(query.data.data) : undefined),
    [query.data],
  );

  return {
    schema,
    isLoading: query.isLoading,
    isError: query.isError,
    error: query.error,
  };
}
