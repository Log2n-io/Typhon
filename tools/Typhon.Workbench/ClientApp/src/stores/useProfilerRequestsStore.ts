import { create } from 'zustand';
import type { RequestRow } from '@/libs/profiler/correlation/requests';

interface ProfilerRequestsState {
  /** Every root in the loaded ticks (#1517), by start time: the time area's root lanes draw these. Producer: `useProfilerRequestsWriter`. */
  allRows: RequestRow[];
  /** The roots starting in the committed viewport: the Requests panel lists these. Same producer. */
  rows: RequestRow[];
  /** The causal key of the selected root: the time area highlights the spans carrying it and draws its flow. Null when none is selected. */
  selectedKey: string | null;
  /** The root under the pointer in the time area's root lanes; previews like a selection and wins over it while set. */
  hoverKey: string | null;
  setRows: (allRows: RequestRow[], rows: RequestRow[]) => void;
  select: (key: string | null) => void;
  hover: (key: string | null) => void;
  clear: () => void;
}

export const useProfilerRequestsStore = create<ProfilerRequestsState>()((set) => ({
  allRows: [],
  rows: [],
  selectedKey: null,
  hoverKey: null,
  setRows: (allRows, rows) => set({ allRows, rows }),
  select: (selectedKey) => set({ selectedKey }),
  hover: (hoverKey) => set((s) => (s.hoverKey === hoverKey ? s : { hoverKey })),
  clear: () => set({ allRows: [], rows: [], selectedKey: null, hoverKey: null }),
}));
