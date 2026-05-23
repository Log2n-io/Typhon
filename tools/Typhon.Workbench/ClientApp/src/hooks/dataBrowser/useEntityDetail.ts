import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { customFetch } from '@/api/client';
import { useSessionStore } from '@/stores/useSessionStore';
import { normalizeEntityDetail, type EntityDetail, type EntityDetailRaw } from './types';

interface Envelope<T> {
  data: T;
  status: number;
  headers: Headers;
}

/** Full component-card detail for one entity. Enabled only when both an archetype and an entity are selected. */
export function useEntityDetail(archetypeId: string | null, entityId: string | null) {
  const sessionId = useSessionStore((s) => s.sessionId);

  const query = useQuery({
    queryKey: ['dataBrowser', 'detail', sessionId, archetypeId, entityId],
    enabled: !!sessionId && !!archetypeId && !!entityId,
    queryFn: () =>
      customFetch<Envelope<EntityDetailRaw>>(
        `/api/sessions/${sessionId}/data/archetypes/${archetypeId}/entities/${entityId}`,
        { method: 'GET' },
      ),
  });

  const detail: EntityDetail | null = useMemo(
    () => (query.data?.data ? normalizeEntityDetail(query.data.data) : null),
    [query.data],
  );

  return { detail, isLoading: query.isLoading, isError: query.isError };
}
