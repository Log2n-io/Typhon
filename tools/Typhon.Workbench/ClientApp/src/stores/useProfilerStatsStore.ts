import { create } from 'zustand';
import type { SelectionStats } from '@/libs/profiler/stats/selectionStats';

interface ProfilerStatsState {
  stats: SelectionStats | null;
  setStats: (stats: SelectionStats | null) => void;
  clear: () => void;
}

/**
 * Single source of truth for the viewport's aggregated SelectionStats. Producer is
 * `useProfilerStatsWriter` (called once from `ProfilerPanel`); consumers (`TopSpansPanel`,
 * `ProfilerDetail.RangeStatsDetail`) subscribe here so the same compute fans out instead of
 * re-running per panel. Cleared on session change by `ProfilerPanel`'s existing wipe path.
 */
export const useProfilerStatsStore = create<ProfilerStatsState>()((set) => ({
  stats: null,
  setStats: (stats) => set({ stats }),
  clear: () => set({ stats: null }),
}));
