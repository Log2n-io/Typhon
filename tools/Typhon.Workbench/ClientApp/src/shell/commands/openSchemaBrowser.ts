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
 * Toggle the Logs panel inside the bottom edge group.
 * If the group is collapsed, expand and focus Logs.
 * If expanded and Logs is already active, collapse the group.
 * If expanded and another tab is active, switch focus to Logs without collapsing.
 */
export function toggleViewLogs(): void {
  const api = registeredApi;
  if (!api) return;
  const eg = api.getEdgeGroup('bottom');
  if (!eg) return;
  if (eg.isCollapsed()) {
    eg.expand();
    api.getPanel('logs')?.focus();
    return;
  }
  const panel = api.getPanel('logs');
  if (panel?.api.isActive) eg.collapse();
  else panel?.focus();
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

export function toggleViewOptions(): void {
  toggleDockPanel('options', 'Options', 'Options');
}

// --- Source preview (action command, not a view toggle) ---

/**
 * #302 Phase 7: open the inline source-preview panel for a given file:line. Each invocation reuses
 * one panel id so opening a second source from the Source row replaces the contents instead of
 * stacking panels.
 */
export function openSourcePreview(path: string, line: number): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel('source-preview');
  if (existing) {
    existing.api.updateParameters({ path, line });
    existing.focus();
    return;
  }
  api.addPanel({
    id: 'source-preview',
    component: 'SourcePreview',
    title: 'Source Preview',
    params: { path, line },
  });
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

function toggleDockPanel(id: string, componentKey: string, title: string): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel(id);
  if (existing) {
    api.removePanel(existing);
    return;
  }
  api.addPanel({ id, component: componentKey, title });
}
