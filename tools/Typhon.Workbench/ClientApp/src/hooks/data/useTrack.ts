import { useQuery } from '@tanstack/react-query';
import { useSessionStore } from '@/stores/useSessionStore';
import type { TrackDataResponseDto } from '@/api/generated/model/trackDataResponseDto';
import { applyWorkbenchAuthHeaders } from '@/api/bootstrapToken';

export function useTrack(
  sessionId: string | null,
  trackId: string,
  from?: number,
  to?: number,
) {
  const token = useSessionStore((s) => s.token);

  return useQuery<TrackDataResponseDto | null, Error>({
    queryKey: ['data', 'track', sessionId, trackId, from, to],
    enabled: !!sessionId && !!trackId,
    staleTime: 30_000,
    queryFn: async ({ signal }) => {
      if (!sessionId) return null;
      const params = new URLSearchParams();
      if (from != null) params.set('from', String(from));
      if (to != null) params.set('to', String(to));
      const qs = params.toString() ? `?${params.toString()}` : '';
      const headers = applyWorkbenchAuthHeaders(new Headers(), token);
      // trackId may contain '/' (e.g. "tick/summary") — the server uses a catch-all route {**trackId}.
      const res = await fetch(`/api/sessions/${sessionId}/track/${trackId}${qs}`, { signal, headers });
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
      return (await res.json()) as TrackDataResponseDto;
    },
  });
}
