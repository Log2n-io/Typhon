import { useQuery } from '@tanstack/react-query';
import { useSessionStore } from '@/stores/useSessionStore';
import type { DbMapData, StorageRegionDto, StorageRegionsDto } from '@/libs/dbmap/types';

// Fetches the coarse Database File Map (Module 15, Track A — A1). One TanStack Query call fetches both
// endpoints (`/dbmap/regions` for metadata + segment table, `/dbmap/region` for the per-page SoA buffers) and
// assembles the decoded `DbMapData` the renderer consumes. Raw-fetch with token injection — the useTrack.ts
// pattern.

function decodeBase64(b64: string): Uint8Array {
  const binary = atob(b64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

async function fetchJson<T>(url: string, token: string | null, signal: AbortSignal): Promise<T> {
  const headers = new Headers();
  if (token) {
    headers.set('X-Session-Token', token);
  }
  headers.set('X-Workbench-Api', '1');
  const res = await fetch(url, { signal, headers });
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

export function useDbMap(sessionId: string | null) {
  const token = useSessionStore((s) => s.token);

  return useQuery<DbMapData | null, Error>({
    queryKey: ['dbmap', sessionId],
    enabled: !!sessionId,
    staleTime: 30_000,
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
        pageCount: regions.dataFilePageCount,
        walBytes: regions.walBytes,
        hilbertOrder: regions.hilbertOrder,
        checkpointLsn: regions.checkpointLsn,
        segments: regions.segments,
        pageType,
        ownerSegmentId,
      };
    },
  });
}
