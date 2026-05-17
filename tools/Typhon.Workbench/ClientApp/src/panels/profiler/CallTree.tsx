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
  type CallTreeNode,
  type CallTreeRequest,
  type CallTreeViewMode,
  type CategorySlice,
} from '@/hooks/profiler/useCallTree';
import { useSampleDensity } from '@/hooks/profiler/useSampleDensity';

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
  // The frame-root drill is a dedicated, panel-local navigation stack (not the global nav history):
  // each Crosshair "focus" pushes a frame, the breadcrumb pops back to any level. The active root —
  // what the fold re-roots at — is the top of the stack.
  const [frameRootStack, setFrameRootStack] = useState<number[]>([]);
  const [filterText, setFilterText] = useState('');
  const [appliedFilter, setAppliedFilter] = useState('');
  const byId = useCpuFrameStore((s) => s.byId);

  const activeFrameRoot = frameRootStack.length > 0 ? frameRootStack[frameRootStack.length - 1] : null;

  // Re-rooting (a Crosshair drill or a breadcrumb jump) clears the filter — it was a find tool for
  // the previous tree, and a stale query against the re-rooted tree just yields a confusing view.
  const drillInto = useCallback((frameId: number) => {
    setFrameRootStack((s) => [...s, frameId]);
    setFilterText('');
    setAppliedFilter('');
  }, []);
  const navigateBreadcrumb = useCallback((depth: number) => {
    setFrameRootStack((s) => s.slice(0, depth));
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

  // The scope is a cross-panel command channel. A scope set for a different session is stale — fall back to whole-session.
  const storeScope = useCallTreeScopeStore((s) => s.scope);
  const ownerSessionId = useCallTreeScopeStore((s) => s.ownerSessionId);
  const scope = ownerSessionId != null && ownerSessionId === sessionId ? storeScope : WHOLE_SESSION_SCOPE;

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

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background text-[11px]">
      <Toolbar
        sessionId={sessionId}
        viewMode={viewMode}
        onViewMode={setViewMode}
        groupByCategory={groupByCategory}
        onGroupByCategory={setGroupByCategory}
        scope={scope}
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
              {frameRootStack.length > 0 && (
                <FocusBreadcrumb stack={frameRootStack} onNavigate={navigateBreadcrumb} />
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
                />
              </div>
            </div>
            <CategorySidebar breakdown={data.categoryBreakdown} total={data.totalSamples} />
          </div>
        )}
      </div>
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
  data: { totalSamples: number; managedSamples: number; externalSamples: number } | null;
}) {
  const { sessionId, scope } = props;
  const metadata = useProfilerSessionStore((s) => s.metadata);
  const setScope = useCallTreeScopeStore((s) => s.setScope);
  const resetScope = useCallTreeScopeStore((s) => s.reset);

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
      resetScope();
    } else {
      setScope(sessionId, rangeScope(s, e));
    }
  };

  return (
    <div className="flex flex-shrink-0 flex-wrap items-center gap-2 border-b border-border bg-card px-2 py-1.5">
      <div className="flex items-center overflow-hidden rounded border border-border">
        <ModeButton active={props.viewMode === 'on-cpu'} onClick={() => props.onViewMode('on-cpu')}>
          On-CPU
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
          setScope(sessionId, systemScope(idx, sys?.name ?? `#${idx}`));
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
          setScope(sessionId, phaseScope(e.target.value));
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

      {/* Active scope chip — shown for any scope other than whole-session. */}
      {scope.kind !== 'session' && (
        <span className="flex items-center gap-1 rounded bg-accent/20 px-1.5 py-0.5 text-foreground">
          {scope.label}
          <button
            type="button"
            onClick={resetScope}
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

function ModeButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      onClick={onClick}
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
 * The frame-root drill path, rendered above the call-tree content. Each Crosshair "focus" pushed a
 * crumb; clicking a crumb re-roots the tree at that frame (popping the deeper crumbs); the leading
 * "root" crumb clears the drill entirely. Verbose declarations are shown friendly, full as tooltip.
 */
function FocusBreadcrumb({ stack, onNavigate }: { stack: number[]; onNavigate: (depth: number) => void }) {
  const byId = useCpuFrameStore((s) => s.byId);
  return (
    <nav
      aria-label="Call tree focus"
      className="flex flex-shrink-0 items-center gap-1 overflow-x-auto border-b border-border bg-card px-2 py-1"
    >
      <Crosshair className="h-3 w-3 shrink-0 text-muted-foreground" aria-hidden="true" />
      <button
        type="button"
        onClick={() => onNavigate(0)}
        className="shrink-0 text-muted-foreground hover:text-foreground hover:underline"
      >
        root
      </button>
      {stack.map((frameId, i) => {
        const full = byId.get(frameId)?.method ?? `#${frameId}`;
        const label = friendlyMethodName(full);
        const isCurrent = i === stack.length - 1;
        return (
          <span key={i} className="flex shrink-0 items-center gap-1">
            <ChevronRight className="h-3 w-3 shrink-0 text-muted-foreground/50" aria-hidden="true" />
            {isCurrent ? (
              <span className="font-medium text-foreground" title={full}>
                {label}
              </span>
            ) : (
              <button
                type="button"
                onClick={() => onNavigate(i + 1)}
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

function TreeBody({
  nodes,
  rootTotal,
  onDrill,
  filter,
}: {
  nodes: CallTreeNode[];
  rootTotal: number;
  onDrill: (frameId: number) => void;
  filter: TreeFilter | null;
}) {
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
      {childIndices.map((childIdx, i) => (
        <TreeRow
          key={childIdx}
          node={nodes[childIdx]}
          nodes={nodes}
          depth={0}
          rootTotal={rootTotal}
          onHotPath={i === 0}
          onDrill={onDrill}
          filter={filter}
        />
      ))}
    </div>
  );
}

function TreeRow({
  node,
  nodes,
  depth,
  rootTotal,
  onHotPath,
  onDrill,
  filter,
}: {
  node: CallTreeNode;
  nodes: CallTreeNode[];
  depth: number;
  rootTotal: number;
  onHotPath: boolean;
  onDrill: (frameId: number) => void;
  filter: TreeFilter | null;
}) {
  const [expanded, setExpanded] = useState(onHotPath && depth < 10);
  const byId = useCpuFrameStore((s) => s.byId);
  const openInEditor = useOptionsStore((s) => s.openInEditor);

  const symbol = byId.get(node.frameId);
  const method = symbol?.method ?? `#${node.frameId}`;
  const friendly = friendlyMethodName(method);
  const hasSource = symbol != null && symbol.line > 0;
  const totalPct = rootTotal > 0 ? (node.totalSamples / rootTotal) * 100 : 0;
  const selfPct = rootTotal > 0 ? (node.selfSamples / rootTotal) * 100 : 0;

  // Under an active filter only the branches leading to a match are shown, and they are force-expanded so every match
  // is on screen without manual drilling.
  const childIndices = filter ? node.children.filter((c) => filter.visible.has(c)) : node.children;
  const hasChildren = childIndices.length > 0;
  const effectiveExpanded = filter != null ? true : expanded;

  const onOpenSource = useCallback(() => {
    if (hasSource && symbol) {
      void openInEditor(symbol.file, symbol.line);
    }
  }, [hasSource, symbol, openInEditor]);

  return (
    <div>
      <div
        className="relative flex h-[22px] cursor-default items-center gap-1 pr-2 leading-none hover:bg-primary/20"
        style={{ paddingLeft: depth * 12 + 4 }}
        title={hasSource ? `${method}\n${symbol?.file}:${symbol?.line}` : method}
      >
        {/* Hot-bar — total% as a faint background fill. */}
        <div
          className="pointer-events-none absolute inset-y-0 left-0 -z-10 bg-primary/10"
          style={{ width: `${Math.min(100, totalPct)}%` }}
          aria-hidden="true"
        />
        <button
          type="button"
          onClick={() => hasChildren && filter == null && setExpanded((v) => !v)}
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
        <span
          className={`min-w-0 flex-1 truncate ${hasSource ? 'cursor-pointer hover:text-accent hover:underline' : 'text-foreground'}`}
          onClick={onOpenSource}
        >
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
          onClick={() => onDrill(node.frameId)}
          className="shrink-0 text-muted-foreground hover:text-foreground"
          title="Focus the tree on this frame"
          aria-label={`Focus the call tree on ${friendly}`}
        >
          <Crosshair className="h-3 w-3" />
        </button>
      </div>
      {effectiveExpanded &&
        childIndices.map((childIdx, i) => (
          <TreeRow
            key={childIdx}
            node={nodes[childIdx]}
            nodes={nodes}
            depth={depth + 1}
            rootTotal={rootTotal}
            onHotPath={onHotPath && i === 0}
            onDrill={onDrill}
            filter={filter}
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
