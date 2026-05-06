import type { TimeRange } from '@/libs/profiler/model/uiTypes';
import { useProfilerSelectionStore } from './useProfilerSelectionStore';
import { useProfilerViewStore } from './useProfilerViewStore';
import { useSchemaInspectorStore } from './useSchemaInspectorStore';
import { useSelectedResourceStore } from './useSelectedResourceStore';
import { useSelectionStore, type TimeSelection } from './useSelectionStore';

// TODO(#309 follow-up): the resource and (eventually) entity slots only mirror legacy → unified.
// The reverse (unified → legacy) needs the resource-graph cache to resolve a `resourceId` into a
// rich `SelectedResource` payload. Until that exists, URL deep-links like `?resource=...` populate
// `useSelectionStore.resource` but do not light up the ResourceTreePanel highlight on cold load.

/**
 * Bidirectional bridges that keep the cross-cutting projections of the legacy per-panel stores
 * synchronised with {@link useSelectionStore}. Per `claude/design/workbench/10-internal-data-api.md §9.2`,
 * Phase C of the migration: old stores become write-throughs to the new store, deletion deferred.
 *
 * Each bridge is a paired `subscribe` — A→B and B→A — guarded against feedback loops by a single
 * boolean flag per pair. Idempotent at the value level (no-op if equal), so the guard is belt-and-
 * suspenders. Volatile slots (`focusTick`, `worker`) and panel-internal sub-selections (span/chunk/
 * marker payloads, schema field, resource raw payload) stay in their respective per-panel stores.
 */

function rangeEqual(a: TimeRange, b: TimeSelection | null): boolean {
  if (!b) return false;
  return a.startUs === b.start && a.endUs === b.end;
}

function bridgeProfilerViewRangeAndSelectionTime(): () => void {
  let suppress = false;

  const offProfiler = useProfilerViewStore.subscribe((s, prev) => {
    if (suppress) return;
    if (s.viewRange === prev.viewRange) return;
    const r = s.viewRange;
    const current = useSelectionStore.getState().time;
    // `{0, 0}` is a profiler-internal "no preference" sentinel — set on every metadata arrival in
    // trace mode (ProfilerPanel.tsx). It is NOT a cross-panel "clear time" signal. Two cases:
    //   - cross-panel time is null → leave both alone.
    //   - cross-panel time is set (URL deep-link, DAG view, etc.) → reassert it back into
    //     viewRange so the metadata-arrival reset doesn't blow away a deep-linked range.
    if (r.startUs === 0 && r.endUs === 0) {
      if (current === null) return;
      suppress = true;
      try {
        useProfilerViewStore.getState().setViewRange({ startUs: current.start, endUs: current.end });
      } finally {
        suppress = false;
      }
      return;
    }
    if (rangeEqual(r, current)) return;
    suppress = true;
    try {
      useSelectionStore.getState().setTime({ start: r.startUs, end: r.endUs });
    } finally {
      suppress = false;
    }
  });

  const offSelection = useSelectionStore.subscribe((s, prev) => {
    if (suppress) return;
    if (s.time === prev.time) return;
    if (s.time === null) return; // Don't clobber profiler's viewRange on a cross-panel clear.
    if (rangeEqual(useProfilerViewStore.getState().viewRange, s.time)) return;
    suppress = true;
    try {
      useProfilerViewStore.getState().setViewRange({ startUs: s.time.start, endUs: s.time.end });
    } finally {
      suppress = false;
    }
  });

  return () => {
    offProfiler();
    offSelection();
  };
}

function bridgeProfilerSelectionAndFocusTick(): () => void {
  let suppress = false;

  const offProfiler = useProfilerSelectionStore.subscribe((s, prev) => {
    if (suppress) return;
    if (s.selected === prev.selected) return;
    const tick = extractTick(s.selected);
    suppress = true;
    try {
      useSelectionStore.getState().setFocusTick(tick);
    } finally {
      suppress = false;
    }
  });

  return offProfiler;
}

function extractTick(selected: ReturnType<typeof useProfilerSelectionStore.getState>['selected']): number | null {
  if (!selected) return null;
  switch (selected.kind) {
    case 'tick':
    case 'phase':
    case 'phase-marker':
      return selected.tickNumber;
    case 'span':
    case 'chunk':
    case 'marker':
      // Panel-internal selections; they don't move the cross-panel focus tick. (Spans/chunks are
      // sub-tick-scope; markers are not tick-aligned in the cross-panel sense.)
      return null;
  }
}

function bridgeSelectedResource(): () => void {
  let suppress = false;

  const offResource = useSelectedResourceStore.subscribe((s, prev) => {
    if (suppress) return;
    if (s.selected === prev.selected) return;
    const id = s.selected?.resourceId ?? null;
    suppress = true;
    try {
      useSelectionStore.getState().setResource(id);
    } finally {
      suppress = false;
    }
  });

  return offResource;
}

function bridgeSelectedComponent(): () => void {
  let suppress = false;

  const offSchema = useSchemaInspectorStore.subscribe((s, prev) => {
    if (suppress) return;
    if (s.selectedComponentType === prev.selectedComponentType) return;
    suppress = true;
    try {
      useSelectionStore.getState().setComponent(s.selectedComponentType);
    } finally {
      suppress = false;
    }
  });

  const offSelection = useSelectionStore.subscribe((s, prev) => {
    if (suppress) return;
    if (s.component === prev.component) return;
    if (useSchemaInspectorStore.getState().selectedComponentType === s.component) return;
    suppress = true;
    try {
      useSchemaInspectorStore.getState().selectComponent(s.component);
    } finally {
      suppress = false;
    }
  });

  return () => {
    offSchema();
    offSelection();
  };
}

/**
 * Installs every per-panel ↔ unified bridge. Returns a single unsubscribe handle that tears them
 * all down. Call once at app boot — calling twice doubles the subscriptions and produces redundant
 * (but idempotent) writes.
 */
export function installSelectionBridges(): () => void {
  const offs = [
    bridgeProfilerViewRangeAndSelectionTime(),
    bridgeProfilerSelectionAndFocusTick(),
    bridgeSelectedResource(),
    bridgeSelectedComponent(),
  ];
  return () => {
    for (const off of offs) off();
  };
}
