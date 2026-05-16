import type { TimeRange } from '@/libs/profiler/model/uiTypes';

/**
 * A trace viewport remembered for one file, tagged with the trace fingerprint it was captured
 * against. Persisted per-file in `useRecentFilesStore`; consumed by {@link resolveInitialViewport}.
 */
export interface SavedViewport {
  /** SHA-256 fingerprint of the trace the viewport was captured against. */
  fingerprint: string;
  startUs: number;
  endUs: number;
}

/** Minimal metadata shape {@link resolveInitialViewport} needs — `ProfilerMetadataDto` is assignable to it. */
interface MetadataLike {
  fingerprint?: string | null;
  tickSummaries?: ReadonlyArray<{ startUs: unknown; durationUs: unknown }> | null;
}

/**
 * Picks the viewport to commit when a trace's metadata first lands:
 *
 *  - the per-file {@link SavedViewport}, **iff** its fingerprint still matches the loaded trace
 *    (identical content) — so reopening an unchanged file lands back where you left off;
 *  - otherwise the first tick's bounds — covers a never-seen file and a re-profiled file alike
 *    (its fingerprint no longer matches, so the stale µs-viewport is correctly discarded rather
 *    than restored out-of-range).
 *
 * Returns `null` when the trace carries no ticks yet (e.g. a live session before its first tick);
 * the caller then leaves the viewport at the `{0,0}` "no selection" sentinel.
 *
 * Pure — no store / DOM access — so the restore decision is unit-tested directly.
 */
export function resolveInitialViewport(metadata: MetadataLike, saved: SavedViewport | null): TimeRange | null {
  const summaries = metadata.tickSummaries;
  if (!summaries || summaries.length === 0) return null;

  // Restore the saved viewport only when the trace content is provably unchanged (fingerprint
  // match). A re-profiled file gets a fresh fingerprint, so this guard rejects the stale viewport.
  if (
    saved != null
    && metadata.fingerprint != null && metadata.fingerprint.length > 0
    && saved.fingerprint === metadata.fingerprint
    && Number.isFinite(saved.startUs) && Number.isFinite(saved.endUs)
    && saved.endUs > saved.startUs
  ) {
    return { startUs: saved.startUs, endUs: saved.endUs };
  }

  const first = summaries[0];
  const startUs = Number(first.startUs);
  const durationUs = Number(first.durationUs) || 1;
  if (!Number.isFinite(startUs)) return null;
  return { startUs, endUs: startUs + durationUs };
}
