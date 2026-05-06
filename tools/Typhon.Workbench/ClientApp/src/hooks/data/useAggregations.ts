import { useQuery } from '@tanstack/react-query';
import { useSessionStore } from '@/stores/useSessionStore';
import type { AggregationQueryDto } from '@/api/generated/model/aggregationQueryDto';
import type { AggregationResponseDto } from '@/api/generated/model/aggregationResponseDto';

export function useAggregations(sessionId: string | null, queries: AggregationQueryDto[]) {
  const token = useSessionStore((s) => s.token);

  return useQuery<AggregationResponseDto | null, Error>({
    queryKey: ['data', 'aggregate', sessionId, queries],
    enabled: !!sessionId && queries.length > 0,
    staleTime: 30_000,
    queryFn: async ({ signal }) => {
      if (!sessionId || queries.length === 0) return null;
      const headers = new Headers({ 'Content-Type': 'application/json' });
      if (token) headers.set('X-Session-Token', token);
      headers.set('X-Workbench-Api', '1');
      const res = await fetch(`/api/sessions/${sessionId}/aggregate`, {
        method: 'POST',
        body: JSON.stringify({ queries }),
        signal,
        headers,
      });
      if (res.status === 202) return null;
      if (!res.ok) {
        let detail = `${res.status} ${res.statusText}`;
        try {
          const problem = (await res.json()) as { detail?: string; title?: string };
          if (problem?.detail) detail = problem.detail;
          else if (problem?.title) detail = problem.title;
        } catch {
          // Non-JSON body — fall back to status text.
        }
        throw new Error(detail);
      }
      return (await res.json()) as AggregationResponseDto;
    },
  });
}
