import { useGetApiSessionsSessionIdProfilerQueriesKindLocalIdExecutions } from '@/api/generated/profiler/profiler';
import { useSessionStore } from '@/stores/useSessionStore';
import type { QueryExecutionDto } from '@/api/generated/model/queryExecutionDto';

/** The query identity the executions are fetched for. */
export interface QueryExecutionsFocus {
  kind: number;
  localId: number;
}

interface ExecutionsResult {
  executions: QueryExecutionDto[];
  isLoading: boolean;
  isError: boolean;
}

/**
 * Fetch the recent executions for the focused (kind, localId). Returns at most {@link pageSize}
 * rows, in trace order. When no focus is set the underlying query is disabled.
 */
export function useExecutions(focus: QueryExecutionsFocus | null, pageSize: number = 200): ExecutionsResult {
  const sessionId = useSessionStore((s) => s.sessionId);
  const enabled = !!sessionId && focus !== null;
  const query = useGetApiSessionsSessionIdProfilerQueriesKindLocalIdExecutions(
    sessionId ?? '',
    focus?.kind ?? 0,
    focus?.localId ?? 0,
    { pageOffset: 0, pageSize },
    { query: { enabled, staleTime: Infinity } },
  );
  return {
    executions: (query.data?.data ?? []) as QueryExecutionDto[],
    isLoading: query.isLoading,
    isError: query.isError,
  };
}
