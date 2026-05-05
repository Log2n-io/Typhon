import { useEffect, useState } from 'react';
import { Activity, Blocks, Clock, Crosshair, ExternalLink, FileCode, Layers, Tag } from 'lucide-react';
import type { ChunkSpan, MarkerSelection, PhaseMarker, PhaseSpan, SpanData, TickData } from '@/libs/profiler/model/traceModel';
import { TraceEventKind } from '@/libs/profiler/model/types';
import type { TickSummary } from '@/libs/profiler/model/types';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';
import type { ProfilerSelection } from '@/stores/useProfilerSelectionStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { computeSelectionStats } from '@/libs/profiler/stats/selectionStats';
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
   * Click selection from the profiler panel. When `null` the user hasn't clicked anything specific
   * — caller should pass the current `ticks` + `tickSummaries` + `viewRange` so this renders the
   * range-stats fallback (aggregates across the visible viewport instead of one element's metadata).
   */
  selection: ProfilerSelection | null;
  /** Cache-resident tick data — required for the range-stats fallback. Optional otherwise. */
  ticks?: TickData[];
  /** All known tick summaries (cheap header-level data) — used to compute coverage caveats. */
  tickSummaries?: TickSummary[] | null;
  /** Current viewport — drives the range-stats aggregation. Required for the fallback. */
  viewRange?: TimeRange;
}

export default function ProfilerDetail({ selection, ticks, tickSummaries, viewRange }: Props): React.JSX.Element | null {
  if (selection === null) {
    if (ticks && viewRange) {
      return <RangeStatsDetail ticks={ticks} tickSummaries={tickSummaries ?? null} viewRange={viewRange} />;
    }
    return null;
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

/**
 * Source-location row for span details (#302). Resolves the span's `sourceLocationId` via the
 * compile-time table emitted by `SourceLocationGenerator` and offers a one-click "Open in editor"
 * handoff to the user's configured editor (VS Code / Cursor / Rider / VS / custom).
 *
 * Rendered only when the span carries a non-zero `sourceLocationId` AND the manifest is loaded.
 * For un-attributed spans (siteId = 0 — non-Engine call sites) this row simply doesn't render.
 */
function SpanSourceRow({ span }: { span: SpanData }): React.JSX.Element | null {
  const resolve = useSourceLocationStore((s) => s.resolve);
  const openInEditor = useOptionsStore((s) => s.openInEditor);
  const [error, setError] = useState<string | null>(null);

  const siteId = span.rawEvent?.sourceLocationId;
  const loc = resolve(siteId);
  if (!loc) return null;

  async function handleOpen(): Promise<void> {
    setError(null);
    try {
      const result = await openInEditor(loc!.file, loc!.line);
      if (!result.ok) {
        setError(result.error || 'Editor launch failed');
      }
    } catch (err) {
      setError((err as Error).message);
    }
  }

  const pathTitle = `${loc.file}:${loc.line}${loc.method ? ` · ${loc.method}` : ''}`;
  return (
    <div className="mt-2 flex flex-col gap-1.5 rounded-md border border-border bg-muted/30 p-2 text-[12px]">
      <div className="flex min-w-0 items-center gap-2">
        <FileCode className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
        <span className="min-w-0 flex-1 truncate select-text font-mono text-foreground" title={pathTitle}>
          {loc.file}<span className="text-muted-foreground">:{loc.line}</span>
          {loc.method && <span className="ml-2 text-muted-foreground"> · {loc.method}</span>}
        </span>
      </div>
      <div className="flex items-center gap-2">
        <button
          type="button"
          onClick={() => openSourcePreview(loc.file, loc.line)}
          className="flex items-center gap-1 rounded border border-border bg-background px-2 py-0.5 text-[11px] hover:bg-accent"
          title="Open an inline source-code preview around this line"
        >
          <FileCode className="h-3 w-3" /> Show inline
        </button>
        <button
          type="button"
          onClick={handleOpen}
          className="flex items-center gap-1 rounded border border-border bg-background px-2 py-0.5 text-[11px] hover:bg-accent"
          title="Open this file at the emission line in your configured editor"
        >
          <ExternalLink className="h-3 w-3" /> Open in editor
        </button>
        {error && (
          <span className="ml-2 truncate text-[11px] text-destructive" title={error}>
            {error.length > 60 ? error.slice(0, 60) + '…' : error}
          </span>
        )}
      </div>
    </div>
  );
}

function SpanDetail({ span }: { span: SpanData }): React.JSX.Element {
  const globalStartUs = useGlobalStartUs();
  return (
    <div className="flex h-full flex-col bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <Header icon={<Activity className="h-4 w-4 text-muted-foreground" />} title={span.name} suffix="span" />
        <dl className={DL_CLASS}>
          <dt className="text-muted-foreground">Kind</dt>
          <dd className="font-mono text-foreground">{span.kind}</dd>

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
              <dd className="truncate font-mono text-foreground">{span.spanId}</dd>
            </>
          )}

          {span.parentSpanId && (
            <>
              <dt className="text-muted-foreground">Parent</dt>
              <dd className="truncate font-mono text-foreground">{span.parentSpanId}</dd>
            </>
          )}

          {span.traceIdHi !== undefined && span.traceIdLo !== undefined && (
            <>
              <dt className="text-muted-foreground">Trace id</dt>
              <dd className="truncate font-mono text-foreground">{span.traceIdHi}.{span.traceIdLo}</dd>
            </>
          )}
        </dl>

        {/* #302: source-attribution row — file:line · method + Open in editor / Show inline buttons. */}
        <SpanSourceRow span={span} />

        <dl className={DL_CLASS}>
          {/* Kind-specific payload surfaced from the rawEvent. ClusterMigration carries archetype +
              entity count + total component instances moved (= entities × per-entity component slots). */}
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

          {/* Page cache flush — pageCount tells us how many dirty pages this SaveChanges call wrote.
              Small (1-3) = registry/bootstrap; medium (~10) = occupancy map grow; large = batched data flush. */}
          {(span.kind === TraceEventKind.PageCacheFlush || span.kind === TraceEventKind.PageCacheFlushCompleted) && span.rawEvent?.pageCount !== undefined && (
            <>
              <dt className="text-muted-foreground">Pages</dt>
              <dd className="font-mono tabular-nums text-foreground">{span.rawEvent.pageCount.toLocaleString()}</dd>
            </>
          )}

          {/* Page cache disk read/write/allocate — the file page index that was being touched. */}
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
        </dl>
      </div>
    </div>
  );
}

// ─── Chunks ────────────────────────────────────────────────────────────────────────────────────

/**
 * Source-location row for chunk details (#302 system attribution). Resolves the chunk's source via
 * the synthesized system id (<c>0x8000 | systemIndex</c>) emitted by <c>RuntimeSourceLocationManifest</c>
 * on the engine side. Same shape as <see cref="SpanSourceRow"/> — file:line · method on row 1, the two
 * actions on row 2. Renders nothing when the system has no PDB-resolved attribution.
 */
function ChunkSourceRow({ chunk }: { chunk: ChunkSpan }): React.JSX.Element | null {
  const resolveSystem = useSourceLocationStore((s) => s.resolveSystem);
  const openInEditor = useOptionsStore((s) => s.openInEditor);
  const [error, setError] = useState<string | null>(null);

  const loc = resolveSystem(chunk.systemIndex);
  if (!loc) return null;

  async function handleOpen(): Promise<void> {
    setError(null);
    try {
      const result = await openInEditor(loc!.file, loc!.line);
      if (!result.ok) {
        setError(result.error || 'Editor launch failed');
      }
    } catch (err) {
      setError((err as Error).message);
    }
  }

  const pathTitle = `${loc.file}:${loc.line}${loc.method ? ` · ${loc.method}` : ''}`;
  return (
    <div className="mt-2 flex flex-col gap-1.5 rounded-md border border-border bg-muted/30 p-2 text-[12px]">
      <div className="flex min-w-0 items-center gap-2">
        <FileCode className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
        <span className="min-w-0 flex-1 truncate select-text font-mono text-foreground" title={pathTitle}>
          {loc.file}<span className="text-muted-foreground">:{loc.line}</span>
          {loc.method && <span className="ml-2 text-muted-foreground"> · {loc.method}</span>}
        </span>
      </div>
      <div className="flex items-center gap-2">
        <button
          type="button"
          onClick={() => openSourcePreview(loc.file, loc.line)}
          className="flex items-center gap-1 rounded border border-border bg-background px-2 py-0.5 text-[11px] hover:bg-accent"
          title="Open an inline source-code preview around this line"
        >
          <FileCode className="h-3 w-3" /> Show inline
        </button>
        <button
          type="button"
          onClick={handleOpen}
          className="flex items-center gap-1 rounded border border-border bg-background px-2 py-0.5 text-[11px] hover:bg-accent"
          title="Open this file at the system entry method in your configured editor"
        >
          <ExternalLink className="h-3 w-3" /> Open in editor
        </button>
        {error && (
          <span className="ml-2 truncate text-[11px] text-destructive" title={error}>
            {error.length > 60 ? error.slice(0, 60) + '…' : error}
          </span>
        )}
      </div>
    </div>
  );
}

function ChunkDetail({ chunk }: { chunk: ChunkSpan }): React.JSX.Element {
  const globalStartUs = useGlobalStartUs();
  return (
    <div className="flex h-full flex-col bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <Header icon={<Blocks className="h-4 w-4 text-muted-foreground" />} title={chunk.systemName || `System ${chunk.systemIndex}`} suffix="chunk" />
        <dl className={DL_CLASS}>
          <dt className="text-muted-foreground">System</dt>
          <dd className="font-mono text-foreground">#{chunk.systemIndex} {chunk.systemName}</dd>

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
        </dl>

        {/* #302 system attribution: source row points to the system's entry method (PDB-resolved). */}
        <ChunkSourceRow chunk={chunk} />
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
    <div className="flex h-full flex-col bg-background p-3">
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
    <div className="flex h-full flex-col bg-background p-3">
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
    <div className="flex h-full flex-col bg-background p-3">
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
    <div className="flex h-full flex-col bg-background p-3">
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
 * drag-selection. Aggregation is debounced 150 ms so wheel-zoom doesn't fire N recomputes per second,
 * and only walks the cache-resident ticks (`ticks` prop) — the cache loader keeps the visible window
 * resident so the resident set normally covers the viewport. When the cache covers only part of the
 * range (whole-trace zoom-out past the budget) we surface that as a "X of Y ticks loaded" caveat.
 */
function RangeStatsDetail({
  ticks,
  tickSummaries,
  viewRange,
}: {
  ticks: TickData[];
  tickSummaries: TickSummary[] | null;
  viewRange: TimeRange;
}): React.JSX.Element {
  // Trace timestamps are absolute QPC-based microseconds (a 64-bit value that's effectively the
  // process's wall-clock counter, not 0). Subtract `globalStartUs` so From/To/Duration display as
  // milliseconds-since-trace-start, matching the ruler's labels.
  const globalStartUs = useGlobalStartUs();

  // Debounce: a wheel-zoom event burst flips viewRange dozens of times per second. Recomputing the
  // O(events) aggregation on each is wasteful — coalesce to one compute 150 ms after the user
  // stops interacting. The render still shows the previous stats during the burst so the panel
  // doesn't flash to "loading" on every wheel notch.
  const [debounced, setDebounced] = useState<{ ticks: TickData[]; viewRange: TimeRange }>({ ticks, viewRange });
  useEffect(() => {
    const id = setTimeout(() => setDebounced({ ticks, viewRange }), 150);
    return () => clearTimeout(id);
  }, [ticks, viewRange]);

  const stats = computeSelectionStats(debounced.ticks, tickSummaries, debounced.viewRange);
  if (stats === null) {
    return (
      <div className="flex h-full items-center justify-center bg-background p-3">
        <p className="text-[11px] text-muted-foreground">Drag a range or pan/zoom to see stats.</p>
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

      {/* Top systems by total chunk time */}
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <h4 className="mb-2 border-b border-border pb-1 text-[12px] font-semibold text-foreground">Top systems</h4>
        {stats.topSystemsByTotal.length === 0 ? (
          <p className="text-[11px] text-muted-foreground">No chunks in range.</p>
        ) : (
          <table className="w-full text-[11px]">
            <thead>
              <tr className="text-left text-muted-foreground">
                <th className="font-normal">System</th>
                <th className="font-normal text-right">Time</th>
                <th className="font-normal text-right">Count</th>
              </tr>
            </thead>
            <tbody>
              {stats.topSystemsByTotal.map((s) => (
                <tr key={s.systemIndex} className="border-t border-border/50">
                  <td className="truncate font-mono text-foreground" title={s.systemName}>{s.systemName || `System ${s.systemIndex}`}</td>
                  <td className="text-right font-mono tabular-nums text-foreground">{formatDurationUs(s.totalDurationUs)}</td>
                  <td className="text-right font-mono tabular-nums text-foreground">{s.count.toLocaleString()}</td>
                </tr>
              ))}
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

function Header({ icon, title, suffix }: { icon: React.ReactNode; title: string; suffix: string }): React.JSX.Element {
  return (
    <div className="mb-2 flex items-center gap-2 border-b border-border pb-2">
      {icon}
      <h3 className="truncate text-[13px] font-semibold text-foreground">{title}</h3>
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
