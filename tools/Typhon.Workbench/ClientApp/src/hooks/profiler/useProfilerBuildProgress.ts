import { useCallback, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useEventSource } from '@/hooks/streams/useEventSource';
import { useProfilerSessionStore, type BuildProgressPayload } from '@/stores/useProfilerSessionStore';

/**
 * SSE subscription for the profiler build-progress stream. Wraps the generic {@link useEventSource} hook to
 * dispatch payload frames into {@link useProfilerSessionStore}.
 *
 * Server emits frames as default `message` events (no `event:` prefix) with phase ∈ {building, done, error}
 * inside the JSON. After a terminal phase the server cleanly closes the SSE connection; the browser's
 * `EventSource` surfaces *every* close (clean or otherwise) as an `error`, which is why the hook tracks
 * `terminated` here and passes `null` for the URL once we're done — without that the underlying
 * {@link useEventSource} would auto-reconnect every 3 s and spam the server log forever.
 */
export function useProfilerBuildProgress(sessionId: string | null) {
  const setBuildProgress = useProfilerSessionStore((s) => s.setBuildProgress);
  const setBuildError = useProfilerSessionStore((s) => s.setBuildError);
  const [terminated, setTerminated] = useState<string | null>(null);
  const queryClient = useQueryClient();

  const onMessage = useCallback(
    (payload: BuildProgressPayload) => {
      if (payload.phase === 'error') {
        setBuildError(payload.message ?? 'Build failed.');
        setTerminated(sessionId);
        return;
      }
      setBuildProgress(payload);
      if (payload.phase === 'done') {
        setTerminated(sessionId);
        // The SSE signals build completion faster than the 2s polling interval in useProfilerMetadata.
        // Invalidate immediately so the overlay clears as soon as the cache is ready instead of
        // waiting up to 2s for the next scheduled poll — noticeable on files with an existing cache
        // where the build completes in <200ms but the first /metadata poll already returned 202.
        if (sessionId) {
          void queryClient.invalidateQueries({ queryKey: ['profiler', 'metadata', sessionId] });
        }
      }
    },
    [setBuildProgress, setBuildError, sessionId, queryClient],
  );

  // Once the build reaches a terminal phase for THIS session, stop subscribing — the server has
  // already closed and there's nothing more to receive. A new sessionId resets the gate so opening
  // a second trace re-subscribes its own build-progress stream.
  const isTerminated = terminated !== null && terminated === sessionId;
  const url = sessionId && !isTerminated ? `/api/sessions/${sessionId}/profiler/build-progress` : null;
  return useEventSource<BuildProgressPayload>(url, onMessage);
}
