import type { DockviewApi } from 'dockview-react';
import { useProfilerSelectionStore } from '@/stores/useProfilerSelectionStore';
import { useSourceLocationStore } from '@/stores/useSourceLocationStore';

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

export function openSchemaBrowser(): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel('schema-browser');
  if (existing) {
    existing.focus();
    return;
  }
  api.addPanel({
    id: 'schema-browser',
    component: 'SchemaBrowser',
    title: 'Component Browser',
  });
}

export function openArchetypeBrowser(): void {
  openDockPanel('archetype-browser', 'ArchetypeBrowser', 'Archetype Browser');
}

export function openSchemaLayout(): void {
  openDockPanel('schema-layout', 'SchemaLayout', 'Component Layout');
}

export function openSchemaArchetypes(): void {
  openDockPanel('schema-archetypes', 'SchemaArchetypes', 'Component Archetypes');
}

export function openSchemaIndexes(): void {
  openDockPanel('schema-indexes', 'SchemaIndexes', 'Component Indexes');
}

export function openSchemaRelationships(): void {
  openDockPanel('schema-relationships', 'SchemaRelationships', 'Component Relationships');
}

/**
 * Open (or focus) the Detail panel — useful when the user closed it and wants it back. In
 * trace/attach sessions the default layout dock this to the right of the Profiler; in open
 * sessions it's already in the default layout, so this is mostly for "I closed it, bring it back".
 */
export function openDetailPanel(): void {
  openDockPanel('detail', 'Detail', 'Detail');
}

/** #302 Phase 5: open (or focus) the Options panel — editor preference + workspace root. */
export function openOptions(): void {
  openDockPanel('options', 'Options', 'Options');
}

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

function openDockPanel(id: string, componentKey: string, title: string): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel(id);
  if (existing) {
    existing.focus();
    return;
  }
  api.addPanel({ id, component: componentKey, title });
}
