import { useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { applyWorkbenchAuthHeaders } from '@/api/bootstrapToken';
import type { ProfilerMetadataDto } from '@/api/generated/model';

/**
 * TanStack Query hook for the session-scoped `/api/sessions/{id}/profiler/metadata` endpoint. Polls every 2 s
 * while the server returns 202 Accepted (build in progress); stops once a 200 + DTO lands. Results are hydrated
 * into {@link useProfilerSessionStore} so other components (Detail panel, chunk cache) can read synchronously.
 *
 * Direct-fetch implementation for now — will swap to the orval-generated `useGetApiSessionsSessionIdProfilerMetadata`
 * once Phase 1a's orval regen step runs against the new endpoints.
 */
export function useProfilerMetadata(sessionId: string | null) {
  const token = useSessionStore((s) => s.token);
  const setMetadata = useProfilerSessionStore((s) => s.setMetadata);
  const setBuildError = useProfilerSessionStore((s) => s.setBuildError);

  const query = useQuery<ProfilerMetadataDto | null, Error>({
    queryKey: ['profiler', 'metadata', sessionId],
    enabled: !!sessionId,
    // Poll every 2 s while the server returns 202 (build in progress). Stop on success (data non-null) OR on
    // a terminal error (500 — build faulted) so a failed build doesn't flood the server with retries. The panel
    // surfaces the error via useProfilerSessionStore.buildError; the user clears it by closing + reopening.
    refetchInterval: (q) => (q.state.data || q.state.error ? false : 2000),
    retry: false,
    queryFn: async ({ signal }) => {
      if (!sessionId) return null;
      const headers = applyWorkbenchAuthHeaders(new Headers(), token);
      const res = await fetch(`/api/sessions/${sessionId}/profiler/metadata`, { signal, headers });
      if (res.status === 202) {
        // Not ready yet — return null to let refetchInterval poll again.
        return null;
      }
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
      return (await res.json()) as ProfilerMetadataDto;
    },
  });

  // Mirror query state into the Zustand store. Keeps panels / detail views independent of the hook call site.
  useEffect(() => {
    if (query.data) {
      setMetadata(query.data);
    }
  }, [query.data, setMetadata]);

  useEffect(() => {
    if (query.error) {
      setBuildError(query.error.message);
    }
  }, [query.error, setBuildError]);

  return query;
}
