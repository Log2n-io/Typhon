import { useCallback, useEffect, useMemo, useRef } from 'react';
import { useEventSource } from '@/hooks/streams/useEventSource';
import {
  useProfilerSessionStore,
  type ConnectionStatus,
  type LiveStreamPayload,
  type LiveThreadInfo,
} from '@/stores/useProfilerSessionStore';
import type {
  ChunkManifestEntryDto,
  GlobalMetricsDto,
  ProfilerMetadataDto,
  TickSummaryDto,
} from '@/api/generated/model';

/**
 * SSE subscription for the profiler live delta stream (#289 unified pipeline; retyped for #308).
 *
 * Wraps {@link useEventSource} with typed event listeners — each delta arrives on its own SSE
 * event channel (`metadata`, `tickSummaryAdded`, `chunkAdded`, `globalMetricsUpdated`,
 * `threadInfoAdded`, `heartbeat`, `shutdown`) instead of switching on a discriminator inside the
 * payload. The hook reconstructs the in-store `LiveStreamPayload` union shape (which still carries
 * `kind`) before pushing into the rAF-batched buffer, so {@link useProfilerSessionStore.applyLiveBatch}
 * sees the same union it always has.
 *
 * **rAF-coalesced batching.** Each SSE message handler runs synchronously on the main thread; under heavy ingest
 * (many chunkAdded + tickSummaryAdded per second from a busy engine), one-mutation-per-event meant N×O(N)
 * `[...prev, entry]` array spreads + N×subscriber notifications per frame, which stuttered the UI. We now buffer
 * incoming events in a ref and flush them via `requestAnimationFrame` so each native paint cycle applies AT MOST
 * one batched mutation. The `applyLiveBatch` store action collapses the N appends into a single O(N+batchSize)
 * spread + a single subscriber notification, regardless of how many events landed in the frame.
 */
export function useProfilerLiveStream(sessionId: string | null) {
  const applyLiveBatch = useProfilerSessionStore((s) => s.applyLiveBatch);

  // Buffered events accumulated between rAF flushes. Lives in a ref because:
  //   - Mutating it must NOT trigger a React re-render (we only re-render via the store mutation in flush()).
  //   - Identity stability lets the rAF callback read the latest batch without React closures going stale.
  const bufferRef = useRef<LiveStreamPayload[]>([]);
  const rafIdRef = useRef<number>(0);

  const flush = useCallback(() => {
    rafIdRef.current = 0;
    const batch = bufferRef.current;
    if (batch.length === 0) return;
    bufferRef.current = [];
    applyLiveBatch(batch);
  }, [applyLiveBatch]);

  const enqueue = useCallback(
    (event: LiveStreamPayload) => {
      bufferRef.current.push(event);
      if (rafIdRef.current === 0) {
        rafIdRef.current = requestAnimationFrame(flush);
      }
    },
    [flush],
  );

  const listeners = useMemo(
    () => ({
      metadata: (data: { metadata: ProfilerMetadataDto }) => enqueue({ kind: 'metadata', metadata: data.metadata }),
      tickSummaryAdded: (data: { tickSummary: TickSummaryDto }) =>
        enqueue({ kind: 'tickSummaryAdded', tickSummary: data.tickSummary }),
      chunkAdded: (data: { chunkEntry: ChunkManifestEntryDto }) =>
        enqueue({ kind: 'chunkAdded', chunkEntry: data.chunkEntry }),
      threadInfoAdded: (data: { threadInfo: LiveThreadInfo }) =>
        enqueue({ kind: 'threadInfoAdded', threadInfo: data.threadInfo }),
      globalMetricsUpdated: (data: { globalMetrics: GlobalMetricsDto }) =>
        enqueue({ kind: 'globalMetricsUpdated', globalMetrics: data.globalMetrics }),
      heartbeat: (data: { status: ConnectionStatus }) => enqueue({ kind: 'heartbeat', status: data.status }),
      shutdown: (data: { status: string }) => enqueue({ kind: 'shutdown', status: data.status ?? 'disconnected' }),
    }),
    [enqueue],
  );

  // Cancel any pending flush on unmount / session change so a late-firing rAF can't hit a stale store.
  useEffect(() => {
    return () => {
      if (rafIdRef.current !== 0) {
        cancelAnimationFrame(rafIdRef.current);
        rafIdRef.current = 0;
      }
      bufferRef.current = [];
    };
  }, [sessionId]);

  const url = sessionId ? `/api/sessions/${sessionId}/profiler/stream` : null;
  return useEventSource(url, listeners);
}
