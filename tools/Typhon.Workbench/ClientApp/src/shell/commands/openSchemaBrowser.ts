import type { DockviewApi } from 'dockview-react';
import { useProfilerSelectionStore } from '@/stores/useProfilerSelectionStore';
import { useSourceLocationStore } from '@/stores/useSourceLocationStore';
import { useDockLayoutStore } from '@/stores/useDockLayoutStore';
import { useSessionStore } from '@/stores/useSessionStore';

/**
 * Module-level dockview api registration — same pattern as refreshResourceGraph. DockHost publishes
 * its api on ready so palette commands and menu items can trigger panel opens without prop drilling.
 * If the api isn't registered yet (pre-mount), the command is a no-op.
 */
let registeredApi: DockviewApi | null = null;
let selectionUnsubscribe: (() => void) | null = null;

export function registerDockApi(api: DockviewApi | null): void {
  registeredApi = api;

  // #302: when the source-preview panel is already open, follow the profiler selection — re-render
  // the panel with the new file:line on each span click. Deliberately scoped to "panel already open":
  // we never spawn the panel from a selection, so the user retains control over whether they want
  // the source-preview real estate. Spans without source attribution preserve the previous content
  // (last-useful-state wins) instead of clearing to a "no source" placeholder.
  if (selectionUnsubscribe) {
    selectionUnsubscribe();
    selectionUnsubscribe = null;
  }
  if (api) {
    selectionUnsubscribe = useProfilerSelectionStore.subscribe((state, prev) => {
      if (state.selected === prev.selected) return;
      const sel = state.selected;
      if (!sel) return;
      const panel = api.getPanel('source-preview');
      if (!panel) return;
      let loc = null;
      if (sel.kind === 'span') {
        loc = useSourceLocationStore.getState().resolve(sel.span.rawEvent?.sourceLocationId);
      } else if (sel.kind === 'chunk') {
        loc = useSourceLocationStore.getState().resolveSystem(sel.chunk.systemIndex);
      }
      if (!loc) return;
      panel.api.updateParameters({ path: loc.file, line: loc.line });
    });
  }
}

// --- Edge-group (structural) view toggles ---

/** Toggle the left edge group (Resource Tree). No-op in trace/attach sessions (no left edge group). */
export function toggleViewResourceTree(): void {
  const api = registeredApi;
  if (!api) return;
  const eg = api.getEdgeGroup('left');
  if (!eg) return;
  if (eg.isCollapsed()) {
    eg.expand();
    api.getPanel('resource-tree')?.focus();
  } else {
    eg.collapse();
  }
}

/** Toggle the right edge group (Detail panel). */
export function toggleViewDetail(): void {
  const api = registeredApi;
  if (!api) return;
  const eg = api.getEdgeGroup('right');
  if (!eg) return;
  if (eg.isCollapsed()) {
    eg.expand();
    api.getPanel('detail')?.focus();
  } else {
    eg.collapse();
  }
}

/**
 * Toggle / surface the Logs panel. Logs normally lives in the bottom edge group: when it's the
 * visible tab there, a repeat call collapses the group; otherwise the group is expanded (if needed)
 * and Logs is focused. A stale saved layout can restore Logs into a different group (or with no
 * bottom edge group at all) — in that case we skip the collapse/expand dance and just focus the
 * panel wherever it lives, so View → Logs is never a silent no-op. (DockHost's onReady safety net
 * guarantees the panel exists post-restore.)
 */
export function toggleViewLogs(): void {
  const api = registeredApi;
  if (!api) return;
  const panel = api.getPanel('logs');
  if (!panel) return;
  const eg = api.getEdgeGroup('bottom');
  // Logs sits in the bottom edge group and is already the visible tab — second call hides it.
  if (eg && !eg.isCollapsed() && panel.api.isActive) {
    eg.collapse();
    return;
  }
  if (eg?.isCollapsed()) {
    eg.expand();
  }
  panel.focus();
}

// --- Dynamic view toggles (close if open, open if closed) ---

export function toggleViewComponentBrowser(): void {
  toggleDockPanel('schema-browser', 'SchemaBrowser', 'Component Browser');
}

export function toggleViewArchetypeBrowser(): void {
  toggleDockPanel('archetype-browser', 'ArchetypeBrowser', 'Archetype Browser');
}

export function toggleViewSchemaLayout(): void {
  toggleDockPanel('schema-layout', 'SchemaLayout', 'Component Layout');
}

export function toggleViewSchemaArchetypes(): void {
  toggleDockPanel('schema-archetypes', 'SchemaArchetypes', 'Component Archetypes');
}

export function toggleViewSchemaIndexes(): void {
  toggleDockPanel('schema-indexes', 'SchemaIndexes', 'Component Indexes');
}

export function toggleViewSchemaRelationships(): void {
  toggleDockPanel('schema-relationships', 'SchemaRelationships', 'Component Relationships');
}

export function toggleViewSystemDag(): void {
  toggleDockPanel('system-dag', 'SystemDag', 'System DAG');
}

export function toggleViewDataFlow(): void {
  toggleDockPanel('data-flow', 'DataFlow', 'Data Flow');
}

export function toggleViewAccessMatrix(): void {
  toggleDockPanel('access-matrix', 'AccessMatrix', 'Access Matrix');
}

export function toggleViewOptions(): void {
  toggleDockPanel('options', 'Options', 'Options');
}

/** Module 15: open / close the Database File Map panel. */
export function toggleViewDbMap(): void {
  toggleDockPanel('dbmap', 'DbMap', 'Database File Map');
}

/**
 * #302: open / close the inline Source Preview panel. Opens empty — the {@link registerDockApi}
 * selection subscription feeds it the resolved `file:line` on the next span / chunk click; until
 * then the panel shows its "No source location selected" placeholder.
 */
export function toggleViewSourcePreview(): void {
  toggleDockPanel('source-preview', 'SourcePreview', 'Source Preview');
}

/** Debug-only: the colour-palette reference panel. Reachable from the command palette alone — no View-menu entry. */
export function toggleViewPaletteDebug(): void {
  toggleDockPanel('palette-debug', 'PaletteDebug', 'Color Palettes');
}

// --- Source preview (action command, not a view toggle) ---

/**
 * #302 Phase 7: open the inline source-preview panel for a given file:line. Each invocation reuses
 * one panel id so opening a second source from the Source row replaces the contents instead of
 * stacking panels. Always surfaces the panel — see {@link surfacePanel}.
 */
export function openSourcePreview(path: string, line: number): void {
  const api = registeredApi;
  if (!api) return;
  let panel = api.getPanel('source-preview');
  if (panel) {
    panel.api.updateParameters({ path, line });
  } else {
    api.addPanel({
      id: 'source-preview',
      component: 'SourcePreview',
      title: 'Source Preview',
      params: { path, line },
    });
    panel = api.getPanel('source-preview');
  }
  surfacePanel(panel);
}

/**
 * Make a panel actually visible. `panel.focus()` activates the panel's tab within its group, but a
 * panel docked in a *collapsed edge group* would stay hidden — `focus()` never expands one. So
 * expand the panel's group first (a no-op for a regular, non-edge group), then focus.
 */
function surfacePanel(panel: ReturnType<DockviewApi['getPanel']>): void {
  if (!panel) return;
  const group = panel.api.group;
  if (group.api.isCollapsed()) {
    group.api.expand();
  }
  panel.focus();
}

/**
 * #351: update the Source Preview panel's content **only when it is already open** — the select-a-row
 * counterpart to {@link openSourcePreview}. A plain selection must never spawn the panel (the user
 * owns that real estate); it just keeps an already-open panel in sync. No-op when the panel is closed.
 */
export function updateSourcePreviewIfOpen(path: string, line: number): void {
  const api = registeredApi;
  if (!api) return;
  const panel = api.getPanel('source-preview');
  if (!panel) return;
  panel.api.updateParameters({ path, line });
}

/**
 * #302 Phase 7: palette command — open the source-preview for the currently selected span.
 * No-op when there is no span selected, the selected span carries no source-location id, or the
 * id can't be resolved against the manifest. The buttons in the Detail panel's Source row remain
 * the primary entry point; this is for keyboard-driven users.
 */
export function openSourcePreviewForCurrentSpan(): void {
  const selection = useProfilerSelectionStore.getState().selected;
  if (!selection || selection.kind !== 'span') return;
  const siteId = selection.span.rawEvent?.sourceLocationId;
  const loc = useSourceLocationStore.getState().resolve(siteId);
  if (!loc) return;
  openSourcePreview(loc.file, loc.line);
}

export function saveLayoutAsDefault(): void {
  const api = registeredApi;
  if (!api) return;
  const kind = useSessionStore.getState().kind;
  if (kind === 'none') return;
  useDockLayoutStore.getState().saveTemplate(kind, api.toJSON());
}

/**
 * Module-level reset-layout hook. DockHost owns `buildDefaultLayout`, so it publishes a reset
 * closure here (mirroring {@link registerDockApi}); the View menu item and palette command invoke
 * it without reaching into DockHost. No-op until DockHost has mounted.
 */
let registeredResetLayout: (() => void) | null = null;

export function registerResetLayout(fn: (() => void) | null): void {
  registeredResetLayout = fn;
}

/**
 * Discard the current dock arrangement and rebuild this session kind's built-in default layout —
 * the recovery path when a panel has been dragged somewhere it can no longer be reached.
 */
export function resetLayout(): void {
  registeredResetLayout?.();
}

/**
 * Cross-link "ensure visible" semantics — opens a dock panel if absent, focuses it if already open, never
 * closes it. The counterpart to {@link toggleDockPanel} for reveal actions, which must always surface a panel.
 */
export function ensureDockPanel(id: string, componentKey: string, title: string): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel(id);
  if (existing) {
    existing.focus();
    return;
  }
  const anchor = api.getPanel('profiler') ?? api.getPanel('start-here');
  if (anchor) {
    api.addPanel({ id, component: componentKey, title, position: { referencePanel: anchor.id } });
  } else {
    api.addPanel({ id, component: componentKey, title });
  }
}

/** Expands the left edge group and focuses the Resource Tree — the "reveal in tree" surfacing step. No-op when absent. */
export function ensureResourceTreeVisible(): void {
  const api = registeredApi;
  if (!api) return;
  const eg = api.getEdgeGroup('left');
  if (eg?.isCollapsed()) {
    eg.expand();
  }
  api.getPanel('resource-tree')?.focus();
}

function toggleDockPanel(id: string, componentKey: string, title: string): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel(id);
  if (existing) {
    api.removePanel(existing);
    return;
  }
  // Without a position, dockview drops the new panel into whichever group was last active — which after a
  // trace-mode auto-build is one of the narrow right-edge groups (Detail / Components / Archetypes), not the
  // wide center group with Profiler. Prefer planting these toggles next to the Profiler/Start-Here so the
  // panel mounts at usable width. Falls back to `addPanel` with no position when neither anchor exists.
  const anchor = api.getPanel('profiler') ?? api.getPanel('start-here');
  if (anchor) {
    api.addPanel({ id, component: componentKey, title, position: { referencePanel: anchor.id } });
  } else {
    api.addPanel({ id, component: componentKey, title });
  }
}
