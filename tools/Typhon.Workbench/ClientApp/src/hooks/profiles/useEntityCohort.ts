import { useQuery } from '@tanstack/react-query';
import { applyWorkbenchAuthHeaders } from '@/api/bootstrapToken';
import { fetchJson } from '@/libs/dbmap/dbMapFetch';
import { useSessionCapability, useSessionStore } from '@/stores/useSessionStore';
import type { CohortResolution, EntityCohort } from '@/libs/profiles/entityCohort';

/**
 * The entity lens's two server calls (#620, design §4.4): what a capture recorded being born or destroyed in a tick
 * range, and what the database still holds of it.
 *
 * Both are gated, and the gates differ on purpose. The cohort needs only a capture, so it works in a bare trace
 * session. The survival split needs a database, and without one it is **absent** rather than zeroed — "0 alive" and
 * "we did not ask" are different claims, and only one of them is true here (§5.7).
 */

/** Per-tick spawn or destroy volume — one point on a `lifecycle/*` track. */
export interface LifecyclePoint {
  tickNumber: number;
  entityCount: number;
  /** Recorded runs behind `entityCount`. One bulk load is 1; a loop of individual spawns is N. */
  runCount: number;
}

async function postJson<T>(url: string, token: string | null, body: unknown, signal: AbortSignal): Promise<T> {
  const headers = applyWorkbenchAuthHeaders(new Headers({ 'Content-Type': 'application/json' }), token);
  const res = await fetch(url, { method: 'POST', signal, headers, body: JSON.stringify(body) });
  if (!res.ok) {
    let detail = `${res.status} ${res.statusText}`;
    try {
      const problem = (await res.json()) as { detail?: string; title?: string };
      detail = problem?.detail ?? problem?.title ?? detail;
    } catch {
      // Non-JSON body — keep the status-text fallback.
    }
    throw new Error(detail);
  }
  return (await res.json()) as T;
}

/** Per-tick spawn/destroy series for the whole capture, or scoped to one archetype. Drives the strip that makes a storm findable. */
export function useLifecycleSeries(kind: 'spawn' | 'destroy', archetypeLabel?: string | null) {
  const hasProfiler = useSessionCapability('profiler');
  const sessionId = useSessionStore((s) => s.sessionId);
  const token = useSessionStore((s) => s.token);
  const suffix = archetypeLabel ? `/${encodeURIComponent(archetypeLabel)}` : '';

  return useQuery<LifecyclePoint[], Error>({
    queryKey: ['lifecycle', 'series', sessionId, kind, archetypeLabel ?? null],
    enabled: !!sessionId && hasProfiler,
    staleTime: 30_000,
    refetchOnWindowFocus: false,
    queryFn: async ({ signal }) => {
      const dto = await fetchJson<{ records: LifecyclePoint[] }>(
        `/api/sessions/${sessionId}/track/lifecycle/${kind}${suffix}`,
        token,
        signal,
      );
      return dto.records ?? [];
    },
  });
}

/**
 * Server-side page cap (`EntityLifecycleService.MaxPageSize`) and the ceiling on how many ids the survival split will
 * gather across pages.
 *
 * The design's sentence is *"1,240 spawned here — 830 still alive"*, and 830-of-1,240 is only true if every member was
 * asked about. One page's worth would report "160 of 200" beside a headline of 620 — arithmetic that reads as the
 * cohort being 200. So the cohort is gathered whole, up to this bound; past it the readout says it sampled.
 */
export const COHORT_PAGE_SIZE = 500;
export const MAX_RESOLVED_COHORT = 5_000;

/** Every id in a cohort, up to {@link MAX_RESOLVED_COHORT}. `complete` is false when the cap truncated it. */
export interface FullCohort {
  ids: string[];
  complete: boolean;
  total: number;
}

/**
 * Gathers a whole cohort's ids by paging, so the survival split can be stated over the cohort rather than over a
 * window of it.
 */
export function useFullCohortIds(
  kind: 'spawn' | 'destroy',
  fromTick: number | null,
  toTick: number | null,
  archetypeLabel: string | null,
  total: number,
) {
  const hasProfiler = useSessionCapability('profiler');
  const sessionId = useSessionStore((s) => s.sessionId);
  const token = useSessionStore((s) => s.token);
  const enabled = !!sessionId && hasProfiler && fromTick != null && toTick != null && total > 0;

  return useQuery<FullCohort | null, Error>({
    queryKey: ['lifecycle', 'cohort-all', sessionId, kind, fromTick, toTick, archetypeLabel, total],
    enabled,
    staleTime: 30_000,
    refetchOnWindowFocus: false,
    queryFn: async ({ signal }) => {
      if (!enabled) return null;
      const want = Math.min(total, MAX_RESOLVED_COHORT);
      const ids: string[] = [];
      while (ids.length < want) {
        const params = new URLSearchParams({
          kind,
          from: String(fromTick),
          to: String(toTick),
          offset: String(ids.length),
          limit: String(COHORT_PAGE_SIZE),
        });
        if (archetypeLabel) params.set('archetype', archetypeLabel);
        const page = await fetchJson<EntityCohort>(`/api/sessions/${sessionId}/lifecycle/cohort?${params}`, token, signal);
        if (!page.entityIds?.length) break;
        ids.push(...page.entityIds);
        if (!page.hasMore) break;
      }
      return { ids: ids.slice(0, want), complete: want >= total, total };
    },
  });
}

/** One page of the entities born (or destroyed) in `[fromTick, toTick]`. */
export function useEntityCohort(
  kind: 'spawn' | 'destroy',
  fromTick: number | null,
  toTick: number | null,
  archetypeLabel: string | null,
  offset: number,
  limit: number,
) {
  const hasProfiler = useSessionCapability('profiler');
  const sessionId = useSessionStore((s) => s.sessionId);
  const token = useSessionStore((s) => s.token);
  const enabled = !!sessionId && hasProfiler && fromTick != null && toTick != null;

  return useQuery<EntityCohort | null, Error>({
    queryKey: ['lifecycle', 'cohort', sessionId, kind, fromTick, toTick, archetypeLabel, offset, limit],
    enabled,
    staleTime: 30_000,
    refetchOnWindowFocus: false,
    queryFn: async ({ signal }) => {
      if (!enabled) return null;
      const params = new URLSearchParams({
        kind,
        from: String(fromTick),
        to: String(toTick),
        offset: String(offset),
        limit: String(limit),
      });
      if (archetypeLabel) params.set('archetype', archetypeLabel);
      return fetchJson<EntityCohort>(`/api/sessions/${sessionId}/lifecycle/cohort?${params}`, token, signal);
    },
  });
}

/**
 * Splits a cohort into the entities the database still holds and those it does not.
 *
 * Disabled without the `database` capability, so a bare trace session issues no request and the readout is absent.
 * `entityIds` is the cohort page's ids; passing the whole cohort would mean paging the *answer* instead of the
 * question, which is the wrong shape once a cohort runs to six figures.
 */
export function useCohortSurvival(archetypeId: string | null, entityIds: string[] | null) {
  const hasDatabase = useSessionCapability('database');
  const sessionId = useSessionStore((s) => s.sessionId);
  const token = useSessionStore((s) => s.token);
  const enabled = !!sessionId && hasDatabase && !!archetypeId && !!entityIds && entityIds.length > 0;

  return useQuery<CohortResolution | null, Error>({
    // The id list is part of the key by length + endpoints rather than in full: a 500-element array stringified into
    // every cache key is a lot of memory for a key, and the (archetype, first, last, count) tuple is enough to
    // distinguish the pages this panel actually issues.
    queryKey: [
      'lifecycle',
      'survival',
      sessionId,
      archetypeId,
      entityIds?.length ?? 0,
      entityIds?.[0] ?? null,
      entityIds?.[entityIds.length - 1] ?? null,
    ],
    enabled,
    staleTime: 30_000,
    refetchOnWindowFocus: false,
    queryFn: async ({ signal }) => {
      if (!enabled) return null;
      return postJson<CohortResolution>(
        `/api/sessions/${sessionId}/data/archetypes/${archetypeId}/entities/resolve`,
        token,
        { entityIds },
        signal,
      );
    },
  });
}
