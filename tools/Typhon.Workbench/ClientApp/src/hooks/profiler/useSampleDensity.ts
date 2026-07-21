import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { useSessionStore } from '@/stores/useSessionStore';
import type { CallTreeRequest } from './useCallTree';
import { applyWorkbenchAuthHeaders } from '@/api/bootstrapToken';

/** One time-bin of the sample-density sparkline — `count` in-scope samples starting at `startUs`. */
export interface SampleDensityBin {
  startUs: number;
  count: number;
}

/**
 * Sample-density-over-time for a scope (#351 Phase 5, §8.2) — the non-stationarity sparkline. A flat profile means the
 * scope is statistically stationary; spikes mean behavioral blending and a narrower scope should be considered.
 */
export interface SampleDensityResponse {
  startUs: number;
  binWidthUs: number;
  bins: SampleDensityBin[];
}

/**
 * Fetches the binned sample density for a scope (#351 Phase 5). POSTs to
 * `/api/sessions/{id}/profiler/sample-density` — the body wraps the same composite scope a `calltree` request carries.
 * A 202 (cache still building) resolves to `null` and the query keeps polling; a trace with no CPU samples in scope
 * resolves to an empty-bins response.
 */
export function useSampleDensity(
  sessionId: string | null,
  scope: CallTreeRequest,
  binCount = 64,
): UseQueryResult<SampleDensityResponse | null, Error> {
  const token = useSessionStore((s) => s.token);

  return useQuery<SampleDensityResponse | null, Error>({
    queryKey: [
      'profiler',
      'sample-density',
      sessionId,
      scope.startUs,
      scope.endUs,
      scope.frameRoot,
      scope.viewMode,
      scope.systemIndex,
      scope.phase,
      scope.spanKind,
      binCount,
    ],
    enabled: !!sessionId,
    retry: false,
    refetchInterval: (q) => (q.state.data || q.state.error ? false : 1000),
    queryFn: async ({ signal }) => {
      if (!sessionId) return null;
      const headers = applyWorkbenchAuthHeaders(new Headers({ 'Content-Type': 'application/json' }), token);
      const res = await fetch(`/api/sessions/${sessionId}/profiler/sample-density`, {
        method: 'POST',
        signal,
        headers,
        body: JSON.stringify({ scope, binCount }),
      });
      if (res.status === 202) return null;
      if (!res.ok) {
        throw new Error(`Sample density request failed: HTTP ${res.status}`);
      }
      return (await res.json()) as SampleDensityResponse;
    },
  });
}
