import { create } from 'zustand';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';
import type { Camera } from '@/libs/dbmap/camera';
import { useProfilerViewStore } from './useProfilerViewStore';
import { restoreDbMapCamera } from '../shell/commands/openDbMap';
import type { ProfilerSelection } from '@/libs/profiler/model/traceModel';
import { useSelectedResourceStore, type SelectedResource } from './useSelectedResourceStore';
import { useSchemaInspectorStore } from './useSchemaInspectorStore';
import { useDataBrowserStore } from './useDataBrowserStore';
import { useSelectionStore, type SelectionLeaf } from './useSelectionStore';
import { focusPanelById } from './navFocusBridge';

/**
 * `panelId` is the dockview panel that held focus *at this location* (IA §3.2 / §5.3). Carried on every
 * entry so back/forward can restore focus to where you navigated **from**, not just the selection
 * (conformance B.2). Stamped at push time for `bus-leaf` (the active panel when the selection happened)
 * and set explicitly for `panel-opened` (the panel the handoff focused). Optional — profiler/dbmap/resource
 * entries restore their own viewport/target instead.
 */
export type NavEntry = { panelId?: string } & (
  | { kind: 'resource-selected'; resourceId: string; selected: SelectedResource; timestamp: number }
  /**
   * A unified selection-bus leaf (Stage 1, #373). Pushed for every object-type selection that lacks
   * its own viewport-carrying entry (component, field, archetype, entity, system, query, index) —
   * closing the old G9 gap where schema/data selections never entered nav history. Restore re-drives
   * the source store so the navigator + Inspector + bus all re-target.
   */
  | { kind: 'bus-leaf'; leaf: SelectionLeaf; timestamp: number }
  /**
   * A view transition — a handoff/reveal that opened or focused a deep panel (Archetype/Component
   * Inspector, Data Browser, File-Map reveal). `panelId` is the destination; `leaf` snapshots the bus
   * selection at that moment so the restore is **self-sufficient** (re-selects + re-focuses on its own,
   * independent of the traversal path). This is the half that lets back/forward restore *focus*, not
   * just selection (IA §5.3, conformance B.2).
   */
  | { kind: 'panel-opened'; panelId: string; leaf: SelectionLeaf | null; timestamp: number }
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
  | { kind: 'dbmap-navigated'; camera: Camera; label: string; timestamp: number }
);

const CAPACITY = 100;

interface NavHistoryState {
  entries: NavEntry[];
  pointer: number;
  canBack: boolean;
  canForward: boolean;
  isRestoring: boolean;
  push: (entry: NavEntry) => void;
  /**
   * Record a selection (the navigator/bus drove a new leaf). **View-granular**: if the top entry is for the
   * *same view* (its `panelId` equals the now-active panel), we update that entry's leaf in place — selecting
   * within a view doesn't add a Back stop (browser-like: only navigating to a new view does). Otherwise we
   * push a new `bus-leaf` entry. No-op while restoring.
   */
  recordSelection: (leaf: SelectionLeaf, panelId: string | undefined) => void;
  /**
   * Record a view transition (a handoff/reveal that opened or focused a deep panel) — always a new Back stop.
   * Pushes a `panel-opened` entry for the destination, carrying the current leaf so its restore re-selects +
   * re-focuses self-sufficiently. The origin view is the entry below (recorded by {@link recordSelection}),
   * so one Back returns to it. No-op while restoring.
   */
  recordViewTransition: (panelId: string, leaf: SelectionLeaf | null) => void;
  /**
   * Record a cross-panel File Map fly-to (§13 A4 AC2). A reveal *opens* the File Map (a `panel-opened`
   * entry from {@link recordViewTransition}) and then *flies the camera* to the target — two events for one
   * navigation. So when the top entry is the just-opened File Map view (`panel-opened` for the same panel),
   * we **replace** it with this camera entry (folding open+fly into one Back stop, carrying the panel so
   * focus restores too). Otherwise — a later bookmark/cross-link jump while already in the map — we append,
   * preserving the map's own retraceable camera history. No-op while restoring.
   */
  recordDbMapNav: (camera: Camera, label: string, panelId: string) => void;
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
  } else if (entry.kind === 'panel-opened') {
    // A recorded view transition. Re-drive the snapshot selection (so the panel reads the right bus
    // leaf when focused below); the panel itself is re-focused at the end. The leaf snapshot makes
    // this self-sufficient regardless of how we arrived at this entry.
    if (entry.leaf) {
      restoreLeaf(entry.leaf);
    }
  } else if (entry.kind === 'profiler-selected') {
    // Restore both: the selection drives DetailPanel recency, the viewRange drives TimeArea's
    // viewport + TickOverview's orange overlay. A null selection means "at this viewport the user
    // hadn't selected anything yet" — clearLeaf() resets the bus leaf without planting a fake
    // tick-0 entry. 3E: the profiler selection lives on the unified bus leaf (silo retired).
    if (entry.selection === null) {
      useSelectionStore.getState().clearLeaf();
    } else {
      useSelectionStore.getState().select(entry.selection.kind === 'tick' ? 'tick' : 'span', entry.selection);
    }
    // Snap the viewport to the target range — back/forward is a teleport to a prior place, not a
    // smooth journey (browser semantics). This used to call animateViewportToRange (an 800 ms
    // ease-out tween): harmless while every tick shared one viewport, but #345 gave each tick its
    // own framing, so the tween turned into a visible slide between adjacent ticks on every
    // back/forward — a regression. commitViewRange writes both slots atomically so TimeArea +
    // TickOverview update on the next frame. (jumpToTimeRange keeps the tween — that is a single,
    // deliberate jump where easing reads as intentional, not as lag.)
    useProfilerViewStore.getState().commitViewRange(entry.viewRange);
  } else if (entry.kind === 'dbmap-navigated') {
    // Flies the map camera back to the recorded framing; a no-op when the File Map panel is not mounted.
    restoreDbMapCamera(entry.camera);
  }
  // Restore focus to the panel that held it at this location (IA §3.2 / §5.3, conformance B.2). After the
  // selection re-drive above, so the focused panel reads the correct bus leaf. Only the bus-driven entries
  // carry a panel; profiler/dbmap restore their own viewport (which implies their panel) and resource keeps
  // today's behaviour. A no-op when the panel is gone / not mounted (focusPanelById guards getPanel).
  if (entry.panelId && (entry.kind === 'bus-leaf' || entry.kind === 'panel-opened' || entry.kind === 'dbmap-navigated')) {
    focusPanelById(entry.panelId);
  }
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

  recordSelection: (leaf, panelId) =>
    set((s) => {
      if (s.isRestoring) return s;
      const top = s.pointer >= 0 ? s.entries[s.pointer] : null;
      // Same view (a defined active panel matching the top leaf-entry's panel) → update its leaf in place,
      // truncating any forward history (a fresh selection after Back discards the redo branch, like push).
      // So selecting within a view tracks the current object without adding a Back stop.
      const sameView =
        top != null && panelId != null && top.panelId === panelId && (top.kind === 'bus-leaf' || top.kind === 'panel-opened');
      if (sameView) {
        const kept = s.entries.slice(0, s.pointer + 1);
        kept[s.pointer] = { ...kept[s.pointer], leaf } as NavEntry;
        return { entries: kept, ...deriveFlags(kept, s.pointer) };
      }
      const entry: NavEntry = { kind: 'bus-leaf', leaf, panelId, timestamp: Date.now() };
      const kept = s.entries.slice(0, s.pointer + 1);
      const next = [...kept, entry].slice(-CAPACITY);
      const pointer = next.length - 1;
      return { entries: next, pointer, ...deriveFlags(next, pointer) };
    }),

  recordViewTransition: (panelId, leaf) =>
    set((s) => {
      if (s.isRestoring) return s;
      // A view transition is always a new Back stop — the origin view is the entry below it.
      const entry: NavEntry = { kind: 'panel-opened', panelId, leaf, timestamp: Date.now() };
      const kept = s.entries.slice(0, s.pointer + 1);
      const next = [...kept, entry].slice(-CAPACITY);
      const pointer = next.length - 1;
      return { entries: next, pointer, ...deriveFlags(next, pointer) };
    }),

  recordDbMapNav: (camera, label, panelId) =>
    set((s) => {
      if (s.isRestoring) return s;
      const entry: NavEntry = { kind: 'dbmap-navigated', camera, label, panelId, timestamp: Date.now() };
      const top = s.pointer >= 0 ? s.entries[s.pointer] : null;
      // The fly that immediately follows the reveal's open → fold into that one File Map Back stop.
      if (top != null && top.kind === 'panel-opened' && top.panelId === panelId) {
        const kept = s.entries.slice(0, s.pointer + 1);
        kept[s.pointer] = entry;
        return { entries: kept, ...deriveFlags(kept, s.pointer) };
      }
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
