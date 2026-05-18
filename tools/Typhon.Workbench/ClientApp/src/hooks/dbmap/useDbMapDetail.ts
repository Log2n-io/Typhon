import { useQueries, useQuery } from '@tanstack/react-query';
import { useSessionStore } from '@/stores/useSessionStore';
import { decodeBase64, decodeInt32, decodeUint16, fetchJson } from '@/libs/dbmap/dbMapFetch';
import type {
  DbChunkContent,
  DbContentCell,
  DbDetailTile,
  DbPageDetail,
  StorageChunkDto,
  StoragePageDetailDto,
  StorageRegionDetailDto,
} from '@/libs/dbmap/types';

// Database File Map detail-tier hooks (Module 15, Track A — A2). Detail data is fetched on demand for the
// visible region only — never the whole file — and cached by TanStack Query keyed on (session, node / page /
// chunk), so panning back over a resident tile never refetches (AC7).

const DETAIL_STALE_MS = 30_000;

function cellsOf(cells: { label: string; value: string; kind: string; offset: number; size: number; colorKey: number }[]): DbContentCell[] {
  return cells.map((c) => ({ label: c.label, value: c.value, kind: c.kind, offset: c.offset, size: c.size, colorKey: c.colorKey }));
}

function decodeTile(dto: StorageRegionDetailDto): DbDetailTile {
  return {
    node: dto.node,
    firstPage: dto.firstPage,
    pageCount: dto.pageCount,
    fillRatio: decodeBase64(dto.fillRatio),
    changeRevision: decodeInt32(dto.changeRevision),
    crcStatus: decodeBase64(dto.crcStatus),
    residency: decodeBase64(dto.residency),
    chunkUsed: decodeUint16(dto.chunkUsed),
    chunkTotal: decodeUint16(dto.chunkTotal),
    maxChangeRevision: dto.maxChangeRevision,
    entropy: decodeBase64(dto.entropy),
    byteClass: decodeBase64(dto.byteClass),
  };
}

function decodePage(dto: StoragePageDetailDto): DbPageDetail {
  return {
    pageIndex: dto.pageIndex,
    byteOffset: dto.byteOffset,
    pageType: dto.pageType,
    ownerSegmentId: dto.ownerSegmentId,
    ownerSegmentKind: dto.ownerSegmentKind,
    changeRevision: dto.changeRevision,
    formatRevision: dto.formatRevision,
    modificationCounter: dto.modificationCounter,
    storedChecksum: dto.storedChecksum,
    liveChecksum: dto.liveChecksum,
    crcStatus: dto.crcStatus,
    residency: dto.residency,
    dirtyCounter: dto.dirtyCounter,
    chunkUsed: dto.chunkUsed,
    chunkTotal: dto.chunkTotal,
    fillRatio: dto.fillRatio,
    firstChunkId: dto.firstChunkId,
    chunkOccupancy: dto.chunkOccupancy ? decodeBase64(dto.chunkOccupancy) : new Uint8Array(0),
    directoryEntries: cellsOf(dto.directoryEntries ?? []),
  };
}

function decodeChunk(dto: StorageChunkDto): DbChunkContent {
  return {
    segmentId: dto.segmentId,
    chunkId: dto.chunkId,
    decoder: dto.decoder,
    occupied: dto.occupied,
    byteOffset: dto.byteOffset,
    size: dto.size,
    componentType: dto.componentType,
    cells: cellsOf(dto.cells ?? []),
  };
}

/** Fetches the detail tiles for the given quadtree-node ids; returns a node→tile map. Resident tiles are cached. */
export function useDbMapTiles(sessionId: string | null, nodes: readonly number[]): Map<number, DbDetailTile> {
  const token = useSessionStore((s) => s.token);
  return useQueries({
    queries: nodes.map((node) => ({
      queryKey: ['dbmap-tile', sessionId, node] as const,
      enabled: !!sessionId,
      staleTime: DETAIL_STALE_MS,
      refetchOnWindowFocus: false,
      queryFn: async ({ signal }: { signal: AbortSignal }) => {
        const dto = await fetchJson<StorageRegionDetailDto>(
          `/api/sessions/${sessionId}/dbmap/region/detail?node=${node}`,
          token,
          signal,
        );
        return decodeTile(dto);
      },
    })),
    combine: (results) => {
      const map = new Map<number, DbDetailTile>();
      results.forEach((r, i) => {
        if (r.data) {
          map.set(nodes[i], r.data);
        }
      });
      return map;
    },
  });
}

/** Fetches the per-page detail for the given page indices; returns a page→detail map. Resident pages are cached. */
export function useDbMapPages(sessionId: string | null, pageIndices: readonly number[]): Map<number, DbPageDetail> {
  const token = useSessionStore((s) => s.token);
  return useQueries({
    queries: pageIndices.map((idx) => ({
      queryKey: ['dbmap-page', sessionId, idx] as const,
      enabled: !!sessionId,
      staleTime: DETAIL_STALE_MS,
      refetchOnWindowFocus: false,
      queryFn: async ({ signal }: { signal: AbortSignal }) => {
        const dto = await fetchJson<StoragePageDetailDto>(`/api/sessions/${sessionId}/dbmap/page/${idx}`, token, signal);
        return decodePage(dto);
      },
    })),
    combine: (results) => {
      const map = new Map<number, DbPageDetail>();
      results.forEach((r, i) => {
        if (r.data) {
          map.set(pageIndices[i], r.data);
        }
      });
      return map;
    },
  });
}

/** Fetches the L4 content for the given chunk references; returns a `segId:chunkId`→content map. */
export function useDbMapChunks(
  sessionId: string | null,
  refs: readonly { segId: number; chunkId: number }[],
): Map<string, DbChunkContent> {
  const token = useSessionStore((s) => s.token);
  return useQueries({
    queries: refs.map((ref) => ({
      queryKey: ['dbmap-chunk', sessionId, ref.segId, ref.chunkId] as const,
      enabled: !!sessionId,
      staleTime: DETAIL_STALE_MS,
      refetchOnWindowFocus: false,
      queryFn: async ({ signal }: { signal: AbortSignal }) => {
        const dto = await fetchJson<StorageChunkDto>(
          `/api/sessions/${sessionId}/dbmap/chunk/${ref.segId}/${ref.chunkId}`,
          token,
          signal,
        );
        return decodeChunk(dto);
      },
    })),
    combine: (results) => {
      const map = new Map<string, DbChunkContent>();
      results.forEach((r, i) => {
        if (r.data) {
          map.set(`${refs[i].segId}:${refs[i].chunkId}`, r.data);
        }
      });
      return map;
    },
  });
}

/** Fetches one page's full decode for the Detail panel. */
export function useDbMapPage(sessionId: string | null, pageIndex: number | null) {
  const token = useSessionStore((s) => s.token);
  return useQuery<DbPageDetail | null, Error>({
    queryKey: ['dbmap-page', sessionId, pageIndex],
    enabled: !!sessionId && pageIndex != null,
    staleTime: DETAIL_STALE_MS,
    refetchOnWindowFocus: false,
    queryFn: async ({ signal }) => {
      const dto = await fetchJson<StoragePageDetailDto>(
        `/api/sessions/${sessionId}/dbmap/page/${pageIndex}`,
        token,
        signal,
      );
      return decodePage(dto);
    },
  });
}

/** Fetches one chunk's L4 decode for the Detail panel. */
export function useDbMapChunk(sessionId: string | null, segId: number | null, chunkId: number | null) {
  const token = useSessionStore((s) => s.token);
  return useQuery<DbChunkContent | null, Error>({
    queryKey: ['dbmap-chunk', sessionId, segId, chunkId],
    enabled: !!sessionId && segId != null && chunkId != null,
    staleTime: DETAIL_STALE_MS,
    refetchOnWindowFocus: false,
    queryFn: async ({ signal }) => {
      const dto = await fetchJson<StorageChunkDto>(
        `/api/sessions/${sessionId}/dbmap/chunk/${segId}/${chunkId}`,
        token,
        signal,
      );
      return decodeChunk(dto);
    },
  });
}
