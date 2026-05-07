import { useMemo } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useSessionStore } from '@/stores/useSessionStore';
import { useEventSource } from './useEventSource';

export interface ResourceGraphEventPayload {
  kind: 'Added' | 'Removed' | 'Mutated';
  nodeId: string;
  parentId: string;
  type: string;
  timestamp: string;
}

/**
 * Subscribes the session to the engine's resource-graph mutation stream. On every event the hook
 * invalidates the resource-root query so the tree + fuse index re-fetch. The server coalesces
 * bursts to at most 10 events/sec per session so the invalidation cadence stays gentle. Each frame
 * arrives as a typed `resource-mutation` SSE event (#308); the JSON payload still carries the
 * domain-level <c>kind</c> field (Added / Removed / Mutated) — that's a domain enum, not the SSE
 * discriminator.
 */
export function useResourceGraphStream(): { state: 'connecting' | 'open' | 'closed' } {
  const sessionId = useSessionStore((s) => s.sessionId);
  const kind = useSessionStore((s) => s.kind);
  const queryClient = useQueryClient();

  // SSE stream is Open-session only — server returns 401 for other kinds. Null URL = hook stays closed.
  const url = sessionId && kind === 'open' ? `/api/sessions/${sessionId}/resources/stream` : null;

  const listeners = useMemo(
    () => ({
      'resource-mutation': (_evt: ResourceGraphEventPayload) => {
        if (!sessionId) return;
        queryClient.invalidateQueries({
          queryKey: [`/api/sessions/${sessionId}/resources/root`],
        });
      },
    }),
    [queryClient, sessionId],
  );

  const state = useEventSource(url, listeners);
  return { state };
}
