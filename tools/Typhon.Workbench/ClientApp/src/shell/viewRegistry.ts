import type { SessionKind } from '@/stores/useSessionStore';

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
  // #386 Phase 1: Query Console — author + run + browse against a live `.typhon`. Open-session only; the
  // panel itself renders a disabled state in trace/attach so we don't need a separate gate here.
  QueryConsole: true,
  // Observe (P-C) — live engine surface.
  // Stage 4 Phase 1 (#377, GAP-21/22): the Engine Live Health view — consolidated live signals (gauges, anomaly
  // log, reconnect banner) + the freeze + Capture & Analyse glue. P1 ships the shell + connection-state header
  // + Disconnect; gauges (P2), anomalies (P3), Capture & Analyse + reconnect banner (P4) follow.
  EngineLiveHealth: true,
  // Sample database — the sample-DB creation surface (presets + Advanced form + editable destination folder).
  // Shipped in Release (#433); the panel probes `/api/fixtures/capability` and renders a "not available" cold
  // state only if the probe fails, so the activation flag stays unconditional here.
  DevFixture: true,
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

// ── Session-kind scope (IA §5.1) ──────────────────────────────────────────────────────────────────────────────
//
// A view can run only in the session kind whose data it needs. The View menu and the command palette BOTH derive
// visibility from this one map so they can never drift: a view that can't run in the current session is *absent*
// from both (IA §7 principle 4 — no broken affordances), not greyed/dead. Independent of `isViewActive` (the
// feature flag): a view shows only when it is BOTH active AND in-scope for the session — see `isViewVisible`.
//
// `open` = a loaded `.typhon` file; `profiler` = a trace/attach session; `any` = session-independent. The
// shell-structural navigators (SchemaExplorer / SystemsQueriesNav / ResourceTree) are listed here even though they
// are not in ZONE_D_VIEW_ACTIVE — they are still session-scoped. Unlisted ids default to `any` (Detail/Logs/Options
// and every non-view command).
export type ViewSessionScope = 'open' | 'profiler' | 'any';

const VIEW_SESSION_SCOPE: Readonly<Record<string, ViewSessionScope>> = {
  // Open (.typhon) views
  SchemaExplorer: 'open',
  DataBrowserEntities: 'open',
  DbMap: 'open',
  StorageHealth: 'open',
  QueryConsole: 'open',
  ResourceTree: 'open',
  // Profiler (trace/attach) views
  Profiler: 'profiler',
  TopSpans: 'profiler',
  CallTree: 'profiler',
  SourcePreview: 'profiler',
  SystemDag: 'profiler',
  CriticalPath: 'profiler',
  DataFlow: 'profiler',
  QueryAnalyzer: 'profiler',
  EngineLiveHealth: 'profiler',
  SystemsQueriesNav: 'profiler',
  // Session-independent (generates/opens regardless of the current session)
  DevFixture: 'any',
};

/** The session-kind scope of a view (or view-bound command). Unlisted / undefined ids are `any`. */
export function viewSessionScope(viewId: string | undefined): ViewSessionScope {
  if (viewId == null) {
    return 'any';
  }
  return VIEW_SESSION_SCOPE[viewId] ?? 'any';
}

/** Whether a view's panel can run in the given session kind (by its scope). `none` opens no session-scoped view. */
export function isViewAvailableInKind(viewId: string | undefined, kind: SessionKind): boolean {
  switch (viewSessionScope(viewId)) {
    case 'any':
      return true;
    case 'open':
      return kind === 'open';
    case 'profiler':
      return kind === 'attach' || kind === 'trace';
  }
}

/**
 * The single visibility predicate for a view (or view-bound command) in the current session — shared by the View
 * menu and the command palette so they stay in lockstep. A view shows iff it is BOTH feature-active
 * ({@link isViewActive}) AND in scope for the session ({@link isViewAvailableInKind}).
 */
export function isViewVisible(viewId: string | undefined, kind: SessionKind): boolean {
  return isViewActive(viewId) && isViewAvailableInKind(viewId, kind);
}
