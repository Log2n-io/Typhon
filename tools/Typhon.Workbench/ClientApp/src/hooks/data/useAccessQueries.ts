import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { SystemListDto } from '@/api/generated/model/systemListDto';
import { useSessionStore } from '@/stores/useSessionStore';
import { useTopology } from './useTopology';
import { applyWorkbenchAuthHeaders } from '@/api/bootstrapToken';

/**
 * Helpers around the RFC 07 access declarations carried on {@link TopologyDto.systems}. The data
 * is small (≤ a few hundred systems × small string arrays) and static after `Build()`, so the
 * single-component lookups derive locally from the cached topology — no extra round-trip. The
 * server-side `/queries/who-writes/{T}` + `/queries/who-reads/{T}` endpoints exist for clients
 * that don't already have the topology loaded; expose them too via {@link useWhoWritesRemote} /
 * {@link useWhoReadsRemote} for parity, but prefer the local versions inside this app.
 */

function arrayHas(a: string[] | null | undefined, value: string): boolean {
  if (!a) return false;
  for (let i = 0; i < a.length; i++) {
    if (a[i] === value) return true;
  }
  return false;
}

/**
 * Local resolution: scan {@link TopologyDto.systems} for systems whose declarations include the
 * named component as any kind of write (Writes ∪ SideWrites).
 */
export function useWhoWrites(component: string | null): SystemDefinitionDto[] {
  const { data: topology } = useTopology(useSessionStore((s) => s.sessionId));
  return useMemo(() => {
    if (!topology?.systems || !component) return [];
    return topology.systems.filter((s) => arrayHas(s.writes, component) || arrayHas(s.sideWrites, component));
  }, [topology, component]);
}

/**
 * Local resolution: scan {@link TopologyDto.systems} for systems whose declarations include the
 * named component as any kind of read (Reads ∪ ReadsFresh ∪ ReadsSnapshot ∪ AdditionalReads).
 */
export function useWhoReads(component: string | null): SystemDefinitionDto[] {
  const { data: topology } = useTopology(useSessionStore((s) => s.sessionId));
  return useMemo(() => {
    if (!topology?.systems || !component) return [];
    return topology.systems.filter(
      (s) =>
        arrayHas(s.reads, component) ||
        arrayHas(s.readsFresh, component) ||
        arrayHas(s.readsSnapshot, component) ||
        arrayHas(s.additionalReads, component),
    );
  }, [topology, component]);
}

/**
 * Local resolution: snapshot readers (read the previous-tick value, ordered before writers).
 * Useful for the DAG view's snapshot-edge lens.
 */
export function useSnapshotReadersOf(component: string | null): SystemDefinitionDto[] {
  const { data: topology } = useTopology(useSessionStore((s) => s.sessionId));
  return useMemo(() => {
    if (!topology?.systems || !component) return [];
    return topology.systems.filter((s) => arrayHas(s.readsSnapshot, component));
  }, [topology, component]);
}

/**
 * Server round-trip variant of {@link useWhoWrites}. Hits `/queries/who-writes/{component}`. Use
 * this when the topology hasn't been (or shouldn't be) cached locally — otherwise prefer the
 * local hook above.
 */
export function useWhoWritesRemote(component: string | null) {
  return useSystemListQuery('who-writes', component);
}

/** Server round-trip variant of {@link useWhoReads}. */
export function useWhoReadsRemote(component: string | null) {
  return useSystemListQuery('who-reads', component);
}

function useSystemListQuery(kind: 'who-writes' | 'who-reads', component: string | null) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const token = useSessionStore((s) => s.token);

  return useQuery<SystemListDto | null, Error>({
    queryKey: ['data', 'queries', kind, sessionId, component],
    enabled: !!sessionId && !!component,
    staleTime: Infinity,
    queryFn: async ({ signal }) => {
      if (!sessionId || !component) return null;
      const headers = applyWorkbenchAuthHeaders(new Headers(), token);
      const res = await fetch(
        `/api/sessions/${sessionId}/queries/${kind}/${encodeURIComponent(component)}`,
        { signal, headers },
      );
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
      return (await res.json()) as SystemListDto;
    },
  });
}
