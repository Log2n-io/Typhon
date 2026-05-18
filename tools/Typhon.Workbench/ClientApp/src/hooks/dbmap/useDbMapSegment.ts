import { useQuery } from '@tanstack/react-query';
import { useSessionStore } from '@/stores/useSessionStore';
import { fetchJson } from '@/libs/dbmap/dbMapFetch';
import type { StorageSegmentDetailDto } from '@/libs/dbmap/types';

// Fetches one segment's directory — its ordered logical→physical page list plus the chunk layout — for the A3
// fragmentation metrics (Module 15, §4.3). Keyed on (session, segment) and cached by TanStack Query, so the
// fragmentation lens never refetches a segment it has already measured.

const SEGMENT_STALE_MS = 30_000;

/** Fetches `GET /dbmap/segment/{id}` for the fragmentation lens; disabled until a segment is focused. */
export function useDbMapSegment(sessionId: string | null, segmentId: number | null) {
  const token = useSessionStore((s) => s.token);
  return useQuery<StorageSegmentDetailDto, Error>({
    queryKey: ['dbmap-segment', sessionId, segmentId],
    enabled: !!sessionId && segmentId != null,
    staleTime: SEGMENT_STALE_MS,
    refetchOnWindowFocus: false,
    queryFn: ({ signal }) =>
      fetchJson<StorageSegmentDetailDto>(`/api/sessions/${sessionId}/dbmap/segment/${segmentId}`, token, signal),
  });
}
