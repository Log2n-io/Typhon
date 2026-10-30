import { Globe, Link2, MessageSquare } from 'lucide-react';
import { useMemo } from 'react';
import { formatDuration } from '@/libs/profiler/canvas/canvasUtils';
import type { NavTarget } from '@/libs/profiler/correlation/causalFlow';
import { isCommandCausalKey, type CausalMarker, type RequestRow } from '@/libs/profiler/correlation/requests';
import { ROOT_COMMANDS_TRACK_ID, ROOT_HTTP_TRACK_ID } from '@/libs/profiler/correlation/rootLanes';
import type { SpanData } from '@/libs/profiler/model/traceModel';
import { cn } from '@/lib/utils';
import { useProfilerRequestsStore } from '@/stores/useProfilerRequestsStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';

/** Links shown at most; a request can cause thousands of spans, and the list is for jumping, not for reading them all. */
const MAX_LINKS = 50;

const STEP_LABELS: Record<CausalMarker['kind'], string> = {
  received: 'Received by the transport',
  admitted: 'Admitted to a tick',
  consumed: 'Read by a system',
  sent: 'Answer frame sent',
};

/**
 * The Details pane's correlation section for a span carrying a cause (#1517): the request or command it served, what that root cost, and every other
 * span the same root caused — each a link that selects the span and brings it into view in the time area.
 */
export function CorrelationLinks({ span, causalKey }: { span: SpanData; causalKey: string }): React.JSX.Element {
  const allRows = useProfilerRequestsStore((s) => s.allRows);
  const root = useMemo(() => pickRoot(allRows, causalKey, span), [allRows, causalKey, span]);
  const isCommand = isCommandCausalKey(causalKey);

  return (
    <div className="rounded-md border border-border bg-card p-3 text-fs-base" data-testid="correlation-links">
      <CardHeader icon={<Link2 className="h-4 w-4 text-muted-foreground" />} title="Correlation" suffix={isCommand ? 'command' : 'request'} />
      <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-fs-sm select-text">
        <dt className="text-muted-foreground">Caused by</dt>
        <dd className="min-w-0 truncate font-mono text-foreground">
          {root !== undefined ? (
            <button type="button" className="truncate underline decoration-dotted hover:text-primary" onClick={() => selectRoot(root)}
              title="Show the request in this pane and bring its bar into view">
              {root.label}{root.status !== undefined ? ` ${root.status}` : ''}
            </button>
          ) : (
            <span className="text-muted-foreground">{isCommand ? 'a client command' : 'an external request'} outside the loaded range</span>
          )}
        </dd>
        {root !== undefined && <RootSummaryRows row={root} />}
      </dl>
      <PinButton causalKey={causalKey} />
      <RelatedSpans causalKey={causalKey} current={span} originUs={root?.startUs ?? span.startUs} />
    </div>
  );
}

/**
 * The Details pane for a root picked in the time area's HTTP or Commands lane (#1517): what it was, where its time went, each step of a command's
 * life (a link to where it happened), and the spans it caused.
 */
export function RequestDetail({ row }: { row: RequestRow }): React.JSX.Element {
  const globalStartUs = useProfilerSessionStore((s) => Number(s.metadata?.globalMetrics?.globalStartUs ?? 0));
  const kindLabel = row.kind === 'http' ? 'HTTP request' : row.kind === 'command' ? 'client command' : 'dropped command';
  const icon = row.kind === 'http'
    ? <Globe className="h-4 w-4 text-muted-foreground" />
    : <MessageSquare className="h-4 w-4 text-muted-foreground" />;

  return (
    <div className="flex h-full flex-col gap-3 overflow-y-auto bg-background p-3" data-testid="request-detail">
      <div className="rounded-md border border-border bg-card p-3 text-fs-base">
        <CardHeader icon={icon} title={row.label} suffix={kindLabel} />
        <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-fs-sm select-text">
          <Row label="Start">{formatDuration(row.startUs - globalStartUs)}</Row>
          {row.status !== undefined && <Row label="Status">{row.status}</Row>}
          {row.kind !== 'dropped' && <RootSummaryRows row={row} />}
          {row.kind === 'dropped' && row.session !== undefined && <Row label="Session">{row.session}</Row>}
        </dl>
        {row.kind !== 'dropped' && <PinButton causalKey={row.key} />}
      </div>

      {row.markers.length > 0 && (
        <div className="rounded-md border border-border bg-card p-3 text-fs-base">
          <CardHeader icon={<Link2 className="h-4 w-4 text-muted-foreground" />} title="Lifecycle" suffix={`${row.markers.length} steps`} />
          <ul className="flex flex-col" data-testid="request-steps">
            {row.markers.map((m) => (
              <li key={m.kind}>
                <button
                  type="button"
                  data-testid="request-step"
                  className="grid w-full grid-cols-[1fr,auto,auto] gap-x-3 rounded px-1 py-0.5 text-left text-fs-sm hover:bg-accent/60"
                  onClick={() => goToStep(m)}
                  title="Bring this step into view"
                >
                  <span className="min-w-0 truncate text-foreground">{STEP_LABELS[m.kind]}</span>
                  <span className="font-mono tabular-nums text-muted-foreground">slot {m.slot}</span>
                  <span className="font-mono tabular-nums text-foreground">+{formatDuration(Math.max(0, m.us - row.startUs))}</span>
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}

      {row.kind !== 'dropped' && (
        <div className="rounded-md border border-border bg-card p-3 text-fs-base">
          <RelatedSpans causalKey={row.key} current={null} originUs={row.startUs} />
        </div>
      )}
    </div>
  );
}

function CardHeader({ icon, title, suffix }: { icon: React.ReactNode; title: string; suffix: string }): React.JSX.Element {
  return (
    <div className="mb-2 flex items-center gap-2 border-b border-border pb-2">
      {icon}
      <h3 className="min-w-0 truncate text-fs-lg font-semibold text-foreground" title={title}>{title}</h3>
      <span className="ml-auto shrink-0 font-mono text-fs-sm text-muted-foreground">{suffix}</span>
    </div>
  );
}

function RootSummaryRows({ row }: { row: RequestRow }): React.JSX.Element {
  return (
    <>
      <Row label="Total">{formatDuration(row.totalUs)}</Row>
      <Row label="Engine">{`${formatDuration(row.engineUs)} in ${row.spanCount.toLocaleString()} span${row.spanCount === 1 ? '' : 's'}`}</Row>
      {row.ringWaitUs !== undefined && <Row label="Ring wait">{formatDuration(row.ringWaitUs)}</Row>}
      {row.tickWaitUs !== undefined && <Row label="Tick wait">{formatDuration(row.tickWaitUs)}</Row>}
      {row.sendWaitUs !== undefined && <Row label="Send wait">{formatDuration(row.sendWaitUs)}</Row>}
      {row.session !== undefined && <Row label="Session">{row.session}</Row>}
    </>
  );
}

function PinButton({ causalKey }: { causalKey: string }): React.JSX.Element {
  const pinnedKey = useProfilerRequestsStore((s) => s.selectedKey);
  const togglePin = (): void => {
    const requests = useProfilerRequestsStore.getState();
    requests.select(requests.selectedKey === causalKey ? null : causalKey);
  };
  return (
    <div className="mt-2 flex items-center gap-2">
      <button type="button" data-testid="correlation-pin"
        className="rounded border border-border bg-background px-2 py-0.5 text-fs-sm hover:bg-accent"
        onClick={togglePin}>
        {pinnedKey === causalKey ? 'Unpin its flow' : 'Pin its flow in the time area'}
      </button>
    </div>
  );
}

/** The spans sharing a cause, as links; <paramref name="current"/>, the span the pane is showing, is marked. */
function RelatedSpans({ causalKey, current, originUs }: { causalKey: string; current: SpanData | null; originUs: number }): React.JSX.Element {
  const related = useProfilerRequestsStore((s) => (s.related?.key === causalKey ? s.related.spans : null));
  const shown = related === null ? [] : related.slice(0, MAX_LINKS);
  return (
    <div className={cn(current !== null && 'mt-3 border-t border-border pt-2')}>
      <div className="mb-1 text-fs-sm text-muted-foreground">
        {related === null ? 'Caused spans…' : `${current !== null ? 'Related' : 'Caused'} spans (${related.length.toLocaleString()})`}
      </div>
      <ul className="flex flex-col">
        {shown.map((s) => (
          <li key={`${s.threadSlot}:${s.spanId}:${s.startUs}`}>
            <button
              type="button"
              data-testid="correlation-link"
              onClick={() => goToSpan(s)}
              className={cn(
                'grid w-full grid-cols-[1fr,auto,auto] gap-x-3 rounded px-1 py-0.5 text-left text-fs-sm hover:bg-accent/60',
                s === current && 'bg-accent/60',
              )}
              title={`Select it and bring it into view (slot ${s.threadSlot})`}
            >
              <span className="min-w-0 truncate font-mono text-foreground">{s.name}</span>
              <span className="font-mono tabular-nums text-muted-foreground">+{formatDuration(Math.max(0, s.startUs - originUs))}</span>
              <span className="font-mono tabular-nums text-foreground">{formatDuration(s.durationUs)}</span>
            </button>
          </li>
        ))}
      </ul>
      {related !== null && related.length === 0 && <div className="text-fs-sm text-muted-foreground">No engine span carried its id.</div>}
      {related !== null && related.length > shown.length && (
        <div className="mt-1 text-fs-xs text-muted-foreground">
          {`+${(related.length - shown.length).toLocaleString()} more — pin its flow to see them all in the time area`}
        </div>
      )}
    </div>
  );
}

function Row({ label, children }: { label: string; children: React.ReactNode }): React.JSX.Element {
  return (
    <>
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="font-mono tabular-nums text-foreground">{children}</dd>
    </>
  );
}

/** Selects the span and brings it into view; the time area does both, so the view left behind is recorded first. */
function goToSpan(target: SpanData): void {
  useProfilerRequestsStore.getState().navigate({ kind: 'span', span: target });
}

function selectRoot(root: RequestRow): void {
  const target: NavTarget = {
    startUs: root.startUs, endUs: root.endUs, trackId: root.kind === 'http' ? ROOT_HTTP_TRACK_ID : ROOT_COMMANDS_TRACK_ID, rowOffset: 0,
    rowHeight: 14,
  };
  useProfilerRequestsStore.getState().navigate({ kind: 'target', target, select: { kind: 'request', row: root } });
}

function goToStep(m: CausalMarker): void {
  useProfilerRequestsStore.getState().navigate({
    kind: 'target', target: { startUs: m.us, endUs: m.us, trackId: `slot-${m.slot}`, rowOffset: 0, rowHeight: 18 },
  });
}

/**
 * The root row this span served. HTTP requests continuing one upstream trace share a key, so among several the one whose time range holds the span
 * is the likeliest; failing that, the latest that started before it.
 */
function pickRoot(rows: readonly RequestRow[], key: string, span: SpanData): RequestRow | undefined {
  let best: RequestRow | undefined;
  for (const row of rows) {
    if (row.key !== key) continue;
    if (row.startUs <= span.startUs && span.startUs <= row.endUs) return row;
    if (row.startUs <= span.startUs && (best === undefined || row.startUs > best.startUs)) best = row;
  }
  return best ?? rows.find((r) => r.key === key);
}
