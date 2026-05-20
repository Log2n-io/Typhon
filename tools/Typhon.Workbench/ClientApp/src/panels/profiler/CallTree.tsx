import { useCallback, useEffect, useMemo, useState } from 'react';
import { Activity, ChevronDown, ChevronRight, Crosshair, FileCode, Loader2, Search, X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useSessionStore } from '@/stores/useSessionStore';
import { useOptionsStore } from '@/stores/useOptionsStore';
import { useCpuFrameStore } from '@/stores/useCpuFrameStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import {
  rangeScope,
  systemScope,
  phaseScope,
  useCallTreeScopeStore,
  WHOLE_SESSION_SCOPE,
  type CallTreeScope,
} from '@/stores/useCallTreeScopeStore';
import { matchMethodName } from '@/panels/profiler/methodNameMatch';
import { friendlyMethodName } from '@/panels/profiler/methodName';
import { useCpuFrameManifest } from '@/hooks/profiler/useCpuFrameManifest';
import {
  useCallTree,
  INVOLUNTARY_FRAME_LABELS,
  type CallTreeNode,
  type CallTreeRequest,
  type CallTreeViewMode,
  type CategorySlice,
} from '@/hooks/profiler/useCallTree';
import { useSampleDensity } from '@/hooks/profiler/useSampleDensity';
import { openSourcePreview, updateSourcePreviewIfOpen } from '@/shell/commands/openSchemaBrowser';
import { CallTreeContextMenu } from '@/panels/profiler/CallTreeContextMenu';

/**
 * One entry of the Call Tree's navigation stack — a complete view state: the time-window {@link CallTreeScope} plus an
 * optional frame-root. The breadcrumb renders the stack; clicking a crumb restores that entry's full state.
 */
interface NavEntry {
  scope: CallTreeScope;
  frameRoot: number | null;
}

/** The root crumb — whole session, no scope, no drill. Always `navStack[0]`; a stable module identity. */
const ROOT_ENTRY: NavEntry = { scope: WHOLE_SESSION_SCOPE, frameRoot: null };

/**
 * Call Tree panel (#351 Phase 4 + Phase 5) — a dotTrace-style, server-folded CPU-sample call tree. Phase 5 makes the
 * scope **commandable**: the toolbar's System/Phase selectors and the Detail panel's "Scope Call Tree to this" action
 * write {@link useCallTreeScopeStore}; the panel folds whatever scope is active and shows a non-stationarity sparkline.
 */
export default function CallTree() {
  const sessionId = useSessionStore((s) => s.sessionId);
  const kind = useSessionStore((s) => s.kind);

  const [viewMode, setViewMode] = useState<CallTreeViewMode>('wall-clock');
  const [groupByCategory, setGroupByCategory] = useState(false);
  // The breadcrumb is the panel-local navigation stack: each entry is a full view state (time-window scope +
  // frame-root). Every scope command and every Crosshair drill pushes one; clicking a crumb restores it. Entry 0
  // is always the whole-session root. Deliberately panel-local exploration, not the global nav history.
  const [navStack, setNavStack] = useState<NavEntry[]>([ROOT_ENTRY]);
  const [filterText, setFilterText] = useState('');
  const [appliedFilter, setAppliedFilter] = useState('');
  const byId = useCpuFrameStore((s) => s.byId);
  const openInEditor = useOptionsStore((s) => s.openInEditor);

  // Row selection — node index into the folded `nodes` array. Single-click selects; the selection
  // syncs an already-open Source Preview panel. Reset whenever the tree re-folds (scope / view-mode /
  // drill change ⇒ a new `data` identity ⇒ stale indices).
  const [selectedIndex, setSelectedIndex] = useState<number | null>(null);
  const [contextMenu, setContextMenu] = useState<{ x: number; y: number; nodeIndex: number } | null>(null);

  // Row expansion — the set of expanded node indices. Lifted out of per-row useState so the context
  // menu can expand / collapse whole subtrees. `key` pins it to the folded `data`; see the in-render
  // resync below.
  const [expansion, setExpansion] = useState<{ key: object | null; set: Set<number> }>({ key: null, set: new Set() });

  const active = navStack[navStack.length - 1];
  const scope = active.scope;
  const activeFrameRoot = active.frameRoot;

  // Every navigation clears the method filter — it was a find tool for the previous tree; a stale query against
  // the new one just confuses.
  //
  // Crosshair drill — push a crumb that keeps the current scope but re-roots the tree at this frame.
  const drillInto = useCallback((frameId: number) => {
    setNavStack((s) => [...s, { scope: s[s.length - 1].scope, frameRoot: frameId }]);
    setFilterText('');
    setAppliedFilter('');
  }, []);
  // Click crumb `index` — truncate the stack to it, restoring that crumb's full view state (scope + frame-root).
  // Index 0 is the root crumb (whole session). Clicking the current crumb is a no-op.
  const navigateBreadcrumb = useCallback((index: number) => {
    setNavStack((s) => (index >= 0 && index < s.length - 1 ? s.slice(0, index + 1) : s));
    setFilterText('');
    setAppliedFilter('');
  }, []);
  // Apply a time-window scope as a new crumb. `replaceIfSameKind` (range live-editing) overwrites the current
  // crumb instead of stacking one per keystroke, when that crumb is an undrilled scope of the same kind.
  const applyScope = useCallback((scoped: CallTreeScope, replaceIfSameKind = false) => {
    setNavStack((s) => {
      const top = s[s.length - 1];
      const entry: NavEntry = { scope: scoped, frameRoot: scoped.frameRoot };
      const replace = replaceIfSameKind && s.length > 1 && top.frameRoot == null && top.scope.kind === scoped.kind;
      return replace ? [...s.slice(0, -1), entry] : [...s, entry];
    });
    setFilterText('');
    setAppliedFilter('');
  }, []);

  // Debounce the filter — the input value updates immediately (responsive typing), but the tree walk + re-render only
  // runs ~180 ms after the last keystroke.
  useEffect(() => {
    const handle = window.setTimeout(() => setAppliedFilter(filterText), 180);
    return () => window.clearTimeout(handle);
  }, [filterText]);

  const isTrace = kind === 'trace';
  useCpuFrameManifest(isTrace ? sessionId : null);

  // The store is the cross-panel command inbox: TimeArea / Detail "View in Call Tree" write a scope here; this
  // panel observes it and pushes a breadcrumb crumb (with any frame-root it resolved — §8.2). Reconciled in-render
  // (the documented "adjust state on prop change" pattern) so the tree never flashes the un-scoped view first. A
  // scope owned by another session is stale.
  const storeScope = useCallTreeScopeStore((s) => s.scope);
  const ownerSessionId = useCallTreeScopeStore((s) => s.ownerSessionId);
  const incomingScope = ownerSessionId != null && ownerSessionId === sessionId ? storeScope : null;
  const [synced, setSynced] = useState<{ sessionId: string | null; scope: CallTreeScope | null }>({
    sessionId: null,
    scope: null,
  });
  if (synced.sessionId !== sessionId) {
    // Session changed — the whole nav history belonged to the old trace; drop it.
    setSynced({ sessionId, scope: incomingScope });
    setNavStack(
      incomingScope != null && incomingScope.kind !== 'session'
        ? [ROOT_ENTRY, { scope: incomingScope, frameRoot: incomingScope.frameRoot }]
        : [ROOT_ENTRY],
    );
    setFilterText('');
    setAppliedFilter('');
  } else if (incomingScope !== synced.scope) {
    // A new cross-panel scope command — push it as a crumb.
    setSynced({ sessionId, scope: incomingScope });
    if (incomingScope != null && incomingScope.kind !== 'session') {
      setNavStack((s) => [...s, { scope: incomingScope, frameRoot: incomingScope.frameRoot }]);
      setFilterText('');
      setAppliedFilter('');
    }
  }

  const request = useMemo<CallTreeRequest>(
    () => ({
      startUs: scope.startUs,
      endUs: scope.endUs,
      frameRoot: activeFrameRoot,
      viewMode,
      systemIndex: scope.systemIndex,
      phase: scope.phase,
      spanKind: scope.spanKind,
    }),
    [scope, activeFrameRoot, viewMode],
  );

  const query = useCallTree(isTrace ? sessionId : null, request);
  const data = query.data ?? null;

  // Re-fold ⇒ node indices are invalid ⇒ drop selection + any open context menu.
  useEffect(() => {
    setSelectedIndex(null);
    setContextMenu(null);
  }, [data]);

  // A re-fold resets expansion to the default hot-path. Done in-render (the documented "adjust state
  // on prop change" pattern) rather than in an effect so there is no all-collapsed flash on load.
  if (expansion.key !== data) {
    setExpansion({ key: data, set: defaultExpandedSet(data) });
  }
  const expandedSet = expansion.set;

  // Chevron toggle — flip one node's membership.
  const toggleExpand = useCallback((nodeIndex: number) => {
    setExpansion((prev) => {
      const set = new Set(prev.set);
      if (set.has(nodeIndex)) {
        set.delete(nodeIndex);
      } else {
        set.add(nodeIndex);
      }
      return { key: prev.key, set };
    });
  }, []);

  // Context-menu "Expand subtree" — open this frame and every descendant.
  const expandSubtree = useCallback(
    (nodeIndex: number) => {
      if (!data) {
        return;
      }
      const sub = collectSubtree(data.nodes, nodeIndex);
      setExpansion((prev) => ({ key: prev.key, set: new Set([...prev.set, ...sub]) }));
    },
    [data],
  );

  // Context-menu "Collapse subtree" — close this frame and drop every descendant from the set, so a
  // later re-expand starts from a clean (collapsed) subtree.
  const collapseSubtree = useCallback(
    (nodeIndex: number) => {
      if (!data) {
        return;
      }
      const sub = new Set(collectSubtree(data.nodes, nodeIndex));
      setExpansion((prev) => {
        const set = new Set<number>();
        for (const i of prev.set) {
          if (!sub.has(i)) {
            set.add(i);
          }
        }
        return { key: prev.key, set };
      });
    },
    [data],
  );

  // frameId → resolved symbol for a node index. `line === 0` ⇒ BCL / native frame with no source.
  const resolveSymbol = useCallback(
    (nodeIndex: number) => {
      const node = data?.nodes[nodeIndex];
      return node ? byId.get(node.frameId) ?? null : null;
    },
    [data, byId],
  );

  // Single-click — select the row and, if the Source Preview panel is open, sync it to this frame's
  // source. Deliberately does not spawn the panel: surfacing it is the context menu's "Show inline".
  const handleSelect = useCallback(
    (nodeIndex: number) => {
      setSelectedIndex(nodeIndex);
      const sym = resolveSymbol(nodeIndex);
      if (sym && sym.line > 0) {
        updateSourcePreviewIfOpen(sym.file, sym.line);
      }
    },
    [resolveSymbol],
  );

  // Double-click — open the frame's source in the external editor.
  const handleActivate = useCallback(
    (nodeIndex: number) => {
      const sym = resolveSymbol(nodeIndex);
      if (sym && sym.line > 0) {
        void openInEditor(sym.file, sym.line);
      }
    },
    [resolveSymbol, openInEditor],
  );

  // Right-click — select the row (so the menu acts on a visibly-highlighted frame) and open the menu.
  const handleContextMenu = useCallback(
    (e: React.MouseEvent, nodeIndex: number) => {
      e.preventDefault();
      handleSelect(nodeIndex);
      setContextMenu({ x: e.clientX, y: e.clientY, nodeIndex });
    },
    [handleSelect],
  );

  // Client-side method-name filter: a node is visible when it — or any descendant — matches the query, so the path to
  // every match stays on screen. Matching is per-word and CamelCase-hump aware (see methodNameMatch): "AUS" hump-matches
  // the word "AntUpdateSystem", with a case-insensitive substring fallback — never stitched across words.
  const filterQuery = appliedFilter.trim();
  const treeFilter = useMemo<TreeFilter | null>(() => {
    if (filterQuery === '' || !data) {
      return null;
    }
    const matched = new Set<number>();
    const visible = new Set<number>();
    const walk = (idx: number): boolean => {
      const node = data.nodes[idx];
      if (!node) {
        return false;
      }
      // The filter matches the friendly display name, so a match always has a visible highlight.
      const method = friendlyMethodName(byId.get(node.frameId)?.method ?? '');
      const selfMatch = matchMethodName(method, filterQuery) !== null;
      if (selfMatch) {
        matched.add(idx);
      }
      let childMatch = false;
      for (const child of node.children) {
        if (walk(child)) {
          childMatch = true;
        }
      }
      const keep = selfMatch || childMatch;
      if (keep) {
        visible.add(idx);
      }
      return keep;
    };
    walk(0);
    return { query: filterQuery, visible, matched };
  }, [filterQuery, data, byId]);

  if (!isTrace) {
    return (
      <EmptyState
        icon={<Activity className="mx-auto mb-2 h-6 w-6" aria-hidden="true" />}
        text="The CPU call tree is available for trace sessions."
      />
    );
  }

  if (!data && query.isError) {
    return <EmptyState text={`Failed to load the call tree: ${query.error?.message ?? 'unknown error'}`} />;
  }

  // Context-menu target — resolve the right-clicked node's symbol once for the menu render.
  const ctxNode = contextMenu && data ? data.nodes[contextMenu.nodeIndex] ?? null : null;
  const ctxSymbol = ctxNode ? byId.get(ctxNode.frameId) ?? null : null;

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background text-[11px]">
      <Toolbar
        sessionId={sessionId}
        viewMode={viewMode}
        onViewMode={setViewMode}
        groupByCategory={groupByCategory}
        onGroupByCategory={setGroupByCategory}
        scope={scope}
        onApplyScope={applyScope}
        onClearScope={() => navigateBreadcrumb(0)}
        onPopScope={() => navigateBreadcrumb(navStack.length - 2)}
        data={data}
      />

      {data && data.totalSamples > 0 && sessionId && (
        <DensitySparkline sessionId={sessionId} request={request} />
      )}

      <div className="relative flex-1 overflow-hidden">
        {!data ? (
          <div className="flex h-full w-full items-center justify-center text-muted-foreground">
            <Loader2 className="mr-2 h-4 w-4 animate-spin" aria-hidden="true" />
            Building call tree…
          </div>
        ) : data.totalSamples === 0 ? (
          <EmptyState
            text={
              scope.kind !== 'session' || activeFrameRoot != null
                ? 'No CPU samples in the selected scope.'
                : 'This trace carries no CPU samples. Re-run profiling with TYPHON__PROFILER__CPUSAMPLING__ENABLED=true.'
            }
          />
        ) : groupByCategory ? (
          <CategoryView breakdown={data.categoryBreakdown} total={data.totalSamples} />
        ) : (
          <div className="flex h-full w-full">
            <div className="flex min-w-0 flex-1 flex-col overflow-hidden">
              {navStack.length > 1 && (
                <NavBreadcrumb stack={navStack} onNavigate={navigateBreadcrumb} />
              )}
              <SearchBar
                value={filterText}
                onChange={setFilterText}
                matchCount={treeFilter ? treeFilter.matched.size : null}
              />
              <div className="min-w-0 flex-1 overflow-auto">
                <TreeBody
                  nodes={data.nodes}
                  rootTotal={data.totalSamples}
                  onDrill={drillInto}
                  filter={treeFilter}
                  selectedIndex={selectedIndex}
                  expandedSet={expandedSet}
                  onSelect={handleSelect}
                  onActivate={handleActivate}
                  onContextMenu={handleContextMenu}
                  onToggleExpand={toggleExpand}
                />
              </div>
            </div>
            <CategorySidebar breakdown={data.categoryBreakdown} total={data.totalSamples} />
          </div>
        )}
      </div>
      {contextMenu && ctxNode && (
        <CallTreeContextMenu
          x={contextMenu.x}
          y={contextMenu.y}
          methodName={friendlyMethodName(ctxSymbol?.method ?? `#${ctxNode.frameId}`)}
          fullSignature={ctxSymbol?.method ?? `#${ctxNode.frameId}`}
          sourceAvailable={ctxSymbol != null && ctxSymbol.line > 0}
          hasChildren={ctxNode.children.length > 0}
          onClose={() => setContextMenu(null)}
          onShowInline={() => {
            if (ctxSymbol && ctxSymbol.line > 0) {
              openSourcePreview(ctxSymbol.file, ctxSymbol.line);
            }
            setContextMenu(null);
          }}
          onOpenInEditor={() => {
            if (ctxSymbol && ctxSymbol.line > 0) {
              void openInEditor(ctxSymbol.file, ctxSymbol.line);
            }
            setContextMenu(null);
          }}
          onFocusTree={() => {
            drillInto(ctxNode.frameId);
            setContextMenu(null);
          }}
          onExpandSubtree={() => {
            expandSubtree(contextMenu.nodeIndex);
            setContextMenu(null);
          }}
          onCollapseSubtree={() => {
            collapseSubtree(contextMenu.nodeIndex);
            setContextMenu(null);
          }}
        />
      )}
    </div>
  );
}

function Toolbar(props: {
  sessionId: string | null;
  viewMode: CallTreeViewMode;
  onViewMode: (m: CallTreeViewMode) => void;
  groupByCategory: boolean;
  onGroupByCategory: (v: boolean) => void;
  scope: CallTreeScope;
  /** Push a time-window scope as a new breadcrumb crumb (`replaceIfSameKind` for live range editing). */
  onApplyScope: (scope: CallTreeScope, replaceIfSameKind?: boolean) => void;
  /** Jump to the root crumb — drops every scope and drill. Backs the active-scope chip's ×. */
  onClearScope: () => void;
  /** Pop the current crumb — backs clearing the manual range fields. */
  onPopScope: () => void;
  data: {
    totalSamples: number;
    managedSamples: number;
    externalSamples: number;
    classificationAvailable: boolean;
  } | null;
}) {
  const { sessionId, scope } = props;
  const metadata = useProfilerSessionStore((s) => s.metadata);

  const systems = metadata?.systems ?? [];
  const phases = metadata?.phases ?? [];

  // Manual range — local text state; the scope store is the source of truth, so the inputs clear when a non-range
  // scope (system / phase / span-kind, or a Detail-panel command) takes over.
  const [startMs, setStartMs] = useState('');
  const [endMs, setEndMs] = useState('');
  useEffect(() => {
    if (scope.kind !== 'range') {
      setStartMs('');
      setEndMs('');
    }
  }, [scope.kind]);

  const applyRange = (nextStartMs: string, nextEndMs: string) => {
    if (!sessionId) return;
    const toUs = (v: string): number | null => {
      const n = Number(v);
      return v.trim() === '' || Number.isNaN(n) ? null : n * 1000;
    };
    const s = toUs(nextStartMs);
    const e = toUs(nextEndMs);
    if (s == null && e == null) {
      // Both fields cleared — drop the range crumb if it is the active one.
      if (scope.kind === 'range') props.onPopScope();
    } else {
      // Live-typed range — replace the crumb rather than stack one per keystroke.
      props.onApplyScope(rangeScope(s, e), true);
    }
  };

  return (
    <div className="flex flex-shrink-0 flex-wrap items-center gap-2 border-b border-border bg-card px-2 py-1.5">
      {/* §8.7 — the on-CPU view is a true on-/off-core split only when the trace carried context-switch data; without
          it the same view is a SampleType proxy, so it honestly labels itself "Thread time" instead of "On-CPU". */}
      <div className="flex items-center overflow-hidden rounded border border-border">
        <ModeButton
          active={props.viewMode === 'on-cpu'}
          onClick={() => props.onViewMode('on-cpu')}
          title={
            props.data?.classificationAvailable
              ? 'Samples classified on-CPU — excludes GC pauses and voluntary waits'
              : 'No context-switch data in this trace — shows managed-leaf samples (thread time), not a true on-CPU split'
          }
        >
          {props.data?.classificationAvailable ? 'On-CPU' : 'Thread time'}
        </ModeButton>
        <ModeButton active={props.viewMode === 'wall-clock'} onClick={() => props.onViewMode('wall-clock')}>
          Wall-clock
        </ModeButton>
      </div>

      <Button
        variant={props.groupByCategory ? 'default' : 'outline'}
        size="sm"
        className="h-6 px-2 text-[11px]"
        onClick={() => props.onGroupByCategory(!props.groupByCategory)}
        title="Collapse the call tree to subsystem categories"
      >
        Group by category
      </Button>

      <span className="text-muted-foreground">·</span>

      {/* Scope catalog — pick a system or a phase to re-scope the folded tree. */}
      <select
        value={scope.kind === 'system' && scope.systemIndex != null ? String(scope.systemIndex) : ''}
        onChange={(e) => {
          if (!sessionId || e.target.value === '') return;
          const idx = Number(e.target.value);
          const sys = systems.find((s) => s.index === idx);
          props.onApplyScope(systemScope(idx, sys?.name ?? `#${idx}`));
        }}
        title="Scope the call tree to a system"
        className="h-6 rounded border border-border bg-background px-1 text-[11px] text-foreground"
      >
        <option value="">System ▾</option>
        {systems.map((s) => (
          <option key={s.index} value={s.index}>
            {s.name}
          </option>
        ))}
      </select>

      <select
        value={scope.kind === 'phase' && scope.phase != null ? scope.phase : ''}
        onChange={(e) => {
          if (!sessionId || e.target.value === '') return;
          props.onApplyScope(phaseScope(e.target.value));
        }}
        title="Scope the call tree to a phase"
        className="h-6 rounded border border-border bg-background px-1 text-[11px] text-foreground"
      >
        <option value="">Phase ▾</option>
        {phases.map((p) => (
          <option key={p} value={p}>
            {p}
          </option>
        ))}
      </select>

      <label className="flex items-center gap-1 text-muted-foreground">
        from
        <input
          value={startMs}
          onChange={(e) => {
            setStartMs(e.target.value);
            applyRange(e.target.value, endMs);
          }}
          placeholder="start"
          inputMode="numeric"
          className="h-6 w-14 rounded border border-border bg-background px-1 text-[11px] text-foreground"
        />
        ms
      </label>
      <label className="flex items-center gap-1 text-muted-foreground">
        to
        <input
          value={endMs}
          onChange={(e) => {
            setEndMs(e.target.value);
            applyRange(startMs, e.target.value);
          }}
          placeholder="end"
          inputMode="numeric"
          className="h-6 w-14 rounded border border-border bg-background px-1 text-[11px] text-foreground"
        />
        ms
      </label>

      {/* Active-scope chip — readout of the current crumb's time-window scope; its × jumps to the root crumb. */}
      {scope.kind !== 'session' && (
        <span className="flex items-center gap-1 rounded bg-accent/20 px-1.5 py-0.5 text-foreground">
          {scope.label}
          <button
            type="button"
            onClick={props.onClearScope}
            className="ml-0.5 text-muted-foreground hover:text-foreground"
            aria-label="Clear scope"
          >
            <X className="h-3 w-3" />
          </button>
        </span>
      )}

      {props.data && (
        <span className="ml-auto font-mono tabular-nums text-muted-foreground">
          {props.data.totalSamples.toLocaleString()} samples · {props.data.managedSamples.toLocaleString()} on-CPU ·{' '}
          {props.data.externalSamples.toLocaleString()} off-CPU
        </span>
      )}
    </div>
  );
}

/**
 * The §8.2 non-stationarity sparkline — in-scope sample density binned over time. A flat profile means the scope is
 * statistically stationary; spikes mean warm-up and steady-state behaviour are being averaged together.
 */
function DensitySparkline({ sessionId, request }: { sessionId: string; request: CallTreeRequest }) {
  const density = useSampleDensity(sessionId, request, 48);
  const bins = density.data?.bins ?? [];
  const max = bins.reduce((m, b) => Math.max(m, b.count), 0);
  const width = 168;
  const height = 24;
  const barWidth = bins.length > 0 ? width / bins.length : 0;
  const caveat =
    'Sample density over the current scope. A flat profile means the scope is stationary; spikes mean warm-up and ' +
    'steady-state are blended — consider a narrower scope before trusting the aggregate.';

  return (
    <div
      className="flex flex-shrink-0 items-center gap-2 border-b border-border bg-card px-2 py-1 text-[11px]"
      title={caveat}
    >
      <span className="text-muted-foreground">density</span>
      {bins.length === 0 || max === 0 ? (
        <span className="text-muted-foreground/60">{density.isError ? 'unavailable' : '—'}</span>
      ) : (
        <svg width={width} height={height} role="img" aria-label="Sample density sparkline">
          {bins.map((b, i) => {
            const barHeight = (b.count / max) * height;
            return (
              <rect
                key={i}
                x={i * barWidth}
                y={height - barHeight}
                width={Math.max(barWidth - 1, 0.5)}
                height={barHeight}
                className="fill-primary/70"
              />
            );
          })}
        </svg>
      )}
      <span className="text-muted-foreground/60">stationarity check</span>
    </div>
  );
}

function ModeButton({
  active,
  onClick,
  title,
  children,
}: {
  active: boolean;
  onClick: () => void;
  title?: string;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={title}
      className={`h-6 px-2 text-[11px] ${active ? 'bg-primary text-primary-foreground' : 'bg-background text-muted-foreground hover:bg-primary/10'}`}
    >
      {children}
    </button>
  );
}

/** An active method-name filter: the node indices to keep visible, the ones that matched, and the lowercased query. */
type TreeFilter = { query: string; visible: Set<number>; matched: Set<number> };

/** Method-name filter field rendered directly above the call-tree content. */
function SearchBar({
  value,
  onChange,
  matchCount,
}: {
  value: string;
  onChange: (v: string) => void;
  matchCount: number | null;
}) {
  return (
    <div className="flex flex-shrink-0 items-center gap-1.5 border-b border-border bg-card px-2 py-1">
      <Search className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
      <input
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder="Filter methods…"
        className="h-6 min-w-0 flex-1 rounded border border-border bg-background px-1.5 text-[11px] text-foreground"
      />
      {value.trim() !== '' && (
        <>
          <span className="shrink-0 font-mono tabular-nums text-muted-foreground">
            {matchCount ?? 0} match{matchCount === 1 ? '' : 'es'}
          </span>
          <button
            type="button"
            onClick={() => onChange('')}
            aria-label="Clear filter"
            className="shrink-0 text-muted-foreground hover:text-foreground"
          >
            <X className="h-3.5 w-3.5" />
          </button>
        </>
      )}
    </div>
  );
}

/**
 * The Call Tree navigation stack, rendered as a breadcrumb. Each crumb is a complete view state (time-window scope +
 * frame-root); clicking one restores that state, dropping the deeper crumbs. Crumb 0 is "All" — whole session, no
 * scope, no drill. A crumb that carries a frame-root is labelled by that method; a scope-only crumb (a catalog pick
 * or a range) by the scope label. Verbose method declarations are shown friendly, full as the tooltip.
 */
function NavBreadcrumb({ stack, onNavigate }: { stack: NavEntry[]; onNavigate: (index: number) => void }) {
  const byId = useCpuFrameStore((s) => s.byId);
  return (
    <nav
      aria-label="Call tree navigation"
      className="flex flex-shrink-0 items-center gap-1 overflow-x-auto border-b border-border bg-card px-2 py-1"
    >
      <Crosshair className="h-3 w-3 shrink-0 text-muted-foreground" aria-hidden="true" />
      {stack.map((entry, i) => {
        const isRoot = i === 0;
        const isCurrent = i === stack.length - 1;
        const full = isRoot
          ? 'Whole session'
          : entry.frameRoot != null
            ? byId.get(entry.frameRoot)?.method ?? `#${entry.frameRoot}`
            : entry.scope.label;
        const label = isRoot ? 'All' : entry.frameRoot != null ? friendlyMethodName(full) : entry.scope.label;
        return (
          <span key={i} className="flex shrink-0 items-center gap-1">
            {!isRoot && <ChevronRight className="h-3 w-3 shrink-0 text-muted-foreground/50" aria-hidden="true" />}
            {isCurrent ? (
              <span className="font-medium text-foreground" title={full}>
                {label}
              </span>
            ) : (
              <button
                type="button"
                onClick={() => onNavigate(i)}
                className="text-muted-foreground hover:text-foreground hover:underline"
                title={full}
              >
                {label}
              </button>
            )}
          </span>
        );
      })}
    </nav>
  );
}

/** Renders {@link text} with the characters that hump- or substring-match {@link query} highlighted (see methodNameMatch). */
function highlightMatch(text: string, query: string): React.ReactNode {
  const positions = matchMethodName(text, query);
  if (!positions || positions.length === 0) {
    return text;
  }
  const hi = new Set(positions);
  const segments: { text: string; hi: boolean }[] = [];
  let cur = { text: '', hi: hi.has(0) };
  for (let i = 0; i < text.length; i++) {
    const isHi = hi.has(i);
    if (isHi !== cur.hi) {
      segments.push(cur);
      cur = { text: text[i], hi: isHi };
    } else {
      cur.text += text[i];
    }
  }
  segments.push(cur);
  return (
    <>
      {segments.map((seg, i) =>
        seg.hi ? (
          <mark key={i} className="rounded-sm bg-amber-300/40 text-foreground">
            {seg.text}
          </mark>
        ) : (
          <span key={i}>{seg.text}</span>
        ),
      )}
    </>
  );
}

/** Row interaction handlers threaded from {@link CallTree} through {@link TreeBody} into every {@link TreeRow}. */
type RowHandlers = {
  /** Node index of the selected row, or null. */
  selectedIndex: number | null;
  /** The set of expanded node indices. */
  expandedSet: Set<number>;
  /** Single-click — select the row. */
  onSelect: (nodeIndex: number) => void;
  /** Double-click — open the frame's source in the editor. */
  onActivate: (nodeIndex: number) => void;
  /** Right-click — open the row context menu. */
  onContextMenu: (e: React.MouseEvent, nodeIndex: number) => void;
  /** Chevron click — toggle this node's expansion. */
  onToggleExpand: (nodeIndex: number) => void;
};

/**
 * The default hot-path expansion for a freshly-folded tree: the chain of hottest children (the server
 * sorts children hottest-first) from the root down to depth 10. Matches the pre-lift per-row
 * `onHotPath && depth < 10` default.
 */
function defaultExpandedSet(data: { nodes: CallTreeNode[] } | null): Set<number> {
  const set = new Set<number>();
  const root = data?.nodes[0];
  if (!root) {
    return set;
  }
  let idx: number | undefined = root.children[0];
  for (let depth = 0; idx != null && depth < 10; depth++) {
    set.add(idx);
    idx = data?.nodes[idx]?.children[0];
  }
  return set;
}

/** Every node index in the subtree rooted at {@link rootIndex} (inclusive), via an iterative walk. */
function collectSubtree(nodes: CallTreeNode[], rootIndex: number): number[] {
  const out: number[] = [];
  const stack = [rootIndex];
  while (stack.length > 0) {
    const i = stack.pop();
    const node = i != null ? nodes[i] : undefined;
    if (i == null || !node) {
      continue;
    }
    out.push(i);
    for (const c of node.children) {
      stack.push(c);
    }
  }
  return out;
}

function TreeBody({
  nodes,
  rootTotal,
  onDrill,
  filter,
  ...rowHandlers
}: {
  nodes: CallTreeNode[];
  rootTotal: number;
  onDrill: (frameId: number) => void;
  filter: TreeFilter | null;
} & RowHandlers) {
  const root = nodes[0];
  if (!root) {
    return null;
  }
  const childIndices = filter ? root.children.filter((c) => filter.visible.has(c)) : root.children;
  if (filter && childIndices.length === 0) {
    return <div className="px-3 py-2 text-muted-foreground">No methods match “{filter.query}”.</div>;
  }
  return (
    <div className="py-0.5">
      {childIndices.map((childIdx) => (
        <TreeRow
          key={childIdx}
          node={nodes[childIdx]}
          nodeIndex={childIdx}
          nodes={nodes}
          depth={0}
          rootTotal={rootTotal}
          onDrill={onDrill}
          filter={filter}
          {...rowHandlers}
        />
      ))}
    </div>
  );
}

function TreeRow({
  node,
  nodeIndex,
  nodes,
  depth,
  rootTotal,
  onDrill,
  filter,
  ...rowHandlers
}: {
  node: CallTreeNode;
  nodeIndex: number;
  nodes: CallTreeNode[];
  depth: number;
  rootTotal: number;
  onDrill: (frameId: number) => void;
  filter: TreeFilter | null;
} & RowHandlers) {
  const { selectedIndex, expandedSet, onSelect, onActivate, onContextMenu, onToggleExpand } = rowHandlers;
  const byId = useCpuFrameStore((s) => s.byId);

  // §8.7 — synthetic involuntary-stall aggregate (`[GC suspension]` / `[Preempted]` / `[Paging]`). It has no real frame,
  // no stack and no children: render a flat, distinct, non-interactive row — never a drill / go-to-source target.
  const involuntaryLabel = INVOLUNTARY_FRAME_LABELS[node.frameId];
  if (involuntaryLabel) {
    const involuntaryPct = rootTotal > 0 ? (node.totalSamples / rootTotal) * 100 : 0;
    return (
      <div
        className="relative flex h-[22px] cursor-default items-center gap-1 pr-2 leading-none hover:bg-primary/10"
        style={{ paddingLeft: depth * 12 + 4 }}
        title="Involuntary stall — the thread was frozen from outside (GC / scheduler / paging); the captured stack is noise (§8.7)"
      >
        <div
          className="pointer-events-none absolute inset-y-0 left-0 -z-10 bg-amber-500/15"
          style={{ width: `${Math.min(100, involuntaryPct)}%` }}
          aria-hidden="true"
        />
        <span className="w-3.5 shrink-0" aria-hidden="true" />
        <span className="min-w-0 flex-1 truncate italic text-amber-600 dark:text-amber-400">{involuntaryLabel}</span>
        <span className="w-14 shrink-0 text-right font-mono tabular-nums text-foreground" title="total %">
          {involuntaryPct.toFixed(1)}%
        </span>
        <span className="w-14 shrink-0 text-right font-mono tabular-nums text-muted-foreground">—</span>
        <span className="w-16 shrink-0 text-right font-mono tabular-nums text-muted-foreground" title="total samples">
          {node.totalSamples.toLocaleString()}
        </span>
        <span className="w-3 shrink-0" aria-hidden="true" />
      </div>
    );
  }

  const symbol = byId.get(node.frameId);
  const method = symbol?.method ?? `#${node.frameId}`;
  const friendly = friendlyMethodName(method);
  const hasSource = symbol != null && symbol.line > 0;
  const selected = nodeIndex === selectedIndex;
  const totalPct = rootTotal > 0 ? (node.totalSamples / rootTotal) * 100 : 0;
  const selfPct = rootTotal > 0 ? (node.selfSamples / rootTotal) * 100 : 0;

  // Under an active filter only the branches leading to a match are shown, and they are force-expanded so every match
  // is on screen without manual drilling.
  const childIndices = filter ? node.children.filter((c) => filter.visible.has(c)) : node.children;
  const hasChildren = childIndices.length > 0;
  const effectiveExpanded = filter != null ? true : expandedSet.has(nodeIndex);

  return (
    <div>
      <div
        className={`relative flex h-[22px] cursor-default items-center gap-1 pr-2 leading-none ${
          selected ? 'bg-primary/30' : 'hover:bg-primary/20'
        }`}
        style={{ paddingLeft: depth * 12 + 4 }}
        title={hasSource ? `${method}\n${symbol?.file}:${symbol?.line}` : method}
        onClick={() => onSelect(nodeIndex)}
        onDoubleClick={() => onActivate(nodeIndex)}
        onContextMenu={(e) => onContextMenu(e, nodeIndex)}
      >
        {/* Hot-bar — total% as a faint background fill. */}
        <div
          className="pointer-events-none absolute inset-y-0 left-0 -z-10 bg-primary/10"
          style={{ width: `${Math.min(100, totalPct)}%` }}
          aria-hidden="true"
        />
        <button
          type="button"
          onClick={(e) => {
            e.stopPropagation();
            if (hasChildren && filter == null) {
              onToggleExpand(nodeIndex);
            }
          }}
          className="w-3.5 shrink-0 text-muted-foreground"
          aria-label={hasChildren ? (effectiveExpanded ? 'Collapse' : 'Expand') : undefined}
        >
          {hasChildren ? (
            effectiveExpanded ? (
              <ChevronDown className="h-3.5 w-3.5" />
            ) : (
              <ChevronRight className="h-3.5 w-3.5" />
            )
          ) : null}
        </button>
        <span className="min-w-0 flex-1 truncate text-foreground">
          {filter ? highlightMatch(friendly, filter.query) : friendly}
          {hasSource && <FileCode className="ml-1 inline h-3 w-3 text-muted-foreground" aria-hidden="true" />}
        </span>
        <span className="w-14 shrink-0 text-right font-mono tabular-nums text-foreground" title="total %">
          {totalPct.toFixed(1)}%
        </span>
        <span className="w-14 shrink-0 text-right font-mono tabular-nums text-muted-foreground" title="self %">
          {selfPct.toFixed(1)}%
        </span>
        <span className="w-16 shrink-0 text-right font-mono tabular-nums text-muted-foreground" title="total samples">
          {node.totalSamples.toLocaleString()}
        </span>
        <button
          type="button"
          onClick={(e) => {
            e.stopPropagation();
            onDrill(node.frameId);
          }}
          className="shrink-0 text-muted-foreground hover:text-foreground"
          title="Focus the tree on this frame"
          aria-label={`Focus the call tree on ${friendly}`}
        >
          <Crosshair className="h-3 w-3" />
        </button>
      </div>
      {effectiveExpanded &&
        childIndices.map((childIdx) => (
          <TreeRow
            key={childIdx}
            node={nodes[childIdx]}
            nodeIndex={childIdx}
            nodes={nodes}
            depth={depth + 1}
            rootTotal={rootTotal}
            onDrill={onDrill}
            filter={filter}
            {...rowHandlers}
          />
        ))}
    </div>
  );
}

function CategorySidebar({ breakdown, total }: { breakdown: CategorySlice[]; total: number }) {
  return (
    <div className="w-52 shrink-0 overflow-auto border-l border-border bg-card px-2 py-1.5">
      <div className="mb-1 font-semibold text-muted-foreground">Subsystems</div>
      <CategoryBars breakdown={breakdown} total={total} />
    </div>
  );
}

function CategoryView({ breakdown, total }: { breakdown: CategorySlice[]; total: number }) {
  return (
    <div className="h-full w-full overflow-auto px-3 py-2">
      <div className="mb-1.5 font-semibold text-muted-foreground">Self-time by subsystem</div>
      <CategoryBars breakdown={breakdown} total={total} />
    </div>
  );
}

function CategoryBars({ breakdown, total }: { breakdown: CategorySlice[]; total: number }) {
  const categoryName = useCpuFrameStore((s) => s.categoryName);
  const sorted = useMemo(
    () => [...breakdown].sort((a, b) => b.selfSamples - a.selfSamples),
    [breakdown],
  );

  if (sorted.length === 0) {
    return <div className="text-muted-foreground">No category data.</div>;
  }

  return (
    <div className="flex flex-col gap-1">
      {sorted.map((slice) => {
        const pct = total > 0 ? (slice.selfSamples / total) * 100 : 0;
        return (
          <div key={slice.categoryId} className="flex items-center gap-2">
            <span className="w-24 shrink-0 truncate text-foreground" title={categoryName.get(slice.categoryId)}>
              {categoryName.get(slice.categoryId) ?? `#${slice.categoryId}`}
            </span>
            <div className="relative h-3 min-w-0 flex-1 overflow-hidden rounded-sm bg-muted">
              <div className="absolute inset-y-0 left-0 bg-primary/60" style={{ width: `${Math.min(100, pct)}%` }} />
            </div>
            <span className="w-12 shrink-0 text-right font-mono tabular-nums text-muted-foreground">
              {pct.toFixed(1)}%
            </span>
          </div>
        );
      })}
    </div>
  );
}

function EmptyState({ icon, text }: { icon?: React.ReactNode; text: string }) {
  return (
    <div className="flex h-full w-full items-center justify-center bg-background">
      <div className="max-w-md px-8 text-center text-[12px] text-muted-foreground">
        {icon}
        {text}
      </div>
    </div>
  );
}
