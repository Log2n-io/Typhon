import { useMemo } from 'react';
import { useLiveGaugeData } from '@/hooks/profiler/useLiveGaugeData';
import { useTelemetrySession } from '@/hooks/profiler/useTelemetrySession';

/**
 * The realms that actually did spatial maintenance work in the current window.
 *
 * **Why this exists: a picker over 1 237 registered realms cannot be used.** The catalog lists every realm the engine
 * holds, and on a galaxy of sleeping interiors almost all of them are asleep and will stay asleep — so an alphabetical
 * list of all of them buries the handful anyone wants. The rate records already answer "which realms are doing
 * anything", because the engine emits one only for a realm the fence touched, so the busy set falls out of data the
 * session is already carrying.
 *
 * **Over the WINDOW, not the tick**, and that is the difference between a usable control and a flickering one. The
 * touched set changes every tick — a realm that migrated one entity this tick may be silent the next — so a list built
 * from one tick would reorder itself fifty times a second under the pointer. The window is the same 60 seconds the
 * rates are quoted over, so a realm stays listed for as long as its numbers are on screen.
 */
export interface ReportingRealms {
  /** Realm ids that reported at least one rate row in the window, ascending. Empty when the session has no telemetry. */
  readonly ids: readonly number[];
  /** Membership test for the list above, for a caller ordering a larger set. */
  readonly reported: (realmId: number) => boolean;
  /** Whether this session could answer at all — false means "no telemetry", not "nothing was busy". */
  readonly hasTelemetry: boolean;
}

const NONE: ReportingRealms = { ids: [], reported: () => false, hasTelemetry: false };

export function useReportingRealms(): ReportingRealms {
  const { sessionId, hasTelemetry } = useTelemetrySession();
  const { windowedTicks } = useLiveGaugeData(sessionId);

  return useMemo(() => {
    if (!hasTelemetry) {
      return NONE;
    }

    const seen = new Set<number>();
    for (const tick of windowedTicks) {
      if (!tick.spatialRatesByRealm) {
        continue;
      }
      for (const row of tick.spatialRatesByRealm.values()) {
        seen.add(row.realmId);
      }
    }

    const ids = [...seen].sort((a, b) => a - b);
    return { ids, reported: (realmId: number) => seen.has(realmId), hasTelemetry: true };
  }, [hasTelemetry, windowedTicks]);
}
