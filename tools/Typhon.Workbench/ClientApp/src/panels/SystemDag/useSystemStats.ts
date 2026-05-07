import { useMemo } from 'react';
import { useAggregations } from '@/hooks/data/useAggregations';
import type { AggregationQueryDto } from '@/api/generated/model/aggregationQueryDto';
import type { TickRange, StatMode } from './useDagViewStore';

/**
 * Per-system primary stat (µs, with relative heat) over a tick range. One batched POST to
 * `/api/sessions/{id}/aggregate` with N queries (one per system) — see
 * `10-internal-data-api.md §12.3`. Returned as a map for O(1) node lookup at render time.
 *
 * Heat is `(value - min) / (max - min)` clamped to [0, 1] across the systems in the same fetch
 * — purely visual, not statistical, so a single hot system doesn't drown the rest.
 */
export interface SystemStat {
  /** µs from the chosen aggregation operator (mean / p95 / etc.). */
  value: number;
  /** Relative heat ∈ [0, 1]. 0 = coldest in the batch, 1 = hottest. */
  heat: number;
}

export interface SystemStatsResult {
  stats: Map<string, SystemStat>;
  isLoading: boolean;
  error: Error | null;
}

export function useSystemStats(
  sessionId: string | null,
  systemNames: string[],
  range: TickRange | null,
  statMode: StatMode,
): SystemStatsResult {
  const queries = useMemo<AggregationQueryDto[]>(() => {
    if (!range || systemNames.length === 0) return [];
    return systemNames.map((name) => ({
      trackId: `system/${name}`,
      field: 'durationUs',
      op: statMode,
      range: [range.from, range.to],
    }));
  }, [range, statMode, systemNames]);

  const { data, isLoading, error } = useAggregations(sessionId, queries);

  const stats = useMemo(() => {
    const map = new Map<string, SystemStat>();
    if (!data?.results) return map;

    let min = Number.POSITIVE_INFINITY;
    let max = Number.NEGATIVE_INFINITY;
    const raw: Array<{ name: string; value: number }> = [];
    for (let i = 0; i < systemNames.length; i++) {
      const r = data.results[i];
      const v = typeof r?.value === 'number' ? r.value : null;
      if (v == null || !Number.isFinite(v)) continue;
      raw.push({ name: systemNames[i], value: v });
      if (v < min) min = v;
      if (v > max) max = v;
    }
    const span = max - min;
    for (const { name, value } of raw) {
      const heat = span > 0 ? (value - min) / span : 0;
      map.set(name, { value, heat });
    }
    return map;
  }, [data, systemNames]);

  return { stats, isLoading, error: (error as Error) ?? null };
}
