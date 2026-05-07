import { create } from 'zustand';

/**
 * Cross-panel hover state — currently a single slot (`hoveredSystem`) shared between the System
 * DAG canvas and the Critical Path tape per `09-system-dag.md §11 Phase 3` ("Hover bar in tape →
 * corresponding DAG node pulses; Hover DAG node (if on critical path): tape bar highlights").
 *
 * Kept separate from {@link useSelectionStore} because hover is volatile (cleared on
 * MouseLeave) and shouldn't round-trip through the URL or any persisted slot. Subscribers read
 * with a slot selector so non-relevant panels don't re-render on every mouseover.
 */
export interface HoverState {
  hoveredSystem: string | null;
  /**
   * Phase name currently being hovered in either the System DAG (swim-lane label) or the Critical
   * Path tape (phase stripe label). Same volatile-cross-panel pattern as `hoveredSystem` —
   * subscribers brighten the matching ribbon / lane when this matches their phase identity.
   */
  hoveredPhase: string | null;
  setHoveredSystem: (name: string | null) => void;
  setHoveredPhase: (phase: string | null) => void;
}

export const useHoverStore = create<HoverState>()((set, get) => ({
  hoveredSystem: null,
  hoveredPhase: null,
  setHoveredSystem: (name) => {
    if (get().hoveredSystem === name) return;
    set({ hoveredSystem: name });
  },
  setHoveredPhase: (phase) => {
    if (get().hoveredPhase === phase) return;
    set({ hoveredPhase: phase });
  },
}));
