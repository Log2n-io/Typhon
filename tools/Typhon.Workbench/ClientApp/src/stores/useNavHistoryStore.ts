import { create } from 'zustand';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';
import type { Camera } from '@/libs/dbmap/camera';
import { animateViewportToRange } from '../shell/commands/profilerCommands';
import { restoreDbMapCamera } from '../shell/commands/openDbMap';
import { useProfilerSelectionStore, type ProfilerSelection } from './useProfilerSelectionStore';
import { useSelectedResourceStore, type SelectedResource } from './useSelectedResourceStore';
import { useSchemaInspectorStore } from './useSchemaInspectorStore';
import { useDataBrowserStore } from './useDataBrowserStore';
import { useSelectionStore, type SelectionLeaf } from './useSelectionStore';

export type NavEntry =
  | { kind: 'resource-selected'; resourceId: string; selected: SelectedResource; timestamp: number }
  /**
   * A unified selection-bus leaf (Stage 1, #373). Pushed for every object-type selection that lacks
   * its own viewport-carrying entry (component, field, archetype, entity, system, query, index) —
   * closing the old G9 gap where schema/data selections never entered nav history. Restore re-drives
   * the source store so the navigator + Inspector + bus all re-target.
   */
  | { kind: 'bus-leaf'; leaf: SelectionLeaf; timestamp: number }
  | { kind: 'panel-opened'; panelId: string; timestamp: number }
  /**
   * A profiler viewport snapshot with the selection that was active at that moment. Pushed on
   * viewport-changing actions (pan, zoom, drag-to-zoom, Ctrl+Home, etc.), not on selection alone.
   * Selection-only changes call {@link NavHistoryState.updateTopSelection} to patch the top entry
   * in place, so back/forward restores the latest span that was active *at* that viewport.
   */
  | { kind: 'profiler-selected'; selection: ProfilerSelection | null; viewRange: TimeRange; timestamp: number }
  /**
   * A Database File Map navigation (§13 A4 AC2) — pushed on a discrete fly-to (region-row / search /
   * pathology / cross-link / bookmark). Restoring flies the map camera back to the recorded framing.
   */
  | { kind: 'dbmap-navigated'; camera: Camera; label: string; timestamp: number };

const CAPACITY = 100;

interface NavHistoryState {
  entries: NavEntry[];
  pointer: number;
  canBack: boolean;
  canForward: boolean;
  isRestoring: boolean;
  push: (entry: NavEntry) => void;
  /**
   * Patch the top entry's selection without adding a new history entry. Used when the user clicks
   * a span/chunk at the current viewport — the viewport didn't change, so there's no new "place"
   * to navigate to, but we want back() to restore whatever span they had highlighted last. No-op
   * when the top entry is not `profiler-selected` or when the stack is empty.
   */
  updateTopSelection: (selection: ProfilerSelection | null) => void;
  back: () => void;
  forward: () => void;
  clear: () => void;
}

function deriveFlags(entries: NavEntry[], pointer: number) {
  return {
    canBack: pointer > 0,
    canForward: pointer >= 0 && pointer < entries.length - 1,
  };
}

/**
 * Re-drive the source store for a restored bus leaf, so back/forward restores the navigator highlight +
 * Inspector + bus together (the source setters mirror back to the bus). Runs under `isRestoring`, so the
 * mirror's nav-push is suppressed. Types without a source store go straight to the bus.
 */
function restoreLeaf(leaf: SelectionLeaf) {
  switch (leaf.type) {
    case 'component':
      useSchemaInspectorStore.getState().selectComponent(leaf.ref as string);
      break;
    case 'field': {
      const r = leaf.ref as { component: string | null; field: string };
      useSchemaInspectorStore.getState().selectComponent(r.component);
      useSchemaInspectorStore.getState().selectField(r.field);
      break;
    }
    case 'archetype':
      useDataBrowserStore.getState().setArchetype(leaf.ref as string);
      break;
    case 'entity': {
      const r = leaf.ref as { archetypeId: string | null; entityId: string };
      useDataBrowserStore.getState().setArchetype(r.archetypeId);
      useDataBrowserStore.getState().selectEntity(r.entityId);
      break;
    }
    default:
      // system / query / index (and any future type with no dedicated source store).
      useSelectionStore.getState().select(leaf.type, leaf.ref);
  }
}

function restoreSideEffect(entry: NavEntry) {
  if (entry.kind === 'resource-selected') {
    useSelectedResourceStore.getState().setSelected(entry.selected);
  } else if (entry.kind === 'bus-leaf') {
    restoreLeaf(entry.leaf);
  } else if (entry.kind === 'profiler-selected') {
    // Restore both: the selection drives DetailPanel recency, the viewRange drives TimeArea's
    // viewport + TickOverview's orange overlay. A null selection means "at this viewport the user
    // hadn't selected anything yet" — clear() resets the selection store without planting a fake
    // tick-0 entry.
    if (entry.selection === null) {
      useProfilerSelectionStore.getState().clear();
    } else {
      useProfilerSelectionStore.getState().setSelected(entry.selection);
    }
    // Animate the viewport to the target range so back/forward feels like the double-click zoom
    // tween (800 ms ease-out). Falls back to a snap via `setViewRange` when the profiler panel
    // isn't mounted. Selection update is synchronous above so DetailPanel reacts immediately; the
    // viewport eases in over 800 ms.
    animateViewportToRange(entry.viewRange);
  } else if (entry.kind === 'dbmap-navigated') {
    // Flies the map camera back to the recorded framing; a no-op when the File Map panel is not mounted.
    restoreDbMapCamera(entry.camera);
  }
  // panel-opened: no-op in Phase 5 (forward-compat).
}

export const useNavHistoryStore = create<NavHistoryState>()((set, get) => ({
  entries: [],
  pointer: -1,
  canBack: false,
  canForward: false,
  isRestoring: false,

  push: (entry) =>
    set((s) => {
      // During a restore (back/forward dispatch), downstream setSelected firing push() is a no-op.
      if (s.isRestoring) return s;
      const kept = s.entries.slice(0, s.pointer + 1);
      const next = [...kept, entry].slice(-CAPACITY);
      const pointer = next.length - 1;
      return { entries: next, pointer, ...deriveFlags(next, pointer) };
    }),

  updateTopSelection: (selection) =>
    set((s) => {
      // Restore-dispatch already writes the target selection directly to the selection store, so a
      // sync patch here would re-write the entry we're navigating to with itself — harmless but
      // chatty. Skipping when `isRestoring` keeps the entries reference stable across back/forward.
      if (s.isRestoring) return s;
      if (s.pointer < 0) return s;
      const top = s.entries[s.pointer];
      if (top.kind !== 'profiler-selected') return s;
      if (top.selection === selection) return s;
      const patched: NavEntry = { ...top, selection };
      const entries = s.entries.slice();
      entries[s.pointer] = patched;
      return { entries };
    }),

  back: () => {
    const s = get();
    if (!s.canBack) return;
    const pointer = s.pointer - 1;
    set({ isRestoring: true, pointer, ...deriveFlags(s.entries, pointer) });
    restoreSideEffect(s.entries[pointer]);
    set({ isRestoring: false });
  },

  forward: () => {
    const s = get();
    if (!s.canForward) return;
    const pointer = s.pointer + 1;
    set({ isRestoring: true, pointer, ...deriveFlags(s.entries, pointer) });
    restoreSideEffect(s.entries[pointer]);
    set({ isRestoring: false });
  },

  clear: () =>
    set({
      entries: [],
      pointer: -1,
      canBack: false,
      canForward: false,
      isRestoring: false,
    }),
}));
