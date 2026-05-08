import { useState } from 'react';
import { Activity, Blocks, Clock, Crosshair, ExternalLink, FileCode, Layers, Tag } from 'lucide-react';
import type { ChunkSpan, MarkerSelection, PhaseMarker, PhaseSpan, SpanData } from '@/libs/profiler/model/traceModel';
import { TraceEventKind } from '@/libs/profiler/model/types';
import type { ProfilerSelection } from '@/stores/useProfilerSelectionStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerStatsStore } from '@/stores/useProfilerStatsStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useSourceLocationStore } from '@/stores/useSourceLocationStore';
import { useOptionsStore } from '@/stores/useOptionsStore';
import { openSourcePreview } from '@/shell/commands/openSchemaBrowser';

/**
 * Profiler selection detail — the fourth DetailPanel render branch (Phase 2e). Mirrors the
 * DL-grid style of `FieldDetail` / `ResourceDetail` so a user switching between a schema-field
 * click and a profiler-span click sees the same visual language.
 *
 * Branches by `selection.kind`:
 *   - `'span'`   → span metadata: name, kind, thread slot, duration, start/end µs, depth, parent span id
 *   - `'chunk'`  → scheduler chunk: system name, chunk/thread indices, duration, entity count, isParallel
 *   - `'tick'`   → tick summary: number (no further data available without the full TickData here)
 *   - `'marker'` → memory-alloc / GC marker kind-specific fields
 */

interface Props {
  /**
   * Click selection from the profiler panel. When `null` the right-pane falls back to the range-
   * stats view, which reads its data from `useProfilerStatsStore` (populated once per click by
   * `useProfilerStatsWriter`) — no per-panel ticks/viewRange threading needed.
   */
  selection: ProfilerSelection | null;
}

export default function ProfilerDetail({ selection }: Props): React.JSX.Element | null {
  if (selection === null) {
    return <RangeStatsDetail />;
  }
  switch (selection.kind) {
    case 'span':         return <SpanDetail span={selection.span} />;
    case 'chunk':        return <ChunkDetail chunk={selection.chunk} />;
    case 'tick':         return <TickDetail tickNumber={selection.tickNumber} />;
    case 'marker':       return <MarkerDetail marker={selection.marker} />;
    case 'phase':        return <PhaseDetail phase={selection.phase} tickNumber={selection.tickNumber} />;
    case 'phase-marker': return <PhaseMarkerDetail marker={selection.marker} tickNumber={selection.tickNumber} />;
  }
}

/**
 * Shared selector for the trace's recording-start timestamp. All detail subcomponents subtract this
 * from absolute Start/End/Timestamp fields so the displayed numbers are "ms since recording start"
 * rather than the raw QPC counter. Falls back to 0 when metadata isn't loaded yet (renders absolute,
 * which is the same display pre-fix — no worse than before).
 */
function useGlobalStartUs(): number {
  return useProfilerSessionStore((s) => Number(s.metadata?.globalMetrics?.globalStartUs ?? 0));
}

/**
 * Shared className for `<dl>` containers in this panel. `select-text` opts back in to text selection
 * (the app-wide `body { user-select: none }` from `globals.css` is intentional, so values aren't
 * selectable by default — but the detail-pane numeric/textual fields are exactly what users want
 * to copy-paste). One className per `<dl>` keeps the override scoped and easy to remove.
 */
const DL_CLASS = 'grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-[11px] select-text';

// ─── Spans ─────────────────────────────────────────────────────────────────────────────────────

function SpanDetail({ span }: { span: SpanData }): React.JSX.Element {
  const globalStartUs = useGlobalStartUs();
  const resolve = useSourceLocationStore((s) => s.resolve);
  const openInEditor = useOptionsStore((s) => s.openInEditor);
  const [openError, setOpenError] = useState<string | null>(null);
  const loc = resolve(span.rawEvent?.sourceLocationId);

  async function handleOpen(): Promise<void> {
    if (!loc) return;
    setOpenError(null);
    try {
      const result = await openInEditor(loc.file, loc.line);
      if (!result.ok) setOpenError(result.error || 'Editor launch failed');
    } catch (err) {
      setOpenError((err as Error).message);
    }
  }

  return (
    <div className="flex h-full flex-col gap-3 overflow-y-auto bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <Header icon={<Activity className="h-4 w-4 text-muted-foreground" />} title={span.name} suffix="span" />
        <dl className={DL_CLASS}>
          <dt className="text-muted-foreground">Kind</dt>
          <dd className="min-w-0 truncate font-mono text-foreground">{span.kind}</dd>

          <dt className="text-muted-foreground">Thread</dt>
          <dd className="font-mono tabular-nums text-foreground">Slot {span.threadSlot}</dd>

          <dt className="text-muted-foreground">Start</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatUs(span.startUs - globalStartUs)}</dd>

          <dt className="text-muted-foreground">End</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatUs(span.endUs - globalStartUs)}</dd>

          <dt className="text-muted-foreground">Duration</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatDurationUs(span.durationUs)}</dd>

          {span.kickoffDurationUs !== undefined && (
            <>
              <dt className="text-muted-foreground">Kickoff</dt>
              <dd className="font-mono tabular-nums text-foreground">{formatDurationUs(span.kickoffDurationUs)}</dd>
            </>
          )}

          {span.depth !== undefined && (
            <>
              <dt className="text-muted-foreground">Depth</dt>
              <dd className="font-mono tabular-nums text-foreground">{span.depth}</dd>
            </>
          )}

          {span.spanId && (
            <>
              <dt className="text-muted-foreground">Span id</dt>
              <dd className="min-w-0 truncate font-mono text-foreground">{span.spanId}</dd>
            </>
          )}

          {span.parentSpanId && (
            <>
              <dt className="text-muted-foreground">Parent</dt>
              <dd className="min-w-0 truncate font-mono text-foreground">{span.parentSpanId}</dd>
            </>
          )}

          {span.traceIdHi !== undefined && span.traceIdLo !== undefined && (
            <>
              <dt className="text-muted-foreground">Trace id</dt>
              <dd className="min-w-0 truncate font-mono text-foreground">{span.traceIdHi}.{span.traceIdLo}</dd>
            </>
          )}

          {/* Kind-specific payload */}
          {span.kind === TraceEventKind.ClusterMigration && span.rawEvent && (
            <>
              {span.rawEvent.archetypeId !== undefined && (
                <>
                  <dt className="text-muted-foreground">Archetype</dt>
                  <dd className="font-mono tabular-nums text-foreground">#{span.rawEvent.archetypeId}</dd>
                </>
              )}
              {span.rawEvent.migrationCount !== undefined && (
                <>
                  <dt className="text-muted-foreground">Entities</dt>
                  <dd className="font-mono tabular-nums text-foreground">{span.rawEvent.migrationCount.toLocaleString()}</dd>
                </>
              )}
              {span.rawEvent.componentCount !== undefined && (
                <>
                  <dt className="text-muted-foreground">Components</dt>
                  <dd className="font-mono tabular-nums text-foreground">{span.rawEvent.componentCount.toLocaleString()}</dd>
                </>
              )}
            </>
          )}

          {(span.kind === TraceEventKind.PageCacheFlush || span.kind === TraceEventKind.PageCacheFlushCompleted) && span.rawEvent?.pageCount !== undefined && (
            <>
              <dt className="text-muted-foreground">Pages</dt>
              <dd className="font-mono tabular-nums text-foreground">{span.rawEvent.pageCount.toLocaleString()}</dd>
            </>
          )}

          {(span.kind === TraceEventKind.PageCacheDiskRead || span.kind === TraceEventKind.PageCacheDiskWrite
            || span.kind === TraceEventKind.PageCacheAllocatePage || span.kind === TraceEventKind.PageEvicted
            || span.kind === TraceEventKind.PageCacheDiskReadCompleted || span.kind === TraceEventKind.PageCacheDiskWriteCompleted)
            && span.rawEvent && (
            <>
              {span.rawEvent.filePageIndex !== undefined && (
                <>
                  <dt className="text-muted-foreground">File page</dt>
                  <dd className="font-mono tabular-nums text-foreground">{span.rawEvent.filePageIndex.toLocaleString()}</dd>
                </>
              )}
              {span.rawEvent.pageCount !== undefined && (
                <>
                  <dt className="text-muted-foreground">Pages</dt>
                  <dd className="font-mono tabular-nums text-foreground">{span.rawEvent.pageCount.toLocaleString()}</dd>
                </>
              )}
            </>
          )}

          {/* Source attribution */}
          {loc && (
            <>
              <dt className="text-muted-foreground">File</dt>
              <dd className="min-w-0 truncate font-mono text-foreground" title={`${loc.file}:${loc.line}`}>
                {loc.file}<span className="text-muted-foreground">:{loc.line}</span>
              </dd>
              {loc.method && (
                <>
                  <dt className="text-muted-foreground">Method</dt>
                  <dd className="min-w-0 truncate font-mono text-foreground" title={loc.method}>{loc.method}</dd>
                </>
              )}
            </>
          )}
        </dl>

        {loc && (
          <div className="mt-2 flex flex-wrap gap-2 border-t border-border pt-2">
            <button type="button" onClick={() => openSourcePreview(loc.file, loc.line)}
              className="flex items-center gap-1 rounded border border-border bg-background px-2 py-0.5 text-[11px] hover:bg-accent">
              <FileCode className="h-3 w-3" /> Show inline
            </button>
            <button type="button" onClick={handleOpen}
              className="flex items-center gap-1 rounded border border-border bg-background px-2 py-0.5 text-[11px] hover:bg-accent">
              <ExternalLink className="h-3 w-3" /> Open in editor
            </button>
            {openError && <span className="w-full truncate text-[11px] text-destructive" title={openError}>{openError.length > 60 ? openError.slice(0, 60) + '…' : openError}</span>}
          </div>
        )}
      </div>
    </div>
  );
}

// ─── Chunks ────────────────────────────────────────────────────────────────────────────────────

function ChunkDetail({ chunk }: { chunk: ChunkSpan }): React.JSX.Element {
  const globalStartUs = useGlobalStartUs();
  const resolveSystem = useSourceLocationStore((s) => s.resolveSystem);
  const openInEditor = useOptionsStore((s) => s.openInEditor);
  const [openError, setOpenError] = useState<string | null>(null);
  const loc = resolveSystem(chunk.systemIndex);

  async function handleOpen(): Promise<void> {
    if (!loc) return;
    setOpenError(null);
    try {
      const result = await openInEditor(loc.file, loc.line);
      if (!result.ok) setOpenError(result.error || 'Editor launch failed');
    } catch (err) {
      setOpenError((err as Error).message);
    }
  }

  return (
    <div className="flex h-full flex-col gap-3 overflow-y-auto bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <Header icon={<Blocks className="h-4 w-4 text-muted-foreground" />} title={chunk.systemName || `System ${chunk.systemIndex}`} suffix="chunk" />
        <dl className={DL_CLASS}>
          <dt className="text-muted-foreground">System</dt>
          <dd className="min-w-0 truncate font-mono text-foreground">#{chunk.systemIndex} {chunk.systemName}</dd>

          <dt className="text-muted-foreground">Thread</dt>
          <dd className="font-mono tabular-nums text-foreground">Slot {chunk.threadSlot}</dd>

          <dt className="text-muted-foreground">Chunk</dt>
          <dd className="font-mono tabular-nums text-foreground">
            {chunk.chunkIndex} / {chunk.totalChunks} {chunk.isParallel ? '(parallel)' : '(serial)'}
          </dd>

          <dt className="text-muted-foreground">Start</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatUs(chunk.startUs - globalStartUs)}</dd>

          <dt className="text-muted-foreground">End</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatUs(chunk.endUs - globalStartUs)}</dd>

          <dt className="text-muted-foreground">Duration</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatDurationUs(chunk.durationUs)}</dd>

          <dt className="text-muted-foreground">Entities</dt>
          <dd className="font-mono tabular-nums text-foreground">{chunk.entitiesProcessed.toLocaleString()}</dd>

          {/* Source attribution (#302) */}
          {loc && (
            <>
              <dt className="text-muted-foreground">File</dt>
              <dd className="min-w-0 truncate font-mono text-foreground" title={`${loc.file}:${loc.line}`}>
                {loc.file}<span className="text-muted-foreground">:{loc.line}</span>
              </dd>
              {loc.method && (
                <>
                  <dt className="text-muted-foreground">Method</dt>
                  <dd className="min-w-0 truncate font-mono text-foreground" title={loc.method}>{loc.method}</dd>
                </>
              )}
            </>
          )}
        </dl>

        {loc && (
          <div className="mt-2 flex flex-wrap gap-2 border-t border-border pt-2">
            <button type="button" onClick={() => openSourcePreview(loc.file, loc.line)}
              className="flex items-center gap-1 rounded border border-border bg-background px-2 py-0.5 text-[11px] hover:bg-accent">
              <FileCode className="h-3 w-3" /> Show inline
            </button>
            <button type="button" onClick={handleOpen}
              className="flex items-center gap-1 rounded border border-border bg-background px-2 py-0.5 text-[11px] hover:bg-accent">
              <ExternalLink className="h-3 w-3" /> Open in editor
            </button>
            {openError && <span className="w-full truncate text-[11px] text-destructive" title={openError}>{openError.length > 60 ? openError.slice(0, 60) + '…' : openError}</span>}
          </div>
        )}
      </div>
    </div>
  );
}

// ─── Ticks ─────────────────────────────────────────────────────────────────────────────────────

function TickDetail({ tickNumber }: { tickNumber: number }): React.JSX.Element {
  // Look up the summary entry for this tick from the metadata DTO (no need to keep the full
  // TickData around — summaries have start/duration/eventCount already). Fall back gracefully
  // when the summary isn't loaded yet.
  const tickSummary = useProfilerSessionStore((s) => {
    const summaries = s.metadata?.tickSummaries;
    if (!summaries) return null;
    for (const t of summaries) {
      if (Number(t.tickNumber) === tickNumber) return t;
    }
    return null;
  });
  const globalStartUs = useGlobalStartUs();

  return (
    <div className="flex h-full flex-col gap-3 overflow-y-auto bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <Header icon={<Clock className="h-4 w-4 text-muted-foreground" />} title={`Tick ${tickNumber}`} suffix="scheduler tick" />
        {tickSummary ? (
          <dl className={DL_CLASS}>
            <dt className="text-muted-foreground">Start</dt>
            <dd className="font-mono tabular-nums text-foreground">{formatUs(Number(tickSummary.startUs) - globalStartUs)}</dd>

            <dt className="text-muted-foreground">Duration</dt>
            <dd className="font-mono tabular-nums text-foreground">{formatDurationUs(Number(tickSummary.durationUs))}</dd>

            <dt className="text-muted-foreground">Events</dt>
            <dd className="font-mono tabular-nums text-foreground">{Number(tickSummary.eventCount).toLocaleString()}</dd>

            <dt className="text-muted-foreground">Max system</dt>
            <dd className="font-mono tabular-nums text-foreground">{formatDurationUs(Number(tickSummary.maxSystemDurationUs))}</dd>
          </dl>
        ) : (
          <p className="text-[11px] text-muted-foreground">Summary not loaded.</p>
        )}
      </div>
    </div>
  );
}

// ─── Markers ──────────────────────────────────────────────────────────────────────────────────

function MarkerDetail({ marker }: { marker: MarkerSelection }): React.JSX.Element {
  const globalStartUs = useGlobalStartUs();
  return (
    <div className="flex h-full flex-col gap-3 overflow-y-auto bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <Header icon={<Tag className="h-4 w-4 text-muted-foreground" />} title={marker.kind} suffix="marker" />
        {marker.kind === 'memory-alloc' && (
          <dl className={DL_CLASS}>
            <dt className="text-muted-foreground">Timestamp</dt>
            <dd className="font-mono tabular-nums text-foreground">{formatUs(marker.event.timestampUs - globalStartUs)}</dd>

            <dt className="text-muted-foreground">Direction</dt>
            <dd className="font-mono text-foreground">{marker.event.direction === 0 ? 'alloc' : 'free'}</dd>

            <dt className="text-muted-foreground">Size</dt>
            <dd className="font-mono tabular-nums text-foreground">{formatBytes(marker.event.sizeBytes)}</dd>

            <dt className="text-muted-foreground">Total after</dt>
            <dd className="font-mono tabular-nums text-foreground">{formatBytes(marker.event.totalAfterBytes)}</dd>

            <dt className="text-muted-foreground">Source tag</dt>
            <dd className="font-mono tabular-nums text-foreground">{marker.event.sourceTag}</dd>

            <dt className="text-muted-foreground">Thread</dt>
            <dd className="font-mono tabular-nums text-foreground">Slot {marker.event.threadSlot}</dd>
          </dl>
        )}
        {marker.kind === 'gc' && (
          <dl className={DL_CLASS}>
            <dt className="text-muted-foreground">Kind</dt>
            <dd className="font-mono text-foreground">{marker.event.kind}</dd>

            <dt className="text-muted-foreground">Timestamp</dt>
            <dd className="font-mono tabular-nums text-foreground">{formatUs(marker.event.timestampUs - globalStartUs)}</dd>

            <dt className="text-muted-foreground">Generation</dt>
            <dd className="font-mono tabular-nums text-foreground">{marker.event.generation}</dd>

            <dt className="text-muted-foreground">GC #</dt>
            <dd className="font-mono tabular-nums text-foreground">{marker.event.gcCount}</dd>

            {marker.event.pauseDurationUs !== undefined && (
              <>
                <dt className="text-muted-foreground">Pause</dt>
                <dd className="font-mono tabular-nums text-foreground">{formatDurationUs(marker.event.pauseDurationUs)}</dd>
              </>
            )}

            {marker.event.promotedBytes !== undefined && (
              <>
                <dt className="text-muted-foreground">Promoted</dt>
                <dd className="font-mono tabular-nums text-foreground">{formatBytes(marker.event.promotedBytes)}</dd>
              </>
            )}
          </dl>
        )}
      </div>
    </div>
  );
}

// ─── Phase span (RuntimePhaseSpan, e.g. WriteTickFence / UoW Flush / OutputPhase) ─────────────

function PhaseDetail({ phase, tickNumber }: { phase: PhaseSpan; tickNumber: number }): React.JSX.Element {
  const globalStartUs = useGlobalStartUs();
  return (
    <div className="flex h-full flex-col gap-3 overflow-y-auto bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <Header icon={<Layers className="h-4 w-4 text-muted-foreground" />} title={phase.phaseName} suffix="phase span" />
        <dl className={DL_CLASS}>
          <dt className="text-muted-foreground">Tick</dt>
          <dd className="font-mono tabular-nums text-foreground">{tickNumber}</dd>

          <dt className="text-muted-foreground">Phase id</dt>
          <dd className="font-mono tabular-nums text-foreground">{phase.phase}</dd>

          <dt className="text-muted-foreground">Start</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatUs(phase.startUs - globalStartUs)}</dd>

          <dt className="text-muted-foreground">End</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatUs(phase.endUs - globalStartUs)}</dd>

          <dt className="text-muted-foreground">Duration</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatDurationUs(phase.durationUs)}</dd>

          {phase.spanId && (
            <>
              <dt className="text-muted-foreground">Span id</dt>
              <dd className="truncate font-mono text-foreground">{phase.spanId}</dd>
            </>
          )}
        </dl>
      </div>
    </div>
  );
}

// ─── Phase marker (UoW Create / UoW Flush glyphs) ─────────────────────────────────────────────

function PhaseMarkerDetail({ marker, tickNumber }: { marker: PhaseMarker; tickNumber: number }): React.JSX.Element {
  const globalStartUs = useGlobalStartUs();
  return (
    <div className="flex h-full flex-col gap-3 overflow-y-auto bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <Header icon={<Tag className="h-4 w-4 text-muted-foreground" />} title={marker.label} suffix="phase marker" />
        <dl className={DL_CLASS}>
          <dt className="text-muted-foreground">Tick</dt>
          <dd className="font-mono tabular-nums text-foreground">{tickNumber}</dd>

          <dt className="text-muted-foreground">Kind</dt>
          <dd className="font-mono tabular-nums text-foreground">{marker.kind}</dd>

          <dt className="text-muted-foreground">Timestamp</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatUs(marker.timestampUs - globalStartUs)}</dd>

          {marker.detail && (
            <>
              <dt className="text-muted-foreground">Detail</dt>
              <dd className="font-mono tabular-nums text-foreground">{marker.detail}</dd>
            </>
          )}
        </dl>
      </div>
    </div>
  );
}

// ─── Range stats (fallback when no click selection) ──────────────────────────────────────────

/**
 * Aggregates over the current viewport — shown in the right-pane Detail when nothing is clicked.
 * Mirrors the user's mental model "what am I looking at?": stats follow the viewRange, not a frozen
 * drag-selection. The aggregation lives in `useProfilerStatsStore`, populated once per click by
 * `useProfilerStatsWriter` (which `ProfilerPanel` runs). This component just subscribes — keeps the
 * compute single-producer so it doesn't double up with TopSpansPanel's read of the same store.
 */
function RangeStatsDetail(): React.JSX.Element {
  // Trace timestamps are absolute QPC-based microseconds (a 64-bit value that's effectively the
  // process's wall-clock counter, not 0). Subtract `globalStartUs` so From/To/Duration display as
  // milliseconds-since-trace-start, matching the ruler's labels.
  const globalStartUs = useGlobalStartUs();
  const stats = useProfilerStatsStore((s) => s.stats);
  // Worker count drives the colour-coding on the parallel-width column in the Top Systems table —
  // without it we don't know what "good" parallelism looks like for this trace. Fall back to 0
  // (no colour, just the raw ratio) when metadata hasn't loaded yet.
  const workerCount = useProfilerSessionStore((s) => Number(s.metadata?.header?.workerCount ?? 0));
  const viewRange = useProfilerViewStore((s) => s.viewRange);
  const hasViewRange = viewRange.endUs > viewRange.startUs;
  if (stats === null) {
    return (
      <div className="flex h-full items-center justify-center bg-background p-3">
        <p className="text-[11px] text-muted-foreground">
          {hasViewRange ? 'Computing range stats…' : 'Drag a range or pan/zoom to see stats.'}
        </p>
      </div>
    );
  }

  const partial = stats.ticksTotal > 0 && stats.ticksLoaded < stats.ticksTotal;
  const coverageLabel = stats.ticksTotal === 0
    ? `${stats.ticksLoaded} ticks`
    : `${stats.ticksLoaded.toLocaleString()} / ${stats.ticksTotal.toLocaleString()} ticks loaded`;

  return (
    <div className="flex h-full flex-col gap-3 overflow-y-auto bg-background p-3">
      {/* Range header */}
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <Header icon={<Crosshair className="h-4 w-4 text-muted-foreground" />} title="Selection" suffix="range stats" />
        <dl className={DL_CLASS}>
          <dt className="text-muted-foreground">From</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatUs(stats.rangeStartUs - globalStartUs)}</dd>

          <dt className="text-muted-foreground">To</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatUs(stats.rangeEndUs - globalStartUs)}</dd>

          <dt className="text-muted-foreground">Duration</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatDurationUs(stats.rangeDurationUs)}</dd>

          <dt className="text-muted-foreground">Coverage</dt>
          <dd className={`font-mono tabular-nums ${partial ? 'text-amber-500' : 'text-foreground'}`}>{coverageLabel}</dd>

          <dt className="text-muted-foreground">Events</dt>
          <dd className="font-mono tabular-nums text-foreground">{stats.eventsLoaded.toLocaleString()}</dd>

          {stats.tickDurationStats && (
            <>
              <dt className="text-muted-foreground">Tick min / avg</dt>
              <dd className="font-mono tabular-nums text-foreground">
                {formatDurationUs(stats.tickDurationStats.minUs)} / {formatDurationUs(stats.tickDurationStats.avgUs)}
              </dd>

              <dt className="text-muted-foreground">Tick p95 / max</dt>
              <dd className="font-mono tabular-nums text-foreground">
                {formatDurationUs(stats.tickDurationStats.p95Us)} / {formatDurationUs(stats.tickDurationStats.maxUs)}
              </dd>
            </>
          )}

          <dt className="text-muted-foreground">GC pause</dt>
          <dd className="font-mono tabular-nums text-foreground">
            {formatDurationUs(stats.gcPauseTotalUs)}{stats.gcSuspensionCount > 0 ? ` (${stats.gcSuspensionCount}×)` : ''}
          </dd>
        </dl>
      </div>

      {/* Top systems by total chunk time. Wall-clock time first (latency cost), CPU time second
          (resource cost), parallel-width ratio (cpu/wall) last with colour coded against the
          worker pool size — green when the system is using most of the pool, red when it's
          parallel-but-underused. Single-threaded systems naturally land at ~1.0 → green when
          worker pool itself is 1, otherwise neutral. */}
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <div className="mb-2 flex items-baseline justify-between border-b border-border pb-1">
          <h4 className="text-[12px] font-semibold text-foreground">Top systems</h4>
          {workerCount > 0 && (
            <span className="font-mono text-[10px] text-muted-foreground" title="Worker pool size — drives the parallel-width colour">
              pool: {workerCount}
            </span>
          )}
        </div>
        {stats.topSystemsByTotal.length === 0 ? (
          <p className="text-[11px] text-muted-foreground">No chunks in range.</p>
        ) : (
          <table className="w-full text-[11px]">
            <thead>
              <tr className="text-left text-muted-foreground">
                <th className="font-normal">System</th>
                <th className="font-normal text-right" title="Σ wall-clock time the system occupied across the range (latency cost)">Wall</th>
                <th className="font-normal text-right" title="Σ chunk durations across all workers (resource cost)">CPU</th>
                <th className="font-normal text-right" title="Pool saturation while the system runs: (CPU/Wall) ÷ workerCount. 100% = every worker busy whenever this system runs. — = single-threaded by design.">Eff</th>
                <th className="font-normal text-right">Count</th>
              </tr>
            </thead>
            <tbody>
              {stats.topSystemsByTotal.map((s) => {
                const ratio = s.totalWallUs > 0 ? s.totalCpuUs / s.totalWallUs : 0;
                return (
                  <tr key={s.systemIndex} className="border-t border-border/50">
                    <td className="truncate font-mono text-foreground" title={s.systemName}>{s.systemName || `System ${s.systemIndex}`}</td>
                    <td className="text-right font-mono tabular-nums text-foreground">{formatDurationUs(s.totalWallUs)}</td>
                    <td className="text-right font-mono tabular-nums text-foreground">{formatDurationUs(s.totalCpuUs)}</td>
                    <td
                      className={`text-right font-mono tabular-nums ${parallelWidthClass(ratio, workerCount)}`}
                      title={parallelWidthTooltip(ratio, workerCount)}
                    >
                      {formatEfficiency(ratio, workerCount)}
                    </td>
                    <td className="text-right font-mono tabular-nums text-foreground">{s.count.toLocaleString()}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>

      {/* Note: the sortable Top-N expensive-spans table lives in its own dock panel
          (`TopSpansPanel`) — it needs horizontal room (7 columns) that this narrow
          right-detail strip can't give it without forcing the user to widen the column. */}
    </div>
  );
}

// ─── Helpers ──────────────────────────────────────────────────────────────────────────────────

/**
 * Colour the parallel-width column against the worker pool size. The ratio is `cpu/wall`:
 *   - Single-threaded by design: ratio ≈ 1.0 regardless of pool size — render neutral, not red.
 *     The user isn't trying to parallelize; flagging it red would be a false alarm.
 *   - Tries to parallelize but underutilized: ratio &gt; 1.5 but well below pool size — render red.
 *   - Mid: 40-75% of pool → amber.
 *   - Well-saturated: ≥75% of pool → green.
 *   - Pool size unknown (workerCount = 0) → neutral; no signal we can trust.
 */
function parallelWidthClass(ratio: number, workerCount: number): string {
  if (workerCount <= 0 || ratio < 0.05) return 'text-foreground';
  if (ratio < 1.5) return 'text-foreground'; // single-threaded by design — no judgement
  // Theme-paired tones: 700 weight reads cleanly on a white background, 300 weight on dark slate.
  // Tailwind's `dark:` prefix flips automatically with the workbench's `darkMode: 'class'` setting.
  // Plain `text-amber-300` would wash out to nearly invisible on light theme.
  const pct = ratio / workerCount;
  if (pct >= 0.75) return 'text-emerald-700 dark:text-emerald-400';
  if (pct >= 0.40) return 'text-amber-700 dark:text-amber-300';
  return 'text-red-700 dark:text-red-400';
}

function parallelWidthTooltip(ratio: number, workerCount: number): string {
  if (workerCount <= 0) return `Effective parallel width: ${ratio.toFixed(2)} workers (pool size unknown).`;
  if (ratio < 0.05) return 'No measurable activity in range.';
  if (ratio < 1.5) return `Single-threaded by design — effective width ${ratio.toFixed(2)} workers (showing 6% pool saturation would be misleading; this system isn't trying to parallelize).`;
  const pct = (ratio / workerCount) * 100;
  return `Pool saturation while running: ${pct.toFixed(0)}% — using ${ratio.toFixed(2)} of ${workerCount} workers on average. 100% = every worker busy whenever this system runs.`;
}

/**
 * Display formatter for the Eff column. Keeps the deliberate-serial / parallel split visible at a
 * glance: single-threaded systems show a dash (the percent would be misleading low — they're not
 * trying to parallelize), parallel systems show pool saturation as a percentage.
 */
function formatEfficiency(ratio: number, workerCount: number): string {
  if (workerCount <= 0 || ratio < 0.05) return '—';
  if (ratio < 1.5) return '—';
  const pct = (ratio / workerCount) * 100;
  return `${pct.toFixed(0)}%`;
}

function Header({ icon, title, suffix }: { icon: React.ReactNode; title: string; suffix: string }): React.JSX.Element {
  return (
    <div className="mb-2 flex items-center gap-2 border-b border-border pb-2">
      {icon}
      <h3 className="min-w-0 truncate text-[13px] font-semibold text-foreground">{title}</h3>
      <span className="ml-auto font-mono text-[11px] text-muted-foreground">{suffix}</span>
    </div>
  );
}

/**
 * Adaptive time formatting — picks the coarsest unit (ns / µs / ms / s) that keeps the displayed
 * number readable. Used for both absolute timestamps (Start / End / Timestamp) and durations, so
 * the Detail panel never shows "1500000.000 µs" when "1.5 s" conveys the same information. Three
 * decimals in each unit preserves enough precision to distinguish close values without becoming
 * noisy (e.g., sub-ns differences aren't worth surfacing in a detail read-out).
 */
function formatUs(us: number): string {
  const abs = Math.abs(us);
  const sign = us < 0 ? '-' : '';
  if (abs === 0) return '0 µs';
  if (abs < 1) {
    const ns = abs * 1000;
    return `${sign}${ns.toFixed(ns < 10 ? 1 : 0)} ns`;
  }
  if (abs < 1000) return `${sign}${abs.toFixed(3)} µs`;
  if (abs < 1_000_000) return `${sign}${(abs / 1000).toFixed(3)} ms`;
  return `${sign}${(abs / 1_000_000).toFixed(3)} s`;
}

/**
 * Same adaptive rule as {@link formatUs} — both timestamps and durations share the unit ladder so
 * a start-time of "2.5 s" and a duration of "1.2 s" read with matching visual grammar.
 */
const formatDurationUs = formatUs;

function formatBytes(b: number): string {
  if (b < 1024) return `${b} B`;
  if (b < 1024 * 1024) return `${(b / 1024).toFixed(1)} KiB`;
  if (b < 1024 * 1024 * 1024) return `${(b / 1024 / 1024).toFixed(2)} MiB`;
  return `${(b / 1024 / 1024 / 1024).toFixed(2)} GiB`;
}
