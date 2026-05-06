import { useMemo } from 'react';
import { useAggregations } from '@/hooks/data/useAggregations';
import type { AggregationQueryDto } from '@/api/generated/model/aggregationQueryDto';
import type { TickRange } from './useDagViewStore';

/**
 * Per-queue backpressure stats over a tick range, fetched in one batched /aggregate call:
 * `peakDepth` max, `endOfTickDepth` p50, `overflowCount` sum. Heat is relative across the
 * queues in the same fetch (max peak depth = 1.0, lowest = 0.0).
 *
 * **v1 limitation — no queue capacity on the wire.** Per `09-system-dag.md §4.5`, ideal
 * encoding normalises peak by capacity. The engine doesn't surface capacity yet (engine work,
 * tracked separately); we substitute a relative heat across the visible queues. This loses
 * absolute meaning ("how close to dropping?") but still surfaces "which queue is the worst."
 * Overflow count compensates: when it's non-zero, that's a binary catastrophe signal that
 * doesn't depend on capacity.
 */
export interface QueueBackpressureStat {
  /** max peakDepth in the range. */
  peakMax: number;
  /** median end-of-tick depth in the range. */
  endTickMedian: number;
  /** sum of overflowCount in the range — binary "events were dropped" signal. */
  overflowSum: number;
  /**
   * Peak-derived heat ∈ [0, 1] across the queues. Drives the **fill colour** per design §4.5
   * ("how close to dropping events at the worst moment"). Independent from {@link outlineHeat}
   * so the two channels answer two different questions.
   */
  heat: number;
  /**
   * End-of-tick-derived heat ∈ [0, 1] across the queues. Drives the **outline thickness** per
   * design §4.5 ("captures sustained backlog — thin = consumer keeping up; thick = chronically
   * lagging"). A queue with high peak but low median end-of-tick is hot in colour but thin in
   * outline (one-tick spike); the inverse is cool but thick (chronic-but-mild backlog).
   */
  outlineHeat: number;
}

export function useQueueBackpressure(
  sessionId: string | null,
  queueNames: string[],
  range: TickRange | null,
): Map<string, QueueBackpressureStat> {
  const queries = useMemo<AggregationQueryDto[]>(() => {
    if (!range || queueNames.length === 0) return [];
    const out: AggregationQueryDto[] = [];
    for (const name of queueNames) {
      const trackId = `queue/${name}`;
      out.push({ trackId, field: 'peakDepth', op: 'max', range: [range.from, range.to] });
      out.push({ trackId, field: 'endOfTickDepth', op: 'p50', range: [range.from, range.to] });
      out.push({ trackId, field: 'overflowCount', op: 'sum', range: [range.from, range.to] });
    }
    return out;
  }, [range, queueNames]);

  const { data } = useAggregations(sessionId, queries);

  return useMemo(() => {
    const map = new Map<string, QueueBackpressureStat>();
    if (!data?.results) return map;
    const raw: Array<{ name: string; peak: number; endTick: number; overflow: number }> = [];
    for (let i = 0; i < queueNames.length; i++) {
      const peak = numericValue(data.results[i * 3]?.value);
      const endTick = numericValue(data.results[i * 3 + 1]?.value);
      const overflow = numericValue(data.results[i * 3 + 2]?.value);
      if (peak == null) continue;
      raw.push({
        name: queueNames[i],
        peak,
        endTick: endTick ?? 0,
        overflow: overflow ?? 0,
      });
    }
    let peakMin = Number.POSITIVE_INFINITY;
    let peakMax = Number.NEGATIVE_INFINITY;
    let endTickMin = Number.POSITIVE_INFINITY;
    let endTickMax = Number.NEGATIVE_INFINITY;
    for (const r of raw) {
      if (r.peak < peakMin) peakMin = r.peak;
      if (r.peak > peakMax) peakMax = r.peak;
      if (r.endTick < endTickMin) endTickMin = r.endTick;
      if (r.endTick > endTickMax) endTickMax = r.endTick;
    }
    const peakSpan = peakMax - peakMin;
    const endTickSpan = endTickMax - endTickMin;
    for (const r of raw) {
      const heat = peakSpan > 0 ? (r.peak - peakMin) / peakSpan : 0;
      const outlineHeat = endTickSpan > 0 ? (r.endTick - endTickMin) / endTickSpan : 0;
      map.set(r.name, {
        peakMax: r.peak,
        endTickMedian: r.endTick,
        overflowSum: r.overflow,
        heat,
        outlineHeat,
      });
    }
    return map;
  }, [data, queueNames]);
}

function numericValue(v: number | string | null | undefined): number | null {
  if (v == null) return null;
  const n = typeof v === 'number' ? v : Number(v);
  return Number.isFinite(n) ? n : null;
}
