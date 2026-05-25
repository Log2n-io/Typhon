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
  // Inspect (P-A) — schema/data/storage deep views.
  // (Stage 2, GAP-02: the SchemaBrowser/ArchetypeBrowser navigators AND the four Schema* deep panels
  // — Layout/Archetypes/Indexes/Relationships — were *removed*, consolidated into the Schema Explorer +
  // the Archetype/Component Inspectors. They are no longer gated entries because they no longer exist.)
  // Stage 2 Phase 2: the Data Browser is reintroduced onto the unified bus (GAP-03/05). Flipping this on
  // mounts the Entity List panel and lights up its View-menu + palette + "Open in → Data Browser" handoffs.
  DataBrowserEntities: true,
  // Stage 2 Phase 3: the File Map is reintroduced (the storage drill) — selection already mirrors to the bus
  // (Stage 1), and the reverse-reveal (GAP-04) handoffs + DS-2/3 color land with it.
  DbMap: true,
  // Stage 2 Phase 3: Storage Health — the aggregate dashboard complement to the File Map (GAP-16).
  StorageHealth: true,
  // Profile (P-B) — profiler deep views.
  // Stage 3 Phase 1: the Profiler timeline (the global time-scope owner) + Top Spans are reintroduced onto the
  // finished shell. Selection already mirrors to the unified bus (Stage 1); flipping these on mounts them into
  // the trace/attach default layout and lights up their View-menu + palette entries. The remaining profiler/query
  // views stay gated until their Stage-3 phase: CallTree/SourcePreview = Phase 2; SystemDag/CriticalPath/DataFlow
  // (the scheduling cluster) = Phase 3; the Query views = Phase 4.
  Profiler: true,
  TopSpans: true,
  // Stage 3 Phase 2: the Call Tree (the span→cause drill — scope axes + off-CPU + sandwich, GAP-17) + Source
  // Preview (frame → file:line, degraded when attribution is absent) are reintroduced. Both are fully-built gated
  // views; flipping mounts them + lights their View-menu / palette / handoffs.
  CallTree: true,
  SourcePreview: true,
  // Stage 3 Phase 3 (3D): the rest of the scheduling cluster — System DAG + Critical Path — is reintroduced.
  // All three cluster panels already read/write the bus `System` (Stage 1), so activation alone delivers AC3.5
  // (one selection drives all); 3D also adds the focus cue + conformance enrolment + the "Reveal in System DAG"
  // handoff. Engine-track systems stay hidden by default (DagForm "show engine tracks").
  SystemDag: true,
  CriticalPath: true,
  // Stage 3 Phase 3 (3A): Data Flow is reintroduced, absorbing the former Access Matrix as its in-panel Matrix
  // mode (the 2→1 consolidation, GAP-19's sibling). AccessMatrix is therefore *removed* — it is no longer a
  // standalone view, so it has no entry here (its panel/command/menu were deleted, not gated).
  DataFlow: true,
  // Query (P-A/B) — query-analysis deep view.
  // Stage 3 Phase 4 (GAP-19): the Query Analyzer — one master/detail view that CONSOLIDATED the former
  // Query Catalog + Query Plan Tree + Execution Inspector (those 3 panels deleted in 4D-2; their reused
  // leaf modules — catalog filters/toolbar, plan graph, phase table, data hooks — relocated into
  // panels/QueryAnalyzer/).
  QueryAnalyzer: true,
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
