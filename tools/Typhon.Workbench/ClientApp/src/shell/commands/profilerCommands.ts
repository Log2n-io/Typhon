import type { DockviewApi } from 'dockview-react';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useUiPrefsStore } from '@/stores/useUiPrefsStore';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';
import type { CommandItem } from './baseCommands';

/**
 * Module-level dockview api registration for profiler-module commands — same pattern as
 * openSchemaBrowser's registerDockApi. DockHost publishes its api on ready so palette commands
 * and menu items can trigger the Profiler panel without prop drilling.
 */
let registeredApi: DockviewApi | null = null;

export function registerProfilerDockApi(api: DockviewApi | null): void {
  registeredApi = api;
}

/** Focuses the Profiler panel. Structural in trace/attach sessions — always present in center. */
export function toggleViewProfiler(): void {
  const api = registeredApi;
  if (!api) return;
  api.getPanel('profiler')?.focus();
}

/**
 * Toggle the Critical-Path panel — a dynamic dock panel (closed by default, no edge-group home).
 * First call adds it to the center area; subsequent calls remove it. Same shape as
 * {@link toggleViewComponentBrowser} but without the schema-browser dependencies.
 */
export function toggleViewCriticalPath(): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel('critical-path');
  if (existing) {
    api.removePanel(existing);
    return;
  }
  api.addPanel({ id: 'critical-path', component: 'CriticalPath', title: 'Critical Path' });
}

/**
 * Toggle the CPU Call Tree panel (#351 Phase 4) — a dynamic dock panel (closed by default, no
 * edge-group home). First call adds it to the center area at full width; subsequent calls remove
 * it. Same shape as {@link toggleViewCriticalPath} — kept out of the default layout because the
 * folded tree needs real width the collapsed right edge group can't give it.
 */
export function toggleViewCallTree(): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel('call-tree');
  if (existing) {
    api.removePanel(existing);
    return;
  }
  api.addPanel({ id: 'call-tree', component: 'CallTree', title: 'Call Tree' });
}

/**
 * Open (or focus) the CPU Call Tree panel — focus-when-present variant of {@link toggleViewCallTree}. Used by the
 * Detail panel's "Scope Call Tree to this" cross-panel command (#351 Phase 5) so a click never flips the panel
 * closed when it is already open. Mirrors {@link openViewQueryCatalog}.
 */
export function openViewCallTree(): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel('call-tree');
  if (existing) {
    existing.focus();
    return;
  }
  api.addPanel({ id: 'call-tree', component: 'CallTree', title: 'Call Tree' });
}

/**
 * Toggle the Query Catalog panel — a dynamic dock panel (closed by default, no edge-group home).
 * First call adds it to the center area; subsequent calls remove it. Same shape as
 * {@link toggleViewCriticalPath}. Issue #338 (P5 of #342).
 */
export function toggleViewQueryCatalog(): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel('query-catalog');
  if (existing) {
    api.removePanel(existing);
    return;
  }
  api.addPanel({ id: 'query-catalog', component: 'QueryCatalog', title: 'Query Catalog' });
}

/**
 * Open the Query Catalog panel (focus-when-present variant of {@link toggleViewQueryCatalog}).
 * Used by cross-panel navigation (e.g., System DAG "Queries" badge in P8) so a click doesn't flip
 * the panel closed when it's already open. Issue #341 (P8 of #342).
 */
export function openViewQueryCatalog(): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel('query-catalog');
  if (existing) {
    existing.focus();
    return;
  }
  api.addPanel({ id: 'query-catalog', component: 'QueryCatalog', title: 'Query Catalog' });
}

/**
 * Open (or focus) the Query Plan Tree panel. The store's <c>focus</c> is set independently before
 * calling this; the panel reads it via {@link useQueryPlanStore}. Issue #339 (P6 of #342).
 */
export function openViewQueryPlanTree(): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel('query-plan-tree');
  if (existing) {
    existing.focus();
    return;
  }
  api.addPanel({ id: 'query-plan-tree', component: 'QueryPlanTree', title: 'Query Plan' });
}

/** Toggle (close-when-open) variant of {@link openViewQueryPlanTree} for the command palette. */
export function toggleViewQueryPlanTree(): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel('query-plan-tree');
  if (existing) {
    api.removePanel(existing);
    return;
  }
  api.addPanel({ id: 'query-plan-tree', component: 'QueryPlanTree', title: 'Query Plan' });
}

/**
 * Open (or focus) the Execution Inspector panel. The store's <c>focus</c> is set independently
 * before calling this; the panel reads it via {@link useExecutionInspectorStore}. Issue #340
 * (P7 of #342).
 */
export function openViewExecutionInspector(): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel('execution-inspector');
  if (existing) {
    existing.focus();
    return;
  }
  api.addPanel({ id: 'execution-inspector', component: 'ExecutionInspector', title: 'Execution Inspector' });
}

/** Toggle (close-when-open) variant of {@link openViewExecutionInspector} for the command palette. */
export function toggleViewExecutionInspector(): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel('execution-inspector');
  if (existing) {
    api.removePanel(existing);
    return;
  }
  api.addPanel({ id: 'execution-inspector', component: 'ExecutionInspector', title: 'Execution Inspector' });
}

/**
 * Toggle the Top Spans panel inside the bottom edge group.
 * If the group is collapsed, expand and focus Top Spans.
 * If expanded and Top Spans is already active, collapse the group.
 * If expanded and another tab is active, switch focus to Top Spans without collapsing.
 */
export function toggleViewTopSpans(): void {
  const api = registeredApi;
  if (!api) return;
  const eg = api.getEdgeGroup('bottom');
  if (!eg) return;
  if (eg.isCollapsed()) {
    eg.expand();
    api.getPanel('top-spans')?.focus();
    return;
  }
  const panel = api.getPanel('top-spans');
  if (panel?.api.isActive) eg.collapse();
  else panel?.focus();
}

function zoomToFullTrace(): void {
  const metadata = useProfilerSessionStore.getState().metadata;
  const gm = metadata?.globalMetrics;
  if (!gm) return;
  const startUs = Number(gm.globalStartUs ?? 0);
  const endUs = Number(gm.globalEndUs ?? 0);
  if (endUs > startUs) useProfilerViewStore.getState().commitViewRange({ startUs, endUs });
}

function panViewport(directionMultiplier: number): void {
  const { viewRange, commitViewRange } = useProfilerViewStore.getState();
  const range = viewRange.endUs - viewRange.startUs;
  if (range <= 0) return;
  const delta = range * 0.25 * directionMultiplier;
  commitViewRange({ startUs: viewRange.startUs + delta, endUs: viewRange.endUs + delta });
}

/**
 * Viewport-animation bridge — TimeArea registers its local `animateToRange` on mount so other
 * modules (nav-history restore, etc.) can ask the profiler to tween the viewport to a target
 * range with the same 800 ms ease-out curve used for double-click zoom. When TimeArea isn't
 * mounted (profiler panel closed, still loading), `animateViewportToRange` falls back to
 * `commitViewRange` — no animation, but navigation still works.
 *
 * Registration pattern mirrors {@link registerProfilerDockApi}: a single module-level slot. The
 * TimeArea component calls `registerAnimateViewport(fn)` on mount and `registerAnimateViewport(null)`
 * on unmount.
 */
let registeredAnimate: ((target: TimeRange) => void) | null = null;

export function registerAnimateViewport(fn: ((target: TimeRange) => void) | null): void {
  registeredAnimate = fn;
}

export function animateViewportToRange(target: TimeRange): void {
  if (registeredAnimate) registeredAnimate(target);
  else useProfilerViewStore.getState().commitViewRange(target);
}

/**
 * Save-replay dialog opener. MenuBar mounts the dialog and registers its setOpen callback here so palette commands and
 * the View menu can both trigger it without prop-drilling through the dock layer. Same pattern as
 * {@link registerProfilerDockApi}.
 */
let registeredOpenSaveReplay: (() => void) | null = null;

export function registerOpenSaveReplay(fn: (() => void) | null): void {
  registeredOpenSaveReplay = fn;
}

export function openSaveReplayDialog(): void {
  registeredOpenSaveReplay?.();
}

/**
 * Profiler-module palette entries. Spread into `buildBaseCommands()` so they land alongside the
 * shell-level commands in the `Ctrl+K` palette.
 */
export function buildProfilerPaletteCommands(): CommandItem[] {
  return [
    { id: 'toggle-view-profiler',     label: 'Toggle View Profiler',  keywords: 'profiler open show',               action: toggleViewProfiler, viewId: 'Profiler' },
    { id: 'toggle-view-critical-path', label: 'Toggle View Critical Path', keywords: 'critical path tape timeline cp wall-clock tick', action: toggleViewCriticalPath, viewId: 'CriticalPath' },
    { id: 'toggle-view-query-catalog', label: 'Toggle View Query Catalog', keywords: 'query catalog definitions filters profiler', action: toggleViewQueryCatalog, viewId: 'QueryCatalog' },
    { id: 'toggle-view-query-plan-tree', label: 'Toggle View Query Plan Tree', keywords: 'query plan tree graph dagre xyflow profiler', action: toggleViewQueryPlanTree, viewId: 'QueryPlanTree' },
    { id: 'toggle-view-execution-inspector', label: 'Toggle View Execution Inspector', keywords: 'execution inspector phases drill profiler query', action: toggleViewExecutionInspector, viewId: 'ExecutionInspector' },
    { id: 'toggle-view-top-spans',   label: 'Toggle View Top Spans', keywords: 'profiler top spans table slow expensive sortable', action: toggleViewTopSpans, viewId: 'TopSpans' },
    { id: 'profiler-save-replay',    label: 'Save Session as .typhon-replay…', keywords: 'save replay export attach session', action: openSaveReplayDialog },
    // Profiler-view interaction commands — only meaningful with the Profiler view mounted, so gated with it.
    { id: 'profiler-toggle-gauges',  label: 'Toggle Gauge Region',   keywords: 'gauges canvas profiler g',         action: () => useProfilerViewStore.getState().toggleGaugeRegion(), viewId: 'Profiler' },
    { id: 'toggle-legends',          label: 'Toggle Legends',        keywords: 'legends labels help legend l app-wide',        action: () => useUiPrefsStore.getState().toggleLegends() },
    { id: 'profiler-toggle-systems', label: 'Toggle Per-System Lanes', keywords: 'systems lanes profiler',         action: () => useProfilerViewStore.getState().togglePerSystemLanes(), viewId: 'Profiler' },
    { id: 'profiler-zoom-full',      label: 'Zoom to Full Trace',    keywords: 'zoom full profiler reset home',    action: zoomToFullTrace, viewId: 'Profiler' },
    { id: 'profiler-pan-left',       label: 'Pan Left',              keywords: 'pan left profiler',                action: () => panViewport(-1), viewId: 'Profiler' },
    { id: 'profiler-pan-right',      label: 'Pan Right',             keywords: 'pan right profiler',               action: () => panViewport(+1), viewId: 'Profiler' },
  ];
}
