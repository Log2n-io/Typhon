import { type CSSProperties, useEffect, useState } from 'react';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { TopologyDto } from '@/api/generated/model/topologyDto';
import { useGatingStore } from '@/stores/useGatingStore';
import type { Bar } from './barBuilding';

/**
 * Hover tooltip for the Data Flow Timeline bars (spec §11.3). Reads from the cross-panel gating store
 * (lifted to `@/stores/useGatingStore`) for the "Gating: blocked by …" line — populated by either the
 * SystemDag panel or this panel itself when both are open. When neither has computed gating yet, the
 * line falls back to "—" rather than disappearing entirely.
 *
 * Positioned absolutely inside the timeline's relative container; the parent passes pixel coordinates
 * relative to that container. We auto-flip to the cursor's left when too close to the right edge.
 */
/** Subset of `SystemTickSummary` the tooltip needs. The parent extracts these fields once per hover. */
export interface BarTickStats {
  startUs: number;
  endUs: number;
  durationUs: number;
  entitiesProcessed: number;
  workersTouched: number;
  chunksProcessed: number;
  skipped: boolean;
}

export interface DataFlowTooltipProps {
  bar: Bar | null;
  /** Cursor position relative to the timeline container's bounding box (CSS pixels). */
  cursor: { x: number; y: number } | null;
  /** Tick duration in µs — for rendering the "tick 156 (2.4ms)" line. */
  tickDurationUs: number | null;
  /** Pre-resolved tick stats for the hovered bar's (system, tick) — null when not available. */
  barTickStats: BarTickStats | null;
  /** Topology — used to resolve the bar's system to a `SystemDefinitionDto` for the access-set + phase line. */
  topology: TopologyDto | null;
  /** Container width — drives the right-edge flip. */
  containerWidth: number;
}

const TOOLTIP_WIDTH = 320;
const TOOLTIP_OFFSET = 12;

export default function DataFlowTooltip({ bar, cursor, tickDurationUs, barTickStats, topology, containerWidth }: DataFlowTooltipProps) {
  // Subscribe to the gating store. Re-renders the tooltip whenever a producer panel updates gating.
  const gatingByName = useGatingStore((s) => s.gatingByName);

  // Defer rendering until cursor + bar have settled for one frame — avoids a flicker as the cursor sweeps
  // across bar boundaries and the tooltip would otherwise rapid-mount-unmount under fast hover.
  const [delayedBar, setDelayedBar] = useState<Bar | null>(null);
  useEffect(() => {
    if (!bar) {
      setDelayedBar(null);
      return;
    }
    const handle = window.setTimeout(() => setDelayedBar(bar), 30);
    return () => window.clearTimeout(handle);
  }, [bar]);

  if (!delayedBar || !cursor) return null;

  const sys = findSystemByName(topology, delayedBar.systemName);
  const gating = gatingByName?.get(delayedBar.systemName) ?? null;

  // Position: place tooltip below+right of the cursor unless that would overflow the container, in which
  // case flip horizontally and/or vertically. Y-flip uses a constant baseline since exact tooltip height
  // depends on content — overestimate to err on the side of clearance.
  const desiredLeft = cursor.x + TOOLTIP_OFFSET;
  const flipRight = desiredLeft + TOOLTIP_WIDTH > containerWidth;
  const left = flipRight ? Math.max(0, cursor.x - TOOLTIP_OFFSET - TOOLTIP_WIDTH) : desiredLeft;
  const top = cursor.y + TOOLTIP_OFFSET;
  const style: CSSProperties = {
    position: 'absolute',
    left: `${left}px`,
    top: `${top}px`,
    width: `${TOOLTIP_WIDTH}px`,
    pointerEvents: 'none',
    zIndex: 30,
  };

  const phase = sys?.phaseName || delayedBar.phaseName || '—';
  const startUs = barTickStats?.startUs ?? null;
  const endUs = barTickStats?.endUs ?? null;
  const durationUs = barTickStats?.durationUs ?? null;
  const entities = barTickStats?.entitiesProcessed ?? 0;
  const workers = barTickStats?.workersTouched ?? 0;
  const chunks = barTickStats?.chunksProcessed ?? 0;
  const skipped = barTickStats?.skipped ?? false;

  return (
    <div
      style={style}
      className="rounded-md border border-border bg-popover px-3 py-2 text-[11px] leading-tight text-popover-foreground shadow-lg"
    >
      <div className="font-semibold">
        {delayedBar.systemName}
        <span className="ml-1 font-normal text-muted-foreground">· phase {phase}</span>
      </div>
      <div className="text-muted-foreground">
        {startUs != null && endUs != null
          ? `[${formatUs(startUs)}, ${formatUs(endUs)}] · ${formatUs(durationUs ?? endUs - startUs)}${skipped ? ' · skipped' : ''}`
          : 'no tick timing'}
        {tickDurationUs != null && ` · tick ${delayedBar.tickNumber} (${formatUs(tickDurationUs)})`}
      </div>

      {sys && <AccessLine system={sys} />}

      <div className="mt-1 text-muted-foreground">
        Entities: <span className="text-foreground">{entities.toLocaleString()}</span>
        <span className="ml-2">· Workers: <span className="text-foreground">{workers}</span></span>
        <span className="ml-2">· Chunks: <span className="text-foreground">{chunks}</span></span>
      </div>

      <GatingLine gating={gating} />
    </div>
  );
}

/**
 * Render the access-set summary: which components/queues/resources this system reads, writes, etc.
 * Truncated at three names per kind so the tooltip stays readable on systems with broad access; full
 * detail lives in the side panel when the user clicks the bar.
 */
function AccessLine({ system }: { system: SystemDefinitionDto }) {
  const parts: string[] = [];
  pushKind(parts, 'W',  system.writes);
  pushKind(parts, 'sW', system.sideWrites);
  pushKind(parts, 'Rf', system.readsFresh);
  pushKind(parts, 'Rs', system.readsSnapshot);
  pushKind(parts, 'R',  system.reads);
  if (parts.length === 0) return null;
  return (
    <div className="mt-1 text-muted-foreground">
      Touches: <span className="text-foreground">{parts.join(', ')}</span>
    </div>
  );
}

function pushKind(parts: string[], label: string, names: string[] | null | undefined) {
  if (!names || names.length === 0) return;
  const shown = names.slice(0, 3).join(', ');
  const more = names.length > 3 ? ` +${names.length - 3}` : '';
  parts.push(`${shortName(shown)} [${label}]${more}`);
}

function shortName(name: string): string {
  // Strip C# namespace prefix for compactness. Keeps the type name only.
  const dot = name.lastIndexOf('.');
  return dot >= 0 ? name.slice(dot + 1) : name;
}

function GatingLine({ gating }: { gating: ReturnType<typeof useGatingStore.getState>['gatingByName'] extends Map<string, infer V> | null ? V | null : null }) {
  if (!gating) {
    return <div className="mt-1 text-muted-foreground">Gating: <span className="text-foreground">—</span></div>;
  }
  const top = gating.gaters[0];
  if (!top || top.ticksGated === 0) {
    return <div className="mt-1 text-muted-foreground">Gating: <span className="text-foreground">none ({formatUs(gating.meanWaitGapUs)} mean wait)</span></div>;
  }
  return (
    <div className="mt-1 text-muted-foreground">
      Gating: <span className="text-foreground">blocked by {shortName(top.predecessorName)} ({formatUs(top.meanPredDurationUs)} mean run)</span>
    </div>
  );
}

function findSystemByName(topology: TopologyDto | null, name: string): SystemDefinitionDto | null {
  if (!topology?.systems) return null;
  for (const s of topology.systems) if (s.name === name) return s;
  return null;
}

function formatUs(us: number | null | undefined): string {
  if (us == null || !Number.isFinite(us)) return '—';
  if (us >= 1000) return `${(us / 1000).toFixed(2)} ms`;
  if (us >= 10) return `${us.toFixed(0)} µs`;
  return `${us.toFixed(2)} µs`;
}
