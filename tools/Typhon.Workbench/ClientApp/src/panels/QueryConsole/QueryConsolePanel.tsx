import { useCallback, useEffect, useRef, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useQueryConsoleStore } from '@/stores/useQueryConsoleStore';
import { usePostApiSessionsSessionIdQueryExecute } from '@/api/generated/query-console/query-console';
import { CostChip } from './CostChip';
import { DslEditor } from './DslEditor';
import { RailTabs } from './RailTabs';
import { ResultGrid } from './ResultGrid';
import { SpecChips } from './SpecChips';
import { specToDsl } from './specToDsl';
import { useSaveCurrent } from './useSaveCurrent';
import { useQueryConsolePlan } from '@/hooks/queryConsole/useQueryConsolePlan';

// Vertical (row) split between the definition area (chips / DSL) and the result grid. Panel-local — not worth
// persisting; resets to 50/50 on remount. Clamped so neither pane can be dragged shut.
const MIN_DEF_PCT = 20;
const MAX_DEF_PCT = 80;

/**
 * Query Console entry component (#386 Phase 1 — AC-9). Composes the toolbar, the editor (chip ⇄ DSL toggle), the
 * left rail (saved queries + history), and the result grid. Disabled in non-Open sessions with a tooltip — matches
 * the Database File Map gate. Run / Save / Share live in the toolbar; the panel reads from
 * {@link useQueryConsoleStore} and dispatches mutations via the orval-generated `usePostApiSessionsSessionIdQueryExecute`.
 *
 * Layout (per design §3 wireframe, simplified for Phase 1):
 *   ┌───────────────── toolbar (mode toggle · cost chip · Run / Save / Share) ────────────────┐
 *   │ rail  │  editor (chip or DSL)                                                            │
 *   │       ├──────────────────────────────────────────────────────────────────────────────────┤
 *   │       │  result grid                                                                     │
 *   └───────┴──────────────────────────────────────────────────────────────────────────────────┘
 */
export default function QueryConsolePanel(_props: IDockviewPanelProps) {
  const sessionKind = useSessionStore((s) => s.kind);
  const dslDraft = useQueryConsoleStore((s) => s.dslDraft);
  const setDslDraft = useQueryConsoleStore((s) => s.setDslDraft);
  const mode = useQueryConsoleStore((s) => s.mode);
  const setMode = useQueryConsoleStore((s) => s.setMode);
  const spec = useQueryConsoleStore((s) => s.spec);
  const runState = useQueryConsoleStore((s) => s.runState);
  const setRunResult = useQueryConsoleStore((s) => s.setRunResult);
  const appendHistory = useQueryConsoleStore((s) => s.appendHistory);
  const sessionId = useSessionStore((s) => s.sessionId);

  const executeMutation = usePostApiSessionsSessionIdQueryExecute();
  const saveCurrent = useSaveCurrent();

  // Live cost chip — debounced /plan on DSL changes.
  useQueryConsolePlan(dslDraft);

  // Resizable horizontal splitter between the definition area (top) and the result grid (bottom). The handle drives a
  // window-level drag so the cursor can leave the 1px bar without dropping the gesture (mirrors QueryAnalyzerPanel).
  const splitRef = useRef<HTMLDivElement | null>(null);
  const [defPct, setDefPct] = useState(50);
  const startDrag = useCallback(() => {
    const onMove = (ev: MouseEvent) => {
      const el = splitRef.current;
      if (!el) return;
      const rect = el.getBoundingClientRect();
      if (rect.height <= 0) return;
      const pct = ((ev.clientY - rect.top) / rect.height) * 100;
      setDefPct(Math.min(MAX_DEF_PCT, Math.max(MIN_DEF_PCT, pct)));
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

  // URL → DSL boot-strap (design §4.9 sharing): #qc=base64(JSON({dsl})) → mounts DSL, never auto-runs.
  useEffect(() => {
    if (window.location.hash.startsWith('#qc=')) {
      try {
        const decoded = JSON.parse(atob(window.location.hash.slice(4)));
        if (typeof decoded.dsl === 'string') {
          setDslDraft(decoded.dsl);
        }
      } catch {
        /* ignore malformed share URLs */
      }
    }
    // Boot-strap only; intentional one-shot on mount.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (sessionKind !== 'open') {
    return (
      <div className="flex h-full items-center justify-center text-sm text-muted-foreground">
        Query Console requires an open <span className="mx-1 rounded border border-border bg-muted px-1 font-mono">.typhon</span> file.
      </div>
    );
  }

  const onRun = () => {
    if (!sessionId || !dslDraft.trim()) return;
    setRunResult(null, 'running');
    const start = performance.now();
    executeMutation.mutate(
      { sessionId, data: { dsl: dslDraft, revision: { kind: 'head', value: 0, timeIso: '' }, pageOffset: 0, pageSize: spec.take || 1000 } },
      {
        onSuccess: (resp) => {
          // Orval wraps the body in `{ data, headers }` — unwrap to reach the QueryResultDto.
          const result = resp.data;
          setRunResult(result, 'ok');
          appendHistory({
            dsl: dslDraft,
            ranAt: Date.now(),
            rowCount: result.rows?.length ?? 0,
            elapsedNs: Number(result.executionWallNs ?? 0),
            errorCode: null,
          });
        },
        onError: (err: unknown) => {
          const e = err as { status?: number; data?: { title?: string; detail?: string } };
          const code = e?.data?.title ?? 'error';
          const message = e?.data?.detail ?? String(err);
          setRunResult(null, 'error', code, message);
          appendHistory({
            dsl: dslDraft,
            ranAt: Date.now(),
            rowCount: 0,
            elapsedNs: Math.round((performance.now() - start) * 1_000_000),
            errorCode: code,
          });
        },
      },
    );
  };

  const onShare = async () => {
    const hash = `#qc=${btoa(JSON.stringify({ dsl: dslDraft }))}`;
    const url = `${window.location.origin}${window.location.pathname}${hash}`;
    try {
      await navigator.clipboard.writeText(url);
    } catch {
      /* clipboard denied — paste manually from prompt */
      window.prompt('Copy this URL:', url);
    }
  };

  const switchToDsl = () => {
    // Serialize current spec to DSL on toggle so chip → DSL is lossless without a server round-trip.
    if (mode === 'chips') setDslDraft(specToDsl(spec));
    setMode('dsl');
  };
  const switchToChips = () => {
    // DSL → chips relies on the debounced /parse already keeping `spec` in sync — no extra work needed here.
    setMode('chips');
  };

  return (
    <div className="flex h-full flex-col text-sm">
      {/* Toolbar */}
      <div className="flex items-center gap-2 border-b border-border bg-muted/20 px-2 py-1">
        <div className="flex rounded border border-border text-xs">
          <button
            onClick={switchToChips}
            className={`px-2 py-1 ${mode === 'chips' ? 'bg-muted font-semibold' : ''}`}
          >
            Chips
          </button>
          <button
            onClick={switchToDsl}
            className={`border-l border-border px-2 py-1 ${mode === 'dsl' ? 'bg-muted font-semibold' : ''}`}
          >
            DSL
          </button>
        </div>
        <span className="text-xs text-muted-foreground">As-of: HEAD</span>
        <CostChip />
        <div className="flex-1" />
        <button
          onClick={onRun}
          disabled={runState === 'running' || !sessionId}
          className="rounded bg-primary px-3 py-1 text-xs font-semibold text-primary-foreground disabled:opacity-50"
        >
          {runState === 'running' ? 'Running…' : '▶ Run'}
        </button>
        <button onClick={saveCurrent} className="rounded border border-border px-2 py-1 text-xs">Save</button>
        <button onClick={onShare} className="rounded border border-border px-2 py-1 text-xs">Share</button>
      </div>

      <div className="flex flex-1 overflow-hidden">
        <div className="w-48 shrink-0"><RailTabs /></div>
        <div ref={splitRef} className="flex flex-1 flex-col overflow-hidden">
          <div className="overflow-auto" style={{ height: `${defPct}%` }}>
            {mode === 'dsl' ? <DslEditor /> : <SpecChips />}
          </div>
          <div
            onMouseDown={startDrag}
            className="h-1 shrink-0 cursor-row-resize bg-border hover:bg-primary/40"
            role="separator"
            aria-orientation="horizontal"
            aria-label="Resize definition / results"
            data-testid="query-console-splitter"
          />
          <div className="min-h-0 flex-1 overflow-hidden">
            <ResultGrid />
          </div>
        </div>
      </div>
    </div>
  );
}
