// Stage 0 view-enablement gate.
//
// The Workbench product re-architecture migrates shell-first: Stage 0 reduces the app to its shell frame by
// deactivating every deep/workspace (zone-D) view, while keeping the view sources compilable (gated, not
// deleted). This registry is the single switch — each entry below maps a dockview component id to its
// active state. Reversible per view: Stages 2-4 flip an entry to `true` as the redesigned view returns.
//
// Shell-structural surfaces (ResourceTree navigator, Detail/Inspector, Logs drawer, Options/Settings,
// PaletteDebug) are intentionally NOT listed here — `isViewActive` treats any unlisted id as always-on.
//
// See claude/design/Apps/Workbench/stages/stage-0-deactivate.md and 02-information-architecture.md §9.4.
export const ZONE_D_VIEW_ACTIVE: Readonly<Record<string, boolean>> = {
  // Inspect (P-A) — schema/data/storage deep views
  SchemaBrowser: false,
  ArchetypeBrowser: false,
  SchemaLayout: false,
  SchemaArchetypes: false,
  SchemaIndexes: false,
  SchemaRelationships: false,
  DataBrowserEntities: false,
  DbMap: false,
  // Profile (P-B) — profiler deep views
  Profiler: false,
  TopSpans: false,
  CallTree: false,
  SourcePreview: false,
  SystemDag: false,
  CriticalPath: false,
  DataFlow: false,
  AccessMatrix: false,
  // Query (P-A/B) — query-analysis deep views
  QueryCatalog: false,
  QueryPlanTree: false,
  ExecutionInspector: false,
};

// Returns whether a view (or a view-bound command) is currently reachable. An undefined id means the caller
// is not a view-toggle (e.g. a shell command) and is always allowed; an unlisted id is a shell-structural
// surface and is likewise always-on. Only the zone-D ids above can be gated off.
export function isViewActive(viewId: string | undefined): boolean {
  if (viewId == null) {
    return true;
  }
  return ZONE_D_VIEW_ACTIVE[viewId] ?? true;
}

// True once at least one zone-D view is re-enabled (Stages 2-4). Lets the View menu show its deep-view
// section separator only when there is a deep-view section to separate. False in Stage 0.
export const ANY_ZONE_D_VIEW_ACTIVE: boolean = Object.values(ZONE_D_VIEW_ACTIVE).some((active) => active);
