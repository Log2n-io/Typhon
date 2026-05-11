import { create } from 'zustand';
import type { QueryExecutionDto } from '@/api/generated/model/queryExecutionDto';

export interface QueryPlanFocus {
  kind: number;
  localId: number;
}

export type QueryPlanMode = 'structural' | 'execution';

interface QueryPlanState {
  focus: QueryPlanFocus | null;
  mode: QueryPlanMode;
  /** Execution selected for execution-mode rendering. When non-null, mode flips to 'execution'. */
  selectedExecution: QueryExecutionDto | null;
  setFocus(focus: QueryPlanFocus | null): void;
  setMode(mode: QueryPlanMode): void;
  setSelectedExecution(execution: QueryExecutionDto | null): void;
  reset(): void;
}

const initial: Pick<QueryPlanState, 'focus' | 'mode' | 'selectedExecution'> = {
  focus: null,
  mode: 'structural',
  selectedExecution: null,
};

export const useQueryPlanStore = create<QueryPlanState>((set) => ({
  ...initial,
  setFocus: (focus) => set({ focus, selectedExecution: null, mode: 'structural' }),
  setMode: (mode) => set({ mode }),
  setSelectedExecution: (execution) => set({
    selectedExecution: execution,
    mode: execution ? 'execution' : 'structural',
  }),
  reset: () => set({ ...initial }),
}));
