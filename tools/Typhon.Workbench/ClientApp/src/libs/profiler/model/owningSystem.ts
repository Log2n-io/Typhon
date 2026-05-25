import type { ChunkSpan, ProfilerSelection } from './traceModel';

/**
 * Resolve the **owning system** of a profiler selection — the system name to project onto the unified bus
 * (`useSelectionStore.setSystem`) so the Inspector resolves `System ⊃ Span` and, from Stage 3 Phase 3, the
 * scheduling cluster (System DAG / Critical Path / Data Flow) highlights the same system.
 *
 * A {@link import('./traceModel').SpanData} carries only its `threadSlot`, never a system — a span's system is
 * the {@link ChunkSpan} (a system's execution slot) occupying that slot at the span's instant. Hence:
 *  - **chunk** → its `systemName` directly (a chunk *is* a system slot).
 *  - **span** (incl. mini-row ops, which are spans under the hood) → the chunk on the same `threadSlot` whose
 *    window contains the span's start. **Infra spans** (WAL, checkpoint, page-cache, GC) run on dedicated
 *    engine threads with no scheduler chunk → `null` (no owning system).
 *  - **tick / phase / phase-marker / marker** → `null` (not system-scoped).
 *
 * Pure over `chunks`; the caller passes the loaded ticks' chunks. Returning `null` clears the projection,
 * which is the correct behaviour for a selection that has no single owning system.
 */
export function resolveOwningSystem(selection: ProfilerSelection, chunks: readonly ChunkSpan[]): string | null {
  if (selection.kind === 'chunk') {
    return selection.chunk.systemName;
  }
  if (selection.kind !== 'span') {
    return null;
  }
  const span = selection.span;
  for (const c of chunks) {
    if (c.threadSlot === span.threadSlot && span.startUs >= c.startUs && span.startUs < c.endUs) {
      return c.systemName;
    }
  }
  return null;
}
