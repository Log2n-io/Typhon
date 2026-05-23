import { useQuery, type QueryKey } from '@tanstack/react-query';
import { customFetch } from '@/api/client';
import { useSessionStore } from '@/stores/useSessionStore';

/**
 * The standard PC-2 view phases. `building` is the 202 "cache building, keep polling" state — distinct
 * from `loading` so the UI can say "building the trace index…" instead of a bare skeleton.
 */
export type ViewPhase = 'loading' | 'building' | 'empty' | 'error' | 'ready' | 'no-selection';

export interface PhaseInput {
  readonly isLoading: boolean;
  readonly error: unknown;
  /** Server returned 202 Accepted (cache build in progress). */
  readonly building: boolean;
  /** Data has arrived but is empty (caller decides what "empty" means). */
  readonly isEmpty: boolean;
  /** Nothing is selected yet (for selection-driven views). */
  readonly noSelection?: boolean;
}

/**
 * Pure PC-2 phase derivation — unit-tested (suite D), shared by {@link use202Query} and any panel that
 * tracks its own query. Error wins, then no-selection, then loading, then building, then empty, then ready.
 */
export function deriveViewPhase(input: PhaseInput): ViewPhase {
  if (input.error != null) return 'error';
  if (input.noSelection) return 'no-selection';
  if (input.isLoading) return 'loading';
  if (input.building) return 'building';
  if (input.isEmpty) return 'empty';
  return 'ready';
}

interface Envelope<T> {
  data: T;
  status: number;
  headers: Headers;
}

export interface Use202Options<T> {
  /** Defaults to `!!sessionId`. */
  enabled?: boolean;
  /** Poll interval while the server returns 202. Default 1000 ms. */
  pollMs?: number;
  staleTime?: number;
  /** Decide whether arrived data counts as empty (→ `empty` phase). */
  isEmpty?: (data: T | null) => boolean;
}

export interface Use202Result<T> {
  data: T | null;
  phase: ViewPhase;
  error: unknown;
  refetch: () => void;
}

/**
 * One wrapper for the dual 202 idioms (hand-written `customFetch` polling vs the Orval envelope): a
 * session-scoped GET that polls while the server builds (202), surfaces a normalized {@link ViewPhase},
 * and unwraps the `{ data, status }` envelope. New views use this; existing ad-hoc hooks converge onto
 * it over time (09-shell-evolution §2 "state-set + 202 unification").
 */
export function use202Query<T>(queryKey: QueryKey, url: string | null, options: Use202Options<T> = {}): Use202Result<T> {
  const sessionId = useSessionStore((s) => s.sessionId);
  const enabled = (options.enabled ?? !!sessionId) && url != null;

  const query = useQuery({
    queryKey,
    enabled,
    staleTime: options.staleTime,
    refetchInterval: (q) => (q.state.data?.status === 202 ? (options.pollMs ?? 1000) : false),
    queryFn: () => customFetch<Envelope<T | undefined>>(url as string, { method: 'GET' }),
  });

  const building = query.data?.status === 202;
  const data = (query.data?.data ?? null) as T | null;
  const phase = deriveViewPhase({
    isLoading: enabled && query.isLoading,
    error: query.error,
    building,
    isEmpty: options.isEmpty ? options.isEmpty(data) : false,
  });

  return { data, phase, error: query.error, refetch: () => void query.refetch() };
}
