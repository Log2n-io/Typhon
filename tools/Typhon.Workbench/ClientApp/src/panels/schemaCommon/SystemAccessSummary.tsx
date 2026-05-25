/**
 * System declared-access summary — the RFC-07 access chips for one system: reads / reads-fresh / reads-snapshot /
 * writes / side-writes, the event read+write sides, and the resource read+write sides; plus a note when the trace
 * carries no declarations.
 *
 * This is the **single** rendering of a system's declared access (PC-3: one detail surface). Used by the Inspector's
 * System card (`DetailPanel`) and the System DAG side panel — both pass a system's already-resolved access arrays
 * (the DAG via `DagNodeData`, the Inspector via `toNodeData()`), so the two surfaces stay byte-identical.
 */
import type { NoAccessReason } from '@/panels/SystemDag/dagModel';

export interface SystemAccessSummaryProps {
  reads: string[];
  readsFresh: string[];
  readsSnapshot: string[];
  writes: string[];
  sideWrites: string[];
  readsEvents: string[];
  writesEvents: string[];
  readsResources: string[];
  writesResources: string[];
  /** False → render the "no RFC 07 declarations" note instead of (empty) chip groups. */
  hasAccess: boolean;
  /**
   * When `hasAccess` is false, selects which explanatory note to show. Defaults to `'trace-empty'`
   * (the historical copy) when omitted. Callers that hold the topology should pass
   * `resolveNoAccessReason(topology, dagId)` so an engine-internal system reads as "no declarations
   * by design" rather than wrongly blaming the trace version.
   */
  noAccessReason?: NoAccessReason;
}

type AccessTone =
  | 'read'
  | 'fresh'
  | 'snapshot'
  | 'write'
  | 'side-write'
  | 'event-read'
  | 'event-write'
  | 'resource-read'
  | 'resource-write';

export default function SystemAccessSummary(props: SystemAccessSummaryProps): React.JSX.Element {
  return (
    <div data-testid="system-access-summary">
      <AccessGroup label="reads" tone="read" items={props.reads} />
      <AccessGroup label="reads fresh" tone="fresh" items={props.readsFresh} />
      <AccessGroup label="reads snapshot" tone="snapshot" items={props.readsSnapshot} />
      <AccessGroup label="writes" tone="write" items={props.writes} />
      <AccessGroup label="side-writes" tone="side-write" items={props.sideWrites} />
      {/* Event queues — producer (writes) and consumer (reads) shown separately; violet to pair with the
          dashed event edges on the DAG canvas. */}
      <AccessGroup label="reads events" tone="event-read" items={props.readsEvents} />
      <AccessGroup label="writes events" tone="event-write" items={props.writesEvents} />
      {/* Named resources — no Fresh/Snapshot variant; red to pair with the dotted resource edges. */}
      <AccessGroup label="reads resources" tone="resource-read" items={props.readsResources} />
      <AccessGroup label="writes resources" tone="resource-write" items={props.writesResources} />

      {!props.hasAccess && <NoAccessNote reason={props.noAccessReason ?? 'trace-empty'} />}
    </div>
  );
}

/**
 * The note shown when a system declares no access. Copy + tone depend on *why*: an engine-internal
 * system (privileged, no RFC 07 by design) and a genuine "declares nothing" user system are neutral
 * informational notes; only `trace-empty` — no system in the whole trace carries access — keeps the
 * amber caution, since that's the one case that actually points at a stale trace or un-recompiled host.
 */
function NoAccessNote({ reason }: { reason: NoAccessReason }): React.JSX.Element {
  if (reason === 'engine-internal') {
    return (
      <div className="mt-2 rounded border border-border bg-muted/40 px-2 py-1.5 text-fs-xs text-muted-foreground">
        Engine-internal system — runs with privileged access and is ordered by explicit edges. No RFC 07 declarations by design.
      </div>
    );
  }
  if (reason === 'declares-none') {
    return (
      <div className="mt-2 rounded border border-border bg-muted/40 px-2 py-1.5 text-fs-xs text-muted-foreground">
        This system declares no RFC 07 access — it reads and writes no component, event, or resource.
      </div>
    );
  }
  return (
    <div className="mt-2 rounded border border-amber-300 bg-amber-50 px-2 py-1.5 text-fs-xs text-amber-900 dark:border-amber-700/40 dark:bg-amber-950/30 dark:text-amber-200">
      No RFC 07 access declarations on the wire — the trace may predate wire v6, or the host wasn't recompiled.
    </div>
  );
}

function AccessGroup({ label, tone, items }: { label: string; tone: AccessTone; items: string[] }): React.JSX.Element | null {
  if (items.length === 0) {
    return null;
  }
  return (
    <div className="mb-2">
      <div className="text-fs-xs font-semibold uppercase tracking-wide text-muted-foreground">{label}</div>
      <div className="mt-0.5 flex flex-wrap items-center gap-1">
        {items.map((item) => (
          <span key={item} className={`rounded border px-1.5 py-0.5 font-mono text-fs-xs ${toneClasses(tone)}`}>
            {item}
          </span>
        ))}
      </div>
    </div>
  );
}

/**
 * Theme-paired chip tones. Each chip uses light-bg + dark-text on the light theme and dark-bg + light-text on the
 * dark theme; the hue family pairs with the corresponding System DAG edge style. Producer (write) sides get a
 * slightly bolder tint than consumer (read) sides so the two read at a glance.
 */
function toneClasses(tone: AccessTone): string {
  switch (tone) {
    case 'read':
      return 'border-slate-300 bg-slate-100 text-slate-800 dark:border-slate-600/50 dark:bg-slate-900/40 dark:text-slate-200';
    case 'fresh':
      return 'border-emerald-300 bg-emerald-100 text-emerald-800 dark:border-emerald-700/50 dark:bg-emerald-950/40 dark:text-emerald-200';
    case 'snapshot':
      return 'border-sky-300 bg-sky-100 text-sky-800 dark:border-sky-700/50 dark:bg-sky-950/40 dark:text-sky-200';
    case 'write':
      return 'border-rose-300 bg-rose-100 text-rose-800 dark:border-rose-700/50 dark:bg-rose-950/40 dark:text-rose-200';
    case 'side-write':
      return 'border-orange-300 bg-orange-100 text-orange-800 dark:border-orange-700/50 dark:bg-orange-950/40 dark:text-orange-200';
    case 'event-read':
      return 'border-violet-300 bg-violet-100 text-violet-800 dark:border-violet-700/50 dark:bg-violet-950/40 dark:text-violet-200';
    case 'event-write':
      return 'border-violet-400 bg-violet-200 text-violet-900 dark:border-violet-600/60 dark:bg-violet-900/50 dark:text-violet-100';
    case 'resource-read':
      return 'border-red-300 bg-red-100 text-red-800 dark:border-red-800/50 dark:bg-red-950/40 dark:text-red-200';
    case 'resource-write':
      return 'border-red-400 bg-red-200 text-red-900 dark:border-red-700/60 dark:bg-red-900/50 dark:text-red-100';
  }
}
