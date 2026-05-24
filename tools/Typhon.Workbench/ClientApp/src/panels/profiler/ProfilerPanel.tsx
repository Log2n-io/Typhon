import { useCallback, useEffect, useMemo, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { Activity, AlertCircle, Loader2, Radio, RefreshCw, Unplug } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { usePanelHotkeys } from '@/hooks/usePanelHotkeys';
import { usePostApiSessionsTrace } from '@/api/generated/sessions/sessions';
import { logError, logInfo } from '@/stores/useLogStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useNavHistoryStore } from '@/stores/useNavHistoryStore';
import { useProfilerSelectionStore } from '@/stores/useProfilerSelectionStore';
import { useProfilerSessionStore, type ConnectionStatus } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useUiPrefsStore } from '@/stores/useUiPrefsStore';
import { useProfilerMetadata } from '@/hooks/profiler/useProfilerMetadata';
import { useProfilerBuildProgress } from '@/hooks/profiler/useProfilerBuildProgress';
import { useProfilerLiveStream } from '@/hooks/profiler/useProfilerLiveStream';
import { useProfilerCache } from '@/hooks/profiler/useProfilerCache';
import { useProfilerSourceLocations } from '@/hooks/profiler/useProfilerSourceLocations';
import { useProfilerStatsWriter } from '@/hooks/profiler/useProfilerStatsWriter';
import { useProfilerTraceStatus } from '@/hooks/profiler/useProfilerTraceStatus';
import { useProfilerStatsStore } from '@/stores/useProfilerStatsStore';
import { useRecentFilesStore } from '@/stores/useRecentFilesStore';
import { resolveInitialViewport } from '@/libs/profiler/initialViewport';
import { buildTickRows, computeSelectionIdxRange } from '@/libs/profiler/canvas/tickOverview';
import TickOverview from './sections/TickOverview';
import TimeArea from './sections/TimeArea';
import OverloadStrip from './sections/OverloadStrip';

/**
 * Empty-shell profiler panel. Handles both session kinds:
 *  - **Trace** — sidecar cache build with progress overlay, then placeholder once metadata lands.
 *  - **Attach** — live status pill + tick counter + Follow toggle; no timeline rendering yet.
 *
 * Phase 1b proves the live-data pipeline end-to-end (TCP connect → Init frame → metadata DTO → tick SSE);
 * Phase 2 lifts the Canvas 2D renderers on top.
 */
export default function ProfilerPanel(props: IDockviewPanelProps) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const token = useSessionStore((s) => s.token);
  const filePath = useSessionStore((s) => s.filePath);
  const kind = useSessionStore((s) => s.kind);

  // Panel-scoped `g` (gauge region) / `l` (legends) toggles — formerly global single-key shortcuts, re-homed
  // here so they only fire while a profiler view is focused (PC-8). Capture-phase, so `g` here takes
  // precedence over the global `g` focus-chord leader while the profiler is active.
  usePanelHotkeys(props.api, {
    g: () => useProfilerViewStore.getState().toggleGaugeRegion(),
    l: () => useUiPrefsStore.getState().toggleLegends(),
  });

  const metadata = useProfilerSessionStore((s) => s.metadata);
  const buildProgress = useProfilerSessionStore((s) => s.buildProgress);
  const buildError = useProfilerSessionStore((s) => s.buildError);
  const connectionStatus = useProfilerSessionStore((s) => s.connectionStatus);
  const latestTickNumber = useProfilerSessionStore((s) => s.latestTickNumber);
  const setIsLive = useProfilerSessionStore((s) => s.setIsLive);

  // Disconnect drops the engine TCP link but keeps the session+buffer alive for inspection. The button is
  // hidden once the runtime is already disconnected (engine quit, user already disconnected, etc.).
  const [disconnecting, setDisconnecting] = useState(false);
  const handleDisconnect = useCallback(async () => {
    if (!sessionId || disconnecting) return;
    setDisconnecting(true);
    try {
      const headers = new Headers();
      if (token) headers.set('X-Session-Token', token);
      await fetch(`/api/sessions/${sessionId}/profiler/disconnect`, { method: 'POST', headers });
    } catch {
      // Server returns 204 even if the runtime is already disconnected; transient network errors are not
      // fatal here — the connection-status SSE heartbeat is the source of truth either way.
    } finally {
      setDisconnecting(false);
    }
  }, [sessionId, token, disconnecting]);

  const isAttach = kind === 'attach';
  const isTrace = kind === 'trace';

  const commitViewRange = useProfilerViewStore((s) => s.commitViewRange);

  // Trace reload — the server watches the source .typhon-trace for re-profiling overwrites and flips a
  // flag this hook polls (~3 s). When set, the header shows a Reload button. Gated on `metadata` so it
  // doesn't poll during the build (the server only arms the watcher after the build completes anyway).
  const setSession = useSessionStore((s) => s.setSession);
  const postTrace = usePostApiSessionsTrace();
  const newVersionAvailable = useProfilerTraceStatus(isTrace && metadata ? sessionId : null);

  const handleReloadTrace = useCallback(async () => {
    if (!filePath || postTrace.isPending) return;
    try {
      // Re-POSTing the same path makes the server drop the stale TraceSession and spin a fresh one — it
      // re-fingerprints the file, sees the mismatch, and rebuilds the sidecar cache. Swapping sessionId
      // re-keys this panel: the cleanup effect below wipes every profiler store (viewRange included), so
      // the first-tick effect re-runs and the view resets cleanly onto the new run.
      const response = await postTrace.mutateAsync({ data: { filePath } });
      setSession(response.data);
      logInfo('Reloaded trace with newer on-disk version', {
        sessionId: response.data.sessionId,
        filePath: response.data.filePath ?? filePath,
      });
    } catch (err) {
      logError('Failed to reload trace', { filePath, error: String(err) });
    }
  }, [filePath, postTrace, setSession]);

  // #289 — unified chunk cache for both modes. The replay path builds the cache once on session open;
  // the live path's IncrementalCacheBuilder grows the manifest server-side and ships growth deltas, so
  // useProfilerCache observes the same expanding manifest in either mode.
  //
  // ⚠️ Important: `useProfilerCache.loadRange` gates on `viewRange.endUs > viewRange.startUs` —
  // any `{0, 0}` sentinel write here would suppress all tick loading. The viewport-restore init
  // below therefore drives off `metadata.tickSummaries` (available in one shot from the API
  // response) rather than `timeAreaTicks` (which only populates after viewRange is non-degenerate).
  const { ticks: timeAreaTicks, gaugeData, threadInfos, pendingRangesUs } = useProfilerCache(sessionId, isAttach);

  // Single producer for the viewport range-stats. RangeStatsDetail and TopSpansPanel both read the
  // result from `useProfilerStatsStore` so the O(events-in-range) aggregation runs once per click
  // rather than once per consuming panel.
  const viewRangeForStats = useProfilerViewStore((s) => s.viewRange);
  const tickSummariesForStats = useMemo(
    () => (metadata?.tickSummaries ?? null) as never,
    [metadata?.tickSummaries],
  );
  useProfilerStatsWriter(timeAreaTicks, tickSummariesForStats, viewRangeForStats);

  // On metadata arrival, restore this file's last-used viewport (per-file memory, fingerprint-gated)
  // or fall back to the first tick. Fires once per session — the `{0,0}` sentinel gate keeps it from
  // re-firing once a real range is set (user pan, URL deep-link via useSelectionBootstrap, or this
  // effect itself), and the session-change cleanup resets viewRange to `{0,0}` so it re-arms for
  // each newly opened file. Restore is trace-only: live/attach sessions have no persistent file id.
  useEffect(() => {
    const vr = useProfilerViewStore.getState().viewRange;
    if (vr.endUs > vr.startUs) return;
    if (!metadata) return;
    const saved = isTrace && filePath ? useRecentFilesStore.getState().getLastViewport(filePath) : null;
    const target = resolveInitialViewport(metadata, saved);
    if (target) commitViewRange(target);
  }, [metadata, isTrace, filePath, commitViewRange]);

  // Persist the committed viewport per file (fingerprint-tagged) so reopening the same trace lands
  // back where it was left. Skipped for the `{0,0}` sentinel and for live/attach sessions (no file
  // identity). The fingerprint lets the restore effect above reject a viewport saved against older
  // content once the trace is re-profiled — see resolveInitialViewport.
  useEffect(() => {
    if (!isTrace || !filePath) return;
    if (viewRangeForStats.endUs <= viewRangeForStats.startUs) return;
    const fingerprint = useProfilerSessionStore.getState().metadata?.fingerprint;
    if (!fingerprint) return;
    useRecentFilesStore.getState().setLastViewport(filePath, {
      fingerprint,
      startUs: viewRangeForStats.startUs,
      endUs: viewRangeForStats.endUs,
    });
  }, [viewRangeForStats, isTrace, filePath]);

  // Metadata polling runs in both Trace and Attach modes — server branches on session kind.
  // Gate on kind so the query doesn't fire for Open (DB) sessions, which would 409.
  useProfilerMetadata(isTrace || isAttach ? sessionId : null);
  // Build-progress SSE only meaningful for Trace mode; live-stream SSE only for Attach mode.
  useProfilerBuildProgress(isTrace ? sessionId : null);
  useProfilerLiveStream(isAttach ? sessionId : null);
  // #302 Phase 6: hydrate `useSourceLocationStore` so the Source row in `ProfilerDetail` can
  // resolve span siteIds. Gated on `metadata` being ready: for trace sessions the server only
  // populates the manifest after build completion (same moment metadata is set), so querying
  // earlier returns an empty manifest that gets cached by TanStack Query (retry: false).
  // For attach sessions metadata arrives after the init handshake which also carries the
  // FileTable + SourceLocationManifest frames, so by the time metadata lands the server's
  // manifest is ready too.
  useProfilerSourceLocations(isTrace || isAttach ? (metadata ? sessionId : null) : null);

  // Tell the store which mode we're in; panels and future renderers can branch off `isLive`.
  // The cleanup wipes every profiler-scoped store so switching sessions (or closing the panel)
  // doesn't leak stale SpanData references / DetailPanel selections / nav-history entries from
  // the previous trace into the next one.
  //
  // viewRange is reset to the `{0,0}` "no selection" sentinel here too: it's session-scoped, and
  // a prior trace's viewport is meaningless on the next trace. Without this, opening a second
  // trace keeps the old viewRange — the first-tick effect's sentinel gate then early-returns, so
  // the new trace never snaps to its first tick AND useProfilerCache can't load chunks for a
  // window outside the new trace's time range (it just shows empty until the user re-selects).
  // The cleanup only fires on session *change* (not first mount), so a cold-load URL deep-link
  // on the first session survives.
  useEffect(() => {
    setIsLive(isAttach);
    return () => {
      useProfilerSessionStore.getState().reset();
      useProfilerSelectionStore.getState().clear();
      useProfilerStatsStore.getState().clear();
      useNavHistoryStore.getState().clear();
      useProfilerViewStore.getState().commitViewRange({ startUs: 0, endUs: 0 });
    };
  }, [sessionId, isAttach, setIsLive]);

  const fileName = useMemo(() => {
    if (!filePath) return null;
    const parts = filePath.split(/[\\/]/);
    return parts[parts.length - 1] || filePath;
  }, [filePath]);

  // Header "selected range" indicator — maps the committed viewport (µs) to the tick numbers it
  // spans, so the top bar shows e.g. "Ticks 12–45 (34 frames)" alongside "x systems". Reuses the
  // same `computeSelectionIdxRange` the TickOverview uses to draw the selection overlay, so the
  // header label and the overlay always agree. `count` is index-based (tick numbers may have gaps).
  // `tickRows` only rebuilds when tickSummaries change.
  const tickRows = useMemo(() => buildTickRows(metadata?.tickSummaries), [metadata?.tickSummaries]);
  const selectedTickRange = useMemo(() => {
    if (tickRows.length === 0) return null;
    if (viewRangeForStats.endUs <= viewRangeForStats.startUs) return null;
    const { first, last } = computeSelectionIdxRange(tickRows, viewRangeForStats);
    if (first < 0 || last < 0) return null;
    return { first: tickRows[first].tickNumber, last: tickRows[last].tickNumber, count: last - first + 1 };
  }, [tickRows, viewRangeForStats]);

  if (!isTrace && !isAttach) {
    return (
      <div className="flex h-full w-full items-center justify-center bg-background">
        <div className="text-center text-sm text-muted-foreground">
          <Activity className="mx-auto mb-2 h-6 w-6" aria-hidden="true" />
          Open a trace file or attach to a live engine to view profiler data.
        </div>
      </div>
    );
  }

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background">
      {/* Header */}
      <div className="flex flex-shrink-0 items-center gap-3 border-b border-border bg-card px-3 py-2 text-[12px]">
        {isAttach
          ? <Radio className="h-4 w-4 text-muted-foreground" aria-label="Live profiler session" />
          : <Activity className="h-4 w-4 text-muted-foreground" aria-label="Trace profiler session" />}
        <span className="font-semibold text-foreground">
          {isAttach ? (fileName ?? 'Live engine') : (fileName ?? 'Profiler')}
        </span>

        {isAttach && (
          <>
            <StatusPill status={connectionStatus} />
            {connectionStatus !== 'disconnected' && (
              <Button
                variant="outline"
                size="sm"
                onClick={handleDisconnect}
                disabled={disconnecting}
                className="h-6 px-2 text-[11px]"
                aria-label="Disconnect from the engine"
                title="Drop the TCP link to the engine. Captured ticks remain visible for inspection; close the tab to discard them."
              >
                <Unplug className="mr-1 h-3 w-3" aria-hidden="true" />
                Disconnect
              </Button>
            )}
            <span className="text-muted-foreground">·</span>
            <span className="font-mono tabular-nums text-foreground">
              Tick {latestTickNumber.toLocaleString()}
            </span>
            {/* Auto-scroll toggle removed in #345 — live mode pins viewRange to the first tick on
               arrival and never moves it automatically thereafter. Pan/zoom interactions are
               always available; the user navigates to new ticks via TickOverview / drag-select. */}
          </>
        )}

        {isTrace && metadata && (
          <>
            <span className="text-muted-foreground">·</span>
            <span className="font-mono tabular-nums text-foreground">
              {Number(metadata.globalMetrics?.totalTicks ?? 0).toLocaleString()} ticks
            </span>
            <span className="text-muted-foreground">·</span>
            <span className="font-mono tabular-nums text-foreground">
              {formatDurationUs(
                Number(metadata.globalMetrics?.globalEndUs ?? 0) -
                  Number(metadata.globalMetrics?.globalStartUs ?? 0),
              )}
            </span>
            <span className="text-muted-foreground">·</span>
            <span className="font-mono tabular-nums text-muted-foreground">
              {metadata.header?.systemCount ?? 0} systems
            </span>
            {selectedTickRange && (
              <>
                <span className="text-muted-foreground">·</span>
                <span
                  className="font-mono tabular-nums text-foreground"
                  title="Selected range — the tick(s) spanned by the current viewport"
                >
                  {selectedTickRange.first === selectedTickRange.last
                    ? `Tick ${selectedTickRange.first.toLocaleString()} (1 frame)`
                    : `Ticks ${selectedTickRange.first.toLocaleString()}–${selectedTickRange.last.toLocaleString()}`
                      + ` (${selectedTickRange.count.toLocaleString()} frames)`}
                </span>
              </>
            )}
            {newVersionAvailable && (
              <Button
                variant="outline"
                size="sm"
                onClick={handleReloadTrace}
                disabled={postTrace.isPending}
                className="ml-auto h-6 border-amber-500/50 px-2 text-[11px] text-amber-600 hover:bg-amber-500/10 dark:text-amber-400"
                aria-label="Reload trace with the newer on-disk version"
                title="The source .typhon-trace file was overwritten on disk (a profiling re-run regenerated it). Reload to rebuild the cache from the new version — the current view resets."
              >
                <RefreshCw className="mr-1 h-3 w-3" aria-hidden="true" />
                Reload trace
              </Button>
            )}
          </>
        )}
      </div>

      {/* Body */}
      <div className="relative flex-1 overflow-hidden">
        {buildError ? (
          <ErrorState message={buildError} />
        ) : isTrace && !metadata ? (
          <BuildProgressOverlay progress={buildProgress} />
        ) : isAttach && !metadata ? (
          <LiveWaitingOverlay status={connectionStatus} />
        ) : (
          <div className="flex h-full w-full flex-col overflow-hidden">
            <TickOverview isLive={isAttach} />
            {/* Overload diagnostics strip — auto-hidden on healthy traces; surfaces multiplier/overrunRatio when the engine throttles. Issue #289. */}
            <OverloadStrip />
            <div className="flex-1 min-h-0">
              <TimeArea ticks={timeAreaTicks} gaugeData={gaugeData} threadNames={gaugeData.threadNames} threadInfos={threadInfos} pendingRangesUs={pendingRangesUs} isLive={isAttach} />
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

function StatusPill({ status }: { status: ConnectionStatus | null }) {
  const effective = status ?? 'connecting';
  const dotClass =
    effective === 'connected'
      ? 'bg-green-500'
      : effective === 'reconnecting' || effective === 'connecting'
        ? 'bg-amber-500 animate-pulse'
        : 'bg-red-500';
  const label =
    effective === 'connected'
      ? 'Connected'
      : effective === 'reconnecting'
        ? 'Reconnecting…'
        : effective === 'connecting'
          ? 'Connecting…'
          : 'Engine offline';
  return (
    <span className="inline-flex items-center gap-1.5 rounded-full border border-border bg-muted px-2 py-0.5 text-[10px] font-medium text-foreground">
      <span className={`h-2 w-2 rounded-full ${dotClass}`} />
      {label}
    </span>
  );
}

function LiveWaitingOverlay({ status }: { status: ConnectionStatus | null }) {
  return (
    <div className="flex h-full w-full items-center justify-center">
      <div className="w-full max-w-md px-8 text-center">
        <Loader2 className="mx-auto mb-3 h-5 w-5 animate-spin text-muted-foreground" aria-hidden="true" />
        <div className="text-[13px] font-semibold text-foreground">Waiting for the engine's Init frame…</div>
        <div className="mt-1 text-[11px] text-muted-foreground">
          {status === 'connected'
            ? 'TCP link is up; engine hasn\u2019t published its metadata yet.'
            : status === 'reconnecting'
              ? 'Reconnecting to the engine — the connection dropped before Init arrived.'
              : 'Establishing TCP connection to the engine.'}
        </div>
      </div>
    </div>
  );
}

function BuildProgressOverlay({
  progress,
}: {
  progress: ReturnType<typeof useProfilerSessionStore.getState>['buildProgress'];
}) {
  const pct = useMemo(() => {
    if (!progress?.totalBytes || progress.totalBytes === 0) return 0;
    return Math.min(100, Math.max(0, ((progress.bytesRead ?? 0) / progress.totalBytes) * 100));
  }, [progress]);

  return (
    <div className="flex h-full w-full items-center justify-center">
      <div className="w-full max-w-md px-8">
        <div className="mb-4 flex items-center gap-2 text-[13px] font-semibold text-foreground">
          <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
          Building trace cache…
        </div>
        <div className="mb-2 h-2 overflow-hidden rounded-full bg-muted">
          <div
            className="h-full bg-primary transition-[width] duration-200"
            style={{ width: `${pct}%` }}
          />
        </div>
        <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-[11px]">
          <dt className="text-muted-foreground">Progress</dt>
          <dd className="font-mono tabular-nums text-foreground">{pct.toFixed(1)}%</dd>
          <dt className="text-muted-foreground">Bytes</dt>
          <dd className="font-mono tabular-nums text-foreground">
            {formatBytes(progress?.bytesRead ?? 0)} / {formatBytes(progress?.totalBytes ?? 0)}
          </dd>
          <dt className="text-muted-foreground">Ticks</dt>
          <dd className="font-mono tabular-nums text-foreground">
            {(progress?.tickCount ?? 0).toLocaleString()}
          </dd>
          <dt className="text-muted-foreground">Events</dt>
          <dd className="font-mono tabular-nums text-foreground">
            {(progress?.eventCount ?? 0).toLocaleString()}
          </dd>
        </dl>
      </div>
    </div>
  );
}

function ErrorState({ message }: { message: string }) {
  return (
    <div className="flex h-full w-full items-center justify-center">
      <div className="max-w-md px-8 text-center">
        <AlertCircle className="mx-auto mb-3 h-6 w-6 text-destructive" aria-hidden="true" />
        <div className="mb-1 text-[13px] font-semibold text-foreground">
          Trace cache build failed
        </div>
        <div className="font-mono text-[11px] text-muted-foreground">{message}</div>
      </div>
    </div>
  );
}

function formatDurationUs(us: number): string {
  if (us < 1000) return `${us.toFixed(0)} µs`;
  if (us < 1_000_000) return `${(us / 1000).toFixed(1)} ms`;
  if (us < 60_000_000) return `${(us / 1_000_000).toFixed(2)} s`;
  return `${(us / 60_000_000).toFixed(1)} min`;
}

function formatBytes(b: number): string {
  if (b < 1024) return `${b} B`;
  if (b < 1024 * 1024) return `${(b / 1024).toFixed(1)} KB`;
  if (b < 1024 * 1024 * 1024) return `${(b / 1024 / 1024).toFixed(2)} MB`;
  return `${(b / 1024 / 1024 / 1024).toFixed(2)} GB`;
}
