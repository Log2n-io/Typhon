import type { DockviewApi } from 'dockview-react';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useUiPrefsStore } from '@/stores/useUiPrefsStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useQueryAnalyzerStore } from '@/panels/QueryAnalyzer/useQueryAnalyzerStore';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';
import { isViewActive } from '../viewRegistry';
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
 * First call docks it in the **bottom strip alongside Logs / Top Spans** (its natural home — a wide-but-short
 * tape view); subsequent calls remove it. A no-position `addPanel` would join whatever group is active (the
 * recurring placement bug), so we anchor `within` the Logs (or Top Spans) group; falls back to no-position
 * when the bottom strip isn't present.
 */
export function toggleViewCriticalPath(): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel('critical-path');
  if (existing) {
    api.removePanel(existing);
    return;
  }
  const bottomAnchor = api.getPanel('logs') ?? api.getPanel('top-spans');
  api.addPanel(
    bottomAnchor
      ? { id: 'critical-path', component: 'CriticalPath', title: 'Critical Path', position: { referencePanel: bottomAnchor.id, direction: 'within' } }
      : { id: 'critical-path', component: 'CriticalPath', title: 'Critical Path' },
  );
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
  // Open in the center as a tab in the Profiler's group — a no-position addPanel joins whatever group is active
  // (e.g. the bottom Logs / Top-spans strip), and the folded tree needs the center's full width & height.
  api.addPanel(
    api.getPanel('profiler')
      ? { id: 'call-tree', component: 'CallTree', title: 'Call Tree', position: { referencePanel: 'profiler', direction: 'within' } }
      : { id: 'call-tree', component: 'CallTree', title: 'Call Tree' },
  );
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
  // Open in the center as a tab in the Profiler's group — a no-position addPanel joins whatever group is active
  // (e.g. the bottom Logs / Top-spans strip), and the folded tree needs the center's full width & height.
  api.addPanel(
    api.getPanel('profiler')
      ? { id: 'call-tree', component: 'CallTree', title: 'Call Tree', position: { referencePanel: 'profiler', direction: 'within' } }
      : { id: 'call-tree', component: 'CallTree', title: 'Call Tree' },
  );
}

/**
 * Add the Query Analyzer panel to the center area (a tab in the Profiler group) — the consolidated
 * master/detail catalog needs the center's full width, like the Call Tree. A no-position addPanel would
 * join whatever group is active (the recurring placement bug), so anchor `within` the Profiler group.
 */
function addQueryAnalyzerPanel(api: DockviewApi): void {
  api.addPanel(
    api.getPanel('profiler')
      ? { id: 'query-analyzer', component: 'QueryAnalyzer', title: 'Query Analyzer', position: { referencePanel: 'profiler', direction: 'within' } }
      : { id: 'query-analyzer', component: 'QueryAnalyzer', title: 'Query Analyzer' },
  );
}

/** The Query Analyzer can open only when its view is active AND we're in a profiler (trace/attach) session. */
function canOpenQueryAnalyzer(): boolean {
  const kind = useSessionStore.getState().kind;
  return isViewActive('QueryAnalyzer') && (kind === 'trace' || kind === 'attach');
}

/**
 * Open (or focus) the Query Analyzer — the consolidated master/detail query view (#376 Phase 4, GAP-19).
 * Focus-when-present so a reveal hand-off never flips it closed. No-ops outside a profiler session.
 */
export function openViewQueryAnalyzer(): void {
  const api = registeredApi;
  if (!api || !canOpenQueryAnalyzer()) return;
  const existing = api.getPanel('query-analyzer');
  if (existing) {
    existing.focus();
    return;
  }
  addQueryAnalyzerPanel(api);
}

/** Toggle (close-when-open) variant of {@link openViewQueryAnalyzer} for the palette / View menu. */
export function toggleViewQueryAnalyzer(): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel('query-analyzer');
  if (existing) {
    api.removePanel(existing);
    return;
  }
  if (!canOpenQueryAnalyzer()) return;
  addQueryAnalyzerPanel(api);
}

/**
 * Reveal a specific query in the analyzer: open/focus the panel, focus the query in the unified store,
 * and write the bus `query` leaf (so the Inspector + nav history follow). The cross-panel entry point —
 * used by the Systems & Queries navigator and the Inspector's query card.
 */
export function revealQueryInAnalyzer(kind: number, localId: number): void {
  openViewQueryAnalyzer();
  const sessionId = useSessionStore.getState().sessionId;
  if (sessionId) {
    useQueryAnalyzerStore.getState().setSelectedQuery(sessionId, { kind, localId });
  }
  useSelectionStore.getState().select('query', { kind, localId });
}

/**
 * Reveal a specific query EXECUTION in the analyzer — used by the Profiler timeline's "Inspect execution"
 * hand-off (a query span → its execution). Selects the query first (which resets the execution per the store
 * quirk), THEN pins the execution + reveals the Executions tab. Order matters: {@link revealQueryInAnalyzer}'s
 * `setSelectedQuery` clears `selectedExecution`, so the execution must be set after it.
 */
export function revealQueryExecutionInAnalyzer(kind: number, localId: number, tickIndex: number, systemId: number): void {
  revealQueryInAnalyzer(kind, localId);
  const store = useQueryAnalyzerStore.getState();
  store.setSelectedExecution({ tickIndex, systemId });
  store.setActiveTab('executions');
}

/**
 * Jump-to-time (GAP-20): set the global time window to `[startUs, endUs]` — the client-only hand-off from a
 * query execution to the timeline (the data is already present; no API). Uses {@link animateViewportToRange}
 * so the timeline tweens to the target when mounted, falling back to a synchronous commit otherwise.
 */
export function jumpToTimeRange(startUs: number, endUs: number): void {
  if (!(endUs > startUs)) return;
  animateViewportToRange({ startUs, endUs });
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
    { id: 'toggle-view-call-tree',    label: 'Toggle View Call Tree', keywords: 'call tree cpu samples folded stack callers callees sandwich bottom-up off-cpu profiler', action: toggleViewCallTree, viewId: 'CallTree' },
    { id: 'toggle-view-critical-path', label: 'Toggle View Critical Path', keywords: 'critical path tape timeline cp wall-clock tick', action: toggleViewCriticalPath, viewId: 'CriticalPath' },
    { id: 'toggle-view-query-analyzer', label: 'Toggle View Query Analyzer', keywords: 'query analyzer catalog plan executions profiler cost ranking selectivity workload', action: toggleViewQueryAnalyzer, viewId: 'QueryAnalyzer' },
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
