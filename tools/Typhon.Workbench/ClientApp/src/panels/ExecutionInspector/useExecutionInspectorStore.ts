import { create } from 'zustand';

export interface ExecutionInspectorFocus {
  kind: number;
  localId: number;
}

export interface ExecutionInspectorSelection {
  tickIndex: number;
  systemId: number;
}

interface ExecutionInspectorState {
  /** Definition the inspector is currently scoped to — set by Catalog row affordance or store hand-off. */
  focus: ExecutionInspectorFocus | null;
  /** Currently highlighted execution within {@link focus}'s list. Null = "first in list" (sentinel). */
  selected: ExecutionInspectorSelection | null;
  setFocus(focus: ExecutionInspectorFocus | null): void;
  setSelected(selection: ExecutionInspectorSelection | null): void;
  reset(): void;
}

const initial: Pick<ExecutionInspectorState, 'focus' | 'selected'> = {
  focus: null,
  selected: null,
};

export const useExecutionInspectorStore = create<ExecutionInspectorState>((set) => ({
  ...initial,
  setFocus: (focus) => set({ focus, selected: null }),
  setSelected: (selected) => set({ selected }),
  reset: () => set({ ...initial }),
}));
