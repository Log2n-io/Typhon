import { useCallback, useEffect, useRef, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useQueryDefinitions } from './useQueryDefinitions';
import { toNumber } from './numeric';
import { QueryAnalyzerMaster } from './QueryAnalyzerMaster';
import { QueryDetail } from './QueryDetail';
import { useQueryAnalyzerStore, selectValidQuery } from './useQueryAnalyzerStore';

/**
 * Query Analyzer — the first-class master/detail home for query/workload analysis (#376 Stage-3
 * Phase 4, GAP-19). Consolidates the former Query Catalog + Query Plan Tree + Execution Inspector
 * into one panel: a ranked catalog (master, left) → detail (right: header + Plan / Executions tabs).
 *
 * Selection is the bus `query` leaf: a row click writes it (and the unified store); an external
 * `Query`/`Execution` selection focuses the analyzer here. Design: `query-analyzer.md`.
 */
const MIN_MASTER_PCT = 25;
const MAX_MASTER_PCT = 70;

export default function QueryAnalyzerPanel(_props: IDockviewPanelProps) {
  const sessionKind = useSessionStore((s) => s.kind);
  const sessionId = useSessionStore((s) => s.sessionId);
  const { definitions, isLoading, isError } = useQueryDefinitions();

  // Bus `query` inbound — an external Query/Execution selection focuses this view's selection.
  // `setSelectedQuery` is value-idempotent, so unrelated leaf changes are cheap no-ops.
  const leaf = useSelectionStore((s) => s.leaf);
  useEffect(() => {
    if (!sessionId || !leaf || leaf.type !== 'query' || typeof leaf.ref !== 'object' || leaf.ref === null) return;
    const r = leaf.ref as { kind?: unknown; localId?: unknown };
    const kind = Number(r.kind);
    const localId = Number(r.localId);
    if (Number.isFinite(kind) && Number.isFinite(localId)) {
      useQueryAnalyzerStore.getState().setSelectedQuery(sessionId, { kind, localId });
    }
  }, [leaf, sessionId]);

  const selectedQuery = useQueryAnalyzerStore((s) => selectValidQuery(s, sessionId));
  const selectedDefinition = selectedQuery
    ? definitions.find(
        (d) => toNumber(d.instanceId.kind) === selectedQuery.kind && toNumber(d.instanceId.localId) === selectedQuery.localId,
      ) ?? null
    : null;

  // Resizable vertical splitter between master + detail. Width is panel-local (resets on remount);
  // it isn't worth persisting. The handle drives a window-level drag so the cursor can leave the bar.
  const containerRef = useRef<HTMLDivElement | null>(null);
  const [masterPct, setMasterPct] = useState(48);
  const startDrag = useCallback(() => {
    const onMove = (ev: MouseEvent) => {
      const el = containerRef.current;
      if (!el) return;
      const rect = el.getBoundingClientRect();
      if (rect.width <= 0) return;
      const pct = ((ev.clientX - rect.left) / rect.width) * 100;
      setMasterPct(Math.min(MAX_MASTER_PCT, Math.max(MIN_MASTER_PCT, pct)));
    };
    const onUp = () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
      document.body.style.userSelect = '';
    };
    document.body.style.userSelect = 'none';
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  }, []);

  if (sessionKind !== 'trace' && sessionKind !== 'attach') {
    return <CenteredMessage><p>Query Analyzer is available in Trace and Attach sessions only.</p></CenteredMessage>;
  }
  if (isError) {
    return <CenteredMessage tone="error"><p>Failed to load query catalog.</p></CenteredMessage>;
  }
  if (isLoading) {
    return <CenteredMessage>Loading query catalog…</CenteredMessage>;
  }
  if (definitions.length === 0) {
    return (
      <CenteredMessage>
        <p>No queries were observed in this trace.</p>
        <p className="mt-1 text-fs-sm">
          The engine emits query data when the profiler is active and user queries run
          (<code className="rounded bg-muted px-1">tx.Query&lt;T&gt;()</code>, <code className="rounded bg-muted px-1">ToView()</code>, …).
          Older traces (v8 and earlier) don&apos;t carry it.
        </p>
      </CenteredMessage>
    );
  }

  return (
    <div ref={containerRef} className="flex h-full w-full overflow-hidden bg-background" data-testid="query-analyzer">
      <div className="h-full overflow-hidden" style={{ width: `${masterPct}%` }}>
        <QueryAnalyzerMaster />
      </div>
      <div
        onMouseDown={startDrag}
        className="w-1 shrink-0 cursor-col-resize bg-border hover:bg-primary/40"
        role="separator"
        aria-orientation="vertical"
        aria-label="Resize catalog / detail"
        data-testid="query-analyzer-splitter"
      />
      <div className="h-full min-w-0 flex-1 overflow-hidden">
        {selectedDefinition ? (
          <QueryDetail definition={selectedDefinition} />
        ) : (
          <CenteredMessage><p>Select a query to see its plan and executions.</p></CenteredMessage>
        )}
      </div>
    </div>
  );
}

function CenteredMessage({ children, tone }: { children: React.ReactNode; tone?: 'error' }) {
  return (
    <div className="flex h-full w-full items-center justify-center bg-background p-4 text-center">
      <div className={tone === 'error' ? 'text-fs-base text-destructive' : 'text-fs-base text-muted-foreground'}>
        {children}
      </div>
    </div>
  );
}
