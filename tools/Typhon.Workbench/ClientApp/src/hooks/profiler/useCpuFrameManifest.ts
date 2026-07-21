import { useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useSessionStore } from '@/stores/useSessionStore';
import { useCpuFrameStore, type CpuCategory, type CpuFrameSymbol } from '@/stores/useCpuFrameStore';
import { applyWorkbenchAuthHeaders } from '@/api/bootstrapToken';

interface CpuFrameManifestDto {
  frames: CpuFrameSymbol[];
  categories: CpuCategory[];
}

/**
 * Fetches the CPU-sample frame-symbol manifest for a profiler session (#351 Phase 4) and hydrates it
 * into {@link useCpuFrameStore}. The manifest lets the Call Tree panel resolve a node's `frameId` to a
 * method + `file:line`. Returns an empty manifest for traces captured without CPU sampling.
 *
 * `cpu-frames` answers 202 (empty body) while the sidecar cache is still building — that resolves to
 * `null` and the query keeps polling, mirroring {@link useCallTree}. A fire-once fetch would race the
 * build, parse the empty 202 body, error out, and leave every Call Tree row stuck on its `#frameId`.
 */
export function useCpuFrameManifest(sessionId: string | null): void {
  const token = useSessionStore((s) => s.token);
  const setManifest = useCpuFrameStore((s) => s.setManifest);

  const query = useQuery<CpuFrameManifestDto | null, Error>({
    queryKey: ['profiler', 'cpu-frames', sessionId],
    enabled: !!sessionId,
    retry: false,
    refetchInterval: (q) => (q.state.data || q.state.error ? false : 1000),
    queryFn: async ({ signal }) => {
      if (!sessionId) return null;
      const headers = applyWorkbenchAuthHeaders(new Headers(), token);
      const res = await fetch(`/api/sessions/${sessionId}/profiler/cpu-frames`, { signal, headers });
      if (res.status === 202) return null;
      if (!res.ok) {
        throw new Error(`CPU frame manifest request failed: HTTP ${res.status}`);
      }
      return (await res.json()) as CpuFrameManifestDto;
    },
  });

  useEffect(() => {
    if (!query.data) return;
    setManifest(query.data.frames, query.data.categories);
  }, [query.data, setManifest]);
}
