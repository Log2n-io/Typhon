import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { useSessionStore } from '@/stores/useSessionStore';

/** View-mode axis for the call tree (§8.2). `on-cpu` = Managed samples only; `wall-clock` = all samples. */
export type CallTreeViewMode = 'on-cpu' | 'wall-clock';

/**
 * One node of the folded call tree. `children` holds indices into {@link CallTreeResponse.nodes} — the tree is
 * flat on the wire (a deep call stack would otherwise blow past System.Text.Json's MaxDepth). The synthetic root
 * carries `frameId === -1`.
 */
export interface CallTreeNode {
  frameId: number;
  selfSamples: number;
  totalSamples: number;
  children: number[];
}

/** Self-time sample count attributed to one subsystem category. */
export interface CategorySlice {
  categoryId: number;
  selfSamples: number;
}

/** Folded call tree for one scope. `nodes[0]` is the synthetic root; every node's `children` index into `nodes`. */
export interface CallTreeResponse {
  nodes: CallTreeNode[];
  totalSamples: number;
  managedSamples: number;
  externalSamples: number;
  categoryBreakdown: CategorySlice[];
}

/**
 * Composite scope for a call-tree request (#351 Phase 4 + Phase 5). The server resolves exactly one scope axis, in
 * precedence order: `spanKind` ▸ `systemIndex` ▸ `phase` ▸ the manual `startUs`/`endUs` range ▸ whole session (all null).
 * `frameRoot` and `viewMode` compose with any scope.
 */
export interface CallTreeRequest {
  startUs: number | null;
  endUs: number | null;
  frameRoot: number | null;
  viewMode: CallTreeViewMode;
  systemIndex: number | null;
  phase: string | null;
  spanKind: number | null;
}

/**
 * Fetches a server-folded call tree for the given scope (#351 Phase 4). POSTs to
 * `/api/sessions/{id}/profiler/calltree` — the scope is composite, hence POST. A 202 (cache still
 * building) resolves to `null` and the query keeps polling; a trace with no CPU samples resolves to
 * an empty-tree response.
 */
export function useCallTree(
  sessionId: string | null,
  request: CallTreeRequest,
): UseQueryResult<CallTreeResponse | null, Error> {
  const token = useSessionStore((s) => s.token);

  return useQuery<CallTreeResponse | null, Error>({
    queryKey: [
      'profiler',
      'calltree',
      sessionId,
      request.startUs,
      request.endUs,
      request.frameRoot,
      request.viewMode,
      request.systemIndex,
      request.phase,
      request.spanKind,
    ],
    enabled: !!sessionId,
    retry: false,
    refetchInterval: (q) => (q.state.data || q.state.error ? false : 1000),
    queryFn: async ({ signal }) => {
      if (!sessionId) return null;
      const headers = new Headers();
      headers.set('Content-Type', 'application/json');
      if (token) headers.set('X-Session-Token', token);
      const res = await fetch(`/api/sessions/${sessionId}/profiler/calltree`, {
        method: 'POST',
        signal,
        headers,
        body: JSON.stringify(request),
      });
      if (res.status === 202) return null;
      if (!res.ok) {
        throw new Error(`Call tree request failed: HTTP ${res.status}`);
      }
      return (await res.json()) as CallTreeResponse;
    },
  });
}
