import type { DockviewApi } from 'dockview-react';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
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
  if (endUs > startUs) useProfilerViewStore.getState().setViewRange({ startUs, endUs });
}

function panViewport(directionMultiplier: number): void {
  const { viewRange, setViewRange } = useProfilerViewStore.getState();
  const range = viewRange.endUs - viewRange.startUs;
  if (range <= 0) return;
  const delta = range * 0.25 * directionMultiplier;
  setViewRange({ startUs: viewRange.startUs + delta, endUs: viewRange.endUs + delta });
}

/**
 * Viewport-animation bridge — TimeArea registers its local `animateToRange` on mount so other
 * modules (nav-history restore, etc.) can ask the profiler to tween the viewport to a target
 * range with the same 800 ms ease-out curve used for double-click zoom. When TimeArea isn't
 * mounted (profiler panel closed, still loading), `animateViewportToRange` falls back to
 * `setViewRange` — no animation, but navigation still works.
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
  else useProfilerViewStore.getState().setViewRange(target);
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
    { id: 'toggle-view-profiler',     label: 'Toggle View Profiler',  keywords: 'profiler open show',               action: toggleViewProfiler },
    { id: 'toggle-view-top-spans',   label: 'Toggle View Top Spans', keywords: 'profiler top spans table slow expensive sortable', action: toggleViewTopSpans },
    { id: 'profiler-save-replay',    label: 'Save Session as .typhon-replay…', keywords: 'save replay export attach session', action: openSaveReplayDialog },
    { id: 'profiler-toggle-gauges',  label: 'Toggle Gauge Region',   keywords: 'gauges canvas profiler g',         action: () => useProfilerViewStore.getState().toggleGaugeRegion() },
    { id: 'profiler-toggle-legends', label: 'Toggle Legends',        keywords: 'legends labels profiler l',        action: () => useProfilerViewStore.getState().toggleLegends() },
    { id: 'profiler-toggle-systems', label: 'Toggle Per-System Lanes', keywords: 'systems lanes profiler',         action: () => useProfilerViewStore.getState().togglePerSystemLanes() },
    { id: 'profiler-zoom-full',      label: 'Zoom to Full Trace',    keywords: 'zoom full profiler reset home',    action: zoomToFullTrace },
    { id: 'profiler-pan-left',       label: 'Pan Left',              keywords: 'pan left profiler',                action: () => panViewport(-1) },
    { id: 'profiler-pan-right',      label: 'Pan Right',             keywords: 'pan right profiler',               action: () => panViewport(+1) },
  ];
}
