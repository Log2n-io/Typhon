import { create } from 'zustand';
import type { NavTarget } from '@/libs/profiler/correlation/causalFlow';
import { NO_REQUEST_FILTER, type RequestFilter, type RequestRow } from '@/libs/profiler/correlation/requests';
import type { ProfilerSelection, SpanData } from '@/libs/profiler/model/traceModel';

/**
 * A request to bring something into view in the time area (#1517): a place already resolved to its track and row, or a span (resolved by the time
 * area from its thread slot and render depth), optionally selecting something on arrival. The time area records the view it leaves in the navigation
 * history before selecting, so Back returns to it. `seq` makes a repeat request a new one.
 */
export type NavRequestBody = { kind: 'target'; target: NavTarget; select?: ProfilerSelection } | { kind: 'span'; span: SpanData };
export type NavRequest = { seq: number } & NavRequestBody;

interface ProfilerRequestsState {
  /** Every root in the loaded ticks (#1517), by start time: the time area's root lanes draw these. Producer: `useProfilerRequestsWriter`. */
  allRows: RequestRow[];
  /** The roots overlapping the committed viewport: the Requests panel lists these. Same producer. */
  rows: RequestRow[];
  /** The causal key of the selected root: the time area highlights the spans carrying it and draws its flow. Null when none is selected. */
  selectedKey: string | null;
  /** The root under the pointer in the time area's root lanes; previews like a selection and wins over it while set. */
  hoverKey: string | null;
  /** The spans sharing the selected span's causal key, in start order, for the Details pane's links. Same producer. */
  related: { key: string; spans: SpanData[] } | null;
  /** The pending bring-into-view request; the time area consumes it. */
  nav: NavRequest | null;
  /** Narrows both the Requests panel's list and what the time area's root lanes pack. */
  filter: RequestFilter;
  setFilter: (patch: Partial<RequestFilter>) => void;
  setRows: (allRows: RequestRow[], rows: RequestRow[]) => void;
  setRelated: (related: { key: string; spans: SpanData[] } | null) => void;
  select: (key: string | null) => void;
  hover: (key: string | null) => void;
  navigate: (request: NavRequestBody) => void;
  clear: () => void;
}

let navSeq = 0;

export const useProfilerRequestsStore = create<ProfilerRequestsState>()((set) => ({
  allRows: [],
  rows: [],
  selectedKey: null,
  hoverKey: null,
  related: null,
  nav: null,
  filter: NO_REQUEST_FILTER,
  setFilter: (patch) => set((s) => ({ filter: { ...s.filter, ...patch } })),
  setRows: (allRows, rows) => set({ allRows, rows }),
  setRelated: (related) => set({ related }),
  select: (selectedKey) => set({ selectedKey }),
  hover: (hoverKey) => set((s) => (s.hoverKey === hoverKey ? s : { hoverKey })),
  navigate: (request) => set({ nav: { ...request, seq: ++navSeq } }),
  clear: () => set({ allRows: [], rows: [], selectedKey: null, hoverKey: null, related: null, nav: null, filter: NO_REQUEST_FILTER }),
}));
