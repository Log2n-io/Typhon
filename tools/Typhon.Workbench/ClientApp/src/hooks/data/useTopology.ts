import { useQuery } from '@tanstack/react-query';
import { useSessionStore } from '@/stores/useSessionStore';
import type { TopologyDto } from '@/api/generated/model/topologyDto';
import { applyWorkbenchAuthHeaders } from '@/api/bootstrapToken';

export function useTopology(sessionId: string | null) {
  const token = useSessionStore((s) => s.token);

  return useQuery<TopologyDto | null, Error>({
    queryKey: ['data', 'topology', sessionId],
    enabled: !!sessionId,
    // Topology is static after Build(), so we don't refetch successful loads. But the server returns 202
    // while the cache is still building; in that case `queryFn` returns null. Without a poll, the query
    // sticks at "loading" forever. Match `useProfilerMetadata`'s pattern: poll every 2 s until data lands
    // or an error fires; stop afterwards (data is then cached at staleTime: Infinity).
    staleTime: Infinity,
    refetchInterval: (q) => (q.state.data || q.state.error ? false : 2000),
    queryFn: async ({ signal }) => {
      if (!sessionId) return null;
      const headers = applyWorkbenchAuthHeaders(new Headers(), token);
      const res = await fetch(`/api/sessions/${sessionId}/topology`, { signal, headers });
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
      return (await res.json()) as TopologyDto;
    },
  });
}
