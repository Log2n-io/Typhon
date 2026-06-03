import { useEffect, useRef, useState } from 'react';
import { useSessionStore } from '@/stores/useSessionStore';
import { usePostApiSessionsSessionIdQueryPlan } from '@/api/generated/query-console/query-console';
import { useQueryConsoleStore } from '@/stores/useQueryConsoleStore';

/**
 * Cheap pre-flight: a DSL is plan-able only once it has a FROM clause. Skipping `/plan` for partial input
 * (mid-typing, "FROM " without an archetype yet) eliminates the noisiest source of expected parse errors and
 * removes wasted round-trips. Anything more elaborate (e.g. trailing-operator detection) would re-implement the
 * grammar in TypeScript — not worth it for Phase 1.
 */
function isPlanCandidate(dsl: string): boolean {
  // Match "FROM <token>" where <token> is at least one non-whitespace character. Case-insensitive.
  return /\bFROM\s+\S/i.test(dsl);
}

/**
 * Suppress logging for the expected mid-typing failure mode (server-side `invalid_query_syntax`). Other
 * failures (500, network) still surface in the Logs panel so server faults aren't hidden by this filter.
 */
function silenceInvalidSyntax(error: unknown): boolean {
  // FetchError carries the parsed ProblemDetails body under `.data`; we look up `.title` for the stable code.
  const e = error as { data?: { title?: string }; status?: number };
  return e?.status === 400 && e?.data?.title === 'invalid_query_syntax';
}

/**
 * Debounced bridge: `dsl` text → `POST /query/plan` → cost-chip estimates pushed to the Zustand store. Subscribes the
 * caller so re-renders track `costPreview` / `costState`; 250 ms debounce matches the design's §4.4 commitment.
 *
 * Phase-1 simplification: we drive the `useMutation` hook from a `useEffect` rather than wrapping in `useQuery` —
 * `POST /plan` is technically a mutation per orval's REST convention, but we treat it as a debounced query. On supersede
 * (DSL changes mid-flight), the latest mutation wins; the in-flight one's result is ignored by the freshness check.
 */
export function useQueryConsolePlan(dsl: string): void {
  const sessionId = useSessionStore((s) => s.sessionId);
  const setCostPreview = useQueryConsoleStore((s) => s.setCostPreview);
  const planMutation = usePostApiSessionsSessionIdQueryPlan({
    // Silence the expected mid-typing failure mode (parse errors during chip / DSL editing). Other failures
    // (network, 5xx) still surface via the global MutationCache.onError handler.
    mutation: { meta: { silenceErrors: silenceInvalidSyntax } },
  });
  const [debouncedDsl, setDebouncedDsl] = useState(dsl);
  const seq = useRef(0);

  // Debounce dsl → debouncedDsl (250 ms).
  useEffect(() => {
    const handle = setTimeout(() => setDebouncedDsl(dsl), 250);
    return () => clearTimeout(handle);
  }, [dsl]);

  // Fire mutation on debounced change. Sequence number guards against out-of-order responses.
  useEffect(() => {
    // Gate: skip when there's no session, no text, or the text isn't yet a plan-able shape (no FROM clause).
    // The third condition cuts the noisiest source of expected /plan failures during early typing.
    if (!sessionId || !debouncedDsl.trim() || !isPlanCandidate(debouncedDsl)) {
      setCostPreview(null, 'idle');
      return;
    }
    const mine = ++seq.current;
    setCostPreview(null, 'loading');
    planMutation.mutate(
      { sessionId, data: { dsl: debouncedDsl } },
      {
        onSuccess: (resp) => {
          // Stale-guard: if a newer request fired while this was in flight, drop the result.
          if (mine !== seq.current) return;
          // Orval wraps the body in `{ data, headers }` — unwrap to reach the QueryPlanDto.
          setCostPreview(resp.data, 'ok');
        },
        onError: () => {
          if (mine !== seq.current) return;
          setCostPreview(null, 'error');
        },
      },
    );
    // planMutation is stable across renders by react-query convention; not added to deps to avoid loop on settle.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [debouncedDsl, sessionId, setCostPreview]);
}
