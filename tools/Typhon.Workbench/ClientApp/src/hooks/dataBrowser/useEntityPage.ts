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
 *
 * When `cohortIds` is supplied (#620 — the entity lens's handoff), the page is a window onto **that set** instead, served by
 * the POST variant because an id list does not fit a query string. Everything downstream — row shape, preview decoding,
 * paging — is identical, so the panel does not fork on which mode it is in; only the chip above the list does.
 */
export function useEntityPage(
  archetypeId: string | null,
  offset: number,
  limit: number,
  preview = '',
  cohortIds: string[] | null = null,
) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const scoped = !!cohortIds && cohortIds.length > 0;

  const query = useQuery({
    // The cohort participates in the key by size and endpoints rather than in full — see useEntityCohort for the same
    // reasoning. Two different cohorts of equal size sharing both endpoints would collide, which cannot happen here
    // because a cohort is a contiguous, ordered slice of one capture's runs.
    queryKey: [
      'dataBrowser',
      'entities',
      sessionId,
      archetypeId,
      offset,
      limit,
      preview,
      scoped ? cohortIds!.length : 0,
      scoped ? cohortIds![0] : null,
      scoped ? cohortIds![cohortIds!.length - 1] : null,
    ],
    enabled: !!sessionId && !!archetypeId,
    placeholderData: keepPreviousData,
    queryFn: () => {
      const previewParam = preview ? `&preview=${encodeURIComponent(preview)}` : '';
      const base = `/api/sessions/${sessionId}/data/archetypes/${archetypeId}/entities`;
      if (scoped) {
        return customFetch<Envelope<EntityPageRaw> | Envelope<undefined>>(
          `${base}/page?offset=${offset}&limit=${limit}${previewParam}`,
          { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ entityIds: cohortIds }) },
        );
      }
      return customFetch<Envelope<EntityPageRaw> | Envelope<undefined>>(
        `${base}?offset=${offset}&limit=${limit}${previewParam}`,
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
