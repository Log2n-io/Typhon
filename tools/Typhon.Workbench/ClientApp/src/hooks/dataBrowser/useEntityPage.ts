import { useMemo } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { customFetch } from '@/api/client';
import { useSessionStore } from '@/stores/useSessionStore';
import { normalizeEntityPage, type EntityPageRaw, type EntityRow } from './types';

interface Envelope<T> {
  data: T;
  status: number;
  headers: Headers;
}

/**
 * One page of an archetype's entities — an offset/limit slice of the server's cached snapshot. `keepPreviousData` holds the
 * current page on screen while the next one loads, so prev/next paging never flashes empty.
 */
export function useEntityPage(archetypeId: string | null, offset: number, limit: number, preview = '') {
  const sessionId = useSessionStore((s) => s.sessionId);

  const query = useQuery({
    queryKey: ['dataBrowser', 'entities', sessionId, archetypeId, offset, limit, preview],
    enabled: !!sessionId && !!archetypeId,
    placeholderData: keepPreviousData,
    queryFn: () => {
      const previewParam = preview ? `&preview=${encodeURIComponent(preview)}` : '';
      return customFetch<Envelope<EntityPageRaw> | Envelope<undefined>>(
        `/api/sessions/${sessionId}/data/archetypes/${archetypeId}/entities?offset=${offset}&limit=${limit}${previewParam}`,
        { method: 'GET' },
      );
    },
  });

  const page = useMemo(() => (query.data?.data ? normalizeEntityPage(query.data.data) : null), [query.data]);
  const rows: EntityRow[] = page?.entities ?? [];

  return {
    rows,
    total: page?.totalCount ?? 0,
    isLoading: query.isLoading,
    isError: query.isError,
    isFetching: query.isFetching,
  };
}
