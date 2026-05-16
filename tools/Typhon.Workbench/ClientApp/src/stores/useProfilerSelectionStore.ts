import { create } from 'zustand';
import type { ChunkSpan, MarkerSelection, OffCpuInterval, PhaseMarker, PhaseSpan, SpanData } from '@/libs/profiler/model/traceModel';

/**
 * Discriminated union of profiler-panel selections. The profiler can select seven kinds of things, each
 * mapped to its own kind tag so DetailPanel's render branches know exactly what they're looking at:
 *
 *  - **span**: a nested span inside a scheduler chunk (Transaction.Commit, BTree.Insert, etc.)
 *  - **chunk**: a top-level scheduler chunk (a system's execution slot inside a tick)
 *  - **tick**: a whole tick on the overview strip
 *  - **marker**: a discrete event instant (GC, memory alloc event)
 *  - **phase**: a tick lifecycle phase span (RuntimePhaseSpan — WriteTickFence, UoW Flush, OutputPhase, etc.)
 *  - **phase-marker**: a single-point lifecycle landmark (UoW Create / UoW Flush glyph in the phase track)
 *  - **off-cpu**: an off-CPU interval — a gap where a thread was switched out (overlay bar on a thread lane)
 */
export type ProfilerSelection =
  | { kind: 'span'; span: SpanData }
  | { kind: 'chunk'; chunk: ChunkSpan }
  | { kind: 'tick'; tickNumber: number }
  | { kind: 'marker'; marker: MarkerSelection }
  | { kind: 'phase'; phase: PhaseSpan; tickNumber: number }
  | { kind: 'phase-marker'; marker: PhaseMarker; tickNumber: number }
  | { kind: 'off-cpu'; interval: OffCpuInterval };

interface ProfilerSelectionState {
  selected: ProfilerSelection | null;
  /**
   * `Date.now()` at which `selected` last changed. Consumed by DetailPanel's recency arbitration so it can
   * pick whichever selection (field / resource / profiler) was touched most recently. Never decreases.
   */
  touchedAt: number;
  setSelected: (selection: ProfilerSelection) => void;
  clear: () => void;
}

export const useProfilerSelectionStore = create<ProfilerSelectionState>()((set) => ({
  selected: null,
  touchedAt: 0,
  setSelected: (selection) => set({ selected: selection, touchedAt: Date.now() }),
  clear: () => set({ selected: null, touchedAt: Date.now() }),
}));
