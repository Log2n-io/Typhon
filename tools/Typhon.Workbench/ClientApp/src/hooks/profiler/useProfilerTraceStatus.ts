import { useQuery } from '@tanstack/react-query';
import { useSessionStore } from '@/stores/useSessionStore';
import { applyWorkbenchAuthHeaders } from '@/api/bootstrapToken';

/**
 * Polls `GET /api/sessions/{id}/profiler/trace-status` (~3 s) to detect when the source `.typhon-trace`
 * file has been overwritten on disk — e.g. a profiling re-run against the same app regenerated it.
 *
 * The actual filesystem watching + SHA-256 fingerprint comparison happens server-side (see
 * `TraceSessionRuntime`); this hook just surfaces the resulting boolean so the profiler header can
 * offer a reload button. Polling stops once the flag flips true — it is monotonic for a given session
 * (a reload spins up a fresh session with its own watcher), so there is nothing further to learn.
 *
 * Direct-fetch implementation, mirroring {@link useProfilerMetadata} — avoids an orval regen step.
 */
export function useProfilerTraceStatus(sessionId: string | null): boolean {
  const token = useSessionStore((s) => s.token);

  const query = useQuery<boolean, Error>({
    queryKey: ['profiler', 'trace-status', sessionId],
    enabled: !!sessionId,
    // Poll while the flag is still false; stop the moment it turns true.
    refetchInterval: (q) => (q.state.data ? false : 3000),
    retry: false,
    queryFn: async ({ signal }) => {
      if (!sessionId) return false;
      const headers = applyWorkbenchAuthHeaders(new Headers(), token);
      const res = await fetch(`/api/sessions/${sessionId}/profiler/trace-status`, { signal, headers });
      if (!res.ok) {
        // 409 (wrong session kind) or transient error — treat as "no new version" and keep polling.
        return false;
      }
      const dto = (await res.json()) as { newVersionAvailable?: boolean };
      return dto.newVersionAvailable === true;
    },
  });

  return query.data === true;
}
