import { useQuery } from '@tanstack/react-query';
import { useSessionStore } from '@/stores/useSessionStore';
import { decodeBase64, fetchJson } from '@/libs/dbmap/dbMapFetch';
import type { DbMapData, StorageRegionDto, StorageRegionsDto } from '@/libs/dbmap/types';

// Fetches the coarse Database File Map (Module 15, Track A — A1). One TanStack Query call fetches both coarse
// endpoints (`/dbmap/regions` for metadata + segment table, `/dbmap/region` for the per-page SoA buffers) and
// assembles the decoded `DbMapData` the renderer consumes. The detail tier (A2) is fetched separately by the
// useDbMapDetail hooks.

export function useDbMap(sessionId: string | null) {
  const token = useSessionStore((s) => s.token);

  return useQuery<DbMapData | null, Error>({
    queryKey: ['dbmap', sessionId],
    enabled: !!sessionId,
    staleTime: 30_000,
    // Structural data — refetch only on the explicit Refresh button, never on a window-focus event (which
    // would otherwise reset the user's zoom when the panel re-fits the camera to fresh data).
    refetchOnWindowFocus: false,
    queryFn: async ({ signal }) => {
      if (!sessionId) {
        return null;
      }
      const base = `/api/sessions/${sessionId}/dbmap`;
      const regions = await fetchJson<StorageRegionsDto>(`${base}/regions`, token, signal);
      const region = await fetchJson<StorageRegionDto>(`${base}/region`, token, signal);

      const pageType = decodeBase64(region.pageTypes);
      const ownerBytes = decodeBase64(region.ownerSegmentIds);
      const ownerSegmentId = new Uint16Array(
        ownerBytes.buffer,
        ownerBytes.byteOffset,
        ownerBytes.byteLength >> 1,
      );

      return {
        databaseName: regions.databaseName,
        dataFileBytes: regions.dataFileBytes,
        // The descriptor arrays are in cell space (§5.5); their length is the grid size, == real pages when exact.
        pageCount: pageType.length,
        downSampleFactor: regions.downSampleFactor,
        walBytes: regions.walBytes,
        hilbertOrder: regions.hilbertOrder,
        checkpointLsn: regions.checkpointLsn,
        detailTileSize: regions.detailTileSize,
        segments: regions.segments,
        pageType,
        ownerSegmentId,
      };
    },
  });
}
