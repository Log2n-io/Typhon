import { useQuery } from '@tanstack/react-query';
import { useSessionStore } from '@/stores/useSessionStore';
import { fetchJson } from '@/libs/dbmap/dbMapFetch';
import type { StorageHealthDto } from '@/api/generated/model/storageHealthDto';

/**
 * Aggregate storage health rollup (GAP-16 / AC2.9) — `GET dbmap/health`. The server aggregates the whole-DB
 * summary + per-segment table in one call (vs the client harvesting every segment). Orval types int64/decimal
 * as `number | string` (overflow-cautious); we normalize to plain numbers for the dashboard.
 */
export interface HealthSegment {
  id: number;
  kind: string;
  typeName: string;
  pageCount: number;
  allocatedChunkCount: number;
  chunkCapacity: number;
  chunkFillPct: number;
  reclaimableBytes: number;
  entityCount: number;
  occupancyPct: number;
}

export interface StorageHealth {
  databaseName: string;
  dataFileBytes: number;
  dataFilePageCount: number;
  usedPageCount: number;
  freePageCount: number;
  walBytes: number;
  checkpointLsn: number;
  segmentCount: number;
  reclaimableBytes: number;
  fragmentationPct: number;
  segments: HealthSegment[];
}

const n = (v: number | string | null | undefined): number => (v == null ? 0 : Number(v));

export function useDbMapHealth(sessionId: string | null) {
  const token = useSessionStore((s) => s.token);

  return useQuery<StorageHealth | null, Error>({
    queryKey: ['dbmap', 'health', sessionId],
    enabled: !!sessionId,
    staleTime: 5_000,
    refetchOnWindowFocus: false,
    queryFn: async ({ signal }) => {
      if (!sessionId) {
        return null;
      }
      const dto = await fetchJson<StorageHealthDto>(`/api/sessions/${sessionId}/dbmap/health`, token, signal);
      return {
        databaseName: dto.databaseName ?? '',
        dataFileBytes: n(dto.dataFileBytes),
        dataFilePageCount: n(dto.dataFilePageCount),
        usedPageCount: n(dto.usedPageCount),
        freePageCount: n(dto.freePageCount),
        walBytes: n(dto.walBytes),
        checkpointLsn: n(dto.checkpointLsn),
        segmentCount: n(dto.segmentCount),
        reclaimableBytes: n(dto.reclaimableBytes),
        fragmentationPct: n(dto.fragmentationPct),
        segments: (dto.segments ?? []).map((s) => ({
          id: n(s.id),
          kind: s.kind ?? '',
          typeName: s.typeName ?? '',
          pageCount: n(s.pageCount),
          allocatedChunkCount: n(s.allocatedChunkCount),
          chunkCapacity: n(s.chunkCapacity),
          chunkFillPct: n(s.chunkFillPct),
          reclaimableBytes: n(s.reclaimableBytes),
          entityCount: n(s.entityCount),
          occupancyPct: n(s.occupancyPct),
        })),
      };
    },
  });
}
