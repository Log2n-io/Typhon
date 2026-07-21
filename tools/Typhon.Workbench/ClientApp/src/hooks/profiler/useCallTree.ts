import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { useSessionStore } from '@/stores/useSessionStore';
import { applyWorkbenchAuthHeaders } from '@/api/bootstrapToken';

/** View-mode axis for the call tree (§8.2). `on-cpu` = Managed samples only; `wall-clock` = all samples. */
export type CallTreeViewMode = 'on-cpu' | 'wall-clock';

/**
 * Fold direction (§8.7 sandwich). `top-down` = callees (root→leaf); `bottom-up` = callers (leaf-rooted). The panel's
 * "sandwich" UI mode is a client composition of the two with the same frame-root, not a separate server direction.
 */
export type CallTreeDirection = 'top-down' | 'bottom-up';

/**
 * One node of the folded call tree. `children` holds indices into {@link CallTreeResponse.nodes} — the tree is
 * flat on the wire (a deep call stack would otherwise blow past System.Text.Json's MaxDepth).
 *
 * Negative `frameId`s are synthetic, not real frames: `-1` is the tree root; `-2`/`-3`/`-4` are the §8.7
 * involuntary-stall aggregates — see {@link INVOLUNTARY_FRAME_LABELS}.
 */
export interface CallTreeNode {
  frameId: number;
  selfSamples: number;
  totalSamples: number;
  children: number[];
}

/**
 * Labels for the synthetic §8.7 involuntary-stall aggregate nodes (server: `CallTreeFolder.GcSuspensionFrameId` etc.).
 * A node whose `frameId` is a key here is an aggregate of samples whose stack was bad-luck noise — it has no children
 * and never resolves against the frame-symbol manifest.
 */
export const INVOLUNTARY_FRAME_LABELS: Record<number, string> = {
  [-2]: '[GC suspension]',
  [-3]: '[Preempted]',
  [-4]: '[Paging]',
};

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
  /**
   * §8.7 — `true` when the trace carried context-switch data, so the on-CPU view is a true on-/off-core split. `false`
   * ⇒ degraded mode (GC stalls still classified, the rest by the `SampleType` proxy); the panel then labels its first
   * view "Thread time" rather than "On-CPU".
   */
  classificationAvailable: boolean;
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
  direction: CallTreeDirection;
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
      request.direction,
      request.systemIndex,
      request.phase,
      request.spanKind,
    ],
    enabled: !!sessionId,
    retry: false,
    refetchInterval: (q) => (q.state.data || q.state.error ? false : 1000),
    queryFn: async ({ signal }) => {
      if (!sessionId) return null;
      const headers = applyWorkbenchAuthHeaders(new Headers(), token);
      headers.set('Content-Type', 'application/json');
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
