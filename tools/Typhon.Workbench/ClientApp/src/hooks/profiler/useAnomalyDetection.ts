import { useEffect } from 'react';
import { useProfilerCache } from '@/hooks/profiler/useProfilerCache';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { detectAnomalies, DEFAULT_THRESHOLDS, type DetectThresholds } from '@/panels/EngineLiveHealth/anomalies';

/**
 * Side-effect hook (#377 Stage 4 Phase 3, GAP-21 jump). Runs the pure detector against the chunk
 * cache's tick array whenever it changes, and pushes the result into `useProfilerSessionStore.
 * anomalies`. The hook returns nothing — the store IS the API. Consumers (`AnomalyLog`) read from
 * the store, so they don't pay for redundant computation when the panel toggles in / out.
 *
 * Why the store + side-effect pattern (vs. a `useDetectedAnomalies` derived hook returning the
 * list): persistence across panel mounts (the Engine Health panel can be hidden + reopened without
 * losing the anomaly log), and a single source of truth for the future "mark seen" / "jump to last
 * unseen" affordances. The detector itself is O(n) so we cap the cost at one pass per cache update.
 *
 * `setAnomalies` does a cheap length + per-row tickNumber/kind equality check before triggering a
 * store mutation — steady-state ticks produce identical output, no subscriber notification needed.
 */
export function useAnomalyDetection(sessionId: string | null, isLive: boolean = true, thresholds: DetectThresholds = DEFAULT_THRESHOLDS): void {
  const { ticks } = useProfilerCache(sessionId, isLive);
  const setAnomalies = useProfilerSessionStore((s) => s.setAnomalies);

  useEffect(() => {
    if (!sessionId || ticks.length === 0) {
      setAnomalies([]);
      return;
    }
    const detected = detectAnomalies(ticks, thresholds);
    setAnomalies(detected);
  }, [sessionId, ticks, setAnomalies, thresholds]);
}
