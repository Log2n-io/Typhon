import { useEffect, useMemo, useState } from 'react';
import { Input } from '@/components/ui/input';
import { cn } from '@/lib/utils';
import {
  filterRequests, groupRequests, isRequestFilterActive, NO_REQUEST_FILTER, type RequestGroup, type RequestRow,
} from '@/libs/profiler/correlation/requests';
import { useProfilerRequestsStore } from '@/stores/useProfilerRequestsStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionCapability } from '@/stores/useSessionStore';

/**
 * The roots of the viewport (#1517): each HTTP request and client command, what the engine did for it and where its time went — waiting in the
 * session's ring, waiting for a tick, in systems, waiting to be sent. Selecting a root highlights its spans in the time area and draws its flow.
 * Reads `useProfilerRequestsStore`, produced once from `ProfilerPanel` over the loaded ticks. Its filter also narrows the time area's root lanes:
 * clicking an endpoint filters on it, clicking a session filters on that client.
 */
type Mode = 'requests' | 'grouped';
const ROW_LIMIT = 500;

export default function RequestsPanel(): React.JSX.Element {
  const isProfilerSession = useSessionCapability('profiler');
  const allRows = useProfilerRequestsStore((s) => s.rows);
  const filter = useProfilerRequestsStore((s) => s.filter);
  const rows = useMemo(() => filterRequests(allRows, filter), [allRows, filter]);
  const [mode, setMode] = useState<Mode>('requests');

  if (!isProfilerSession) {
    return <Empty text="Open a profiler trace or attach a session to see requests." />;
  }

  if (allRows.length === 0 && !isRequestFilterActive(filter)) {
    return (
      <Empty text="No requests in the current viewport. Requests are recorded when the profiler's correlation switch is on (Typhon:Profiler:Correlation) and the host calls AddTyphonRequestCorrelation." />
    );
  }

  return (
    <div className="flex h-full flex-col overflow-hidden bg-background">
      <div className="wb-pane-header flex items-center gap-3 border-b border-border px-3 py-1.5 text-fs-sm">
        <ModeButton active={mode === 'requests'} onClick={() => setMode('requests')}>Requests</ModeButton>
        <ModeButton active={mode === 'grouped'} onClick={() => setMode('grouped')}>By endpoint</ModeButton>
        <span className="ml-auto text-fs-xs text-muted-foreground">
          {isRequestFilterActive(filter) ? `${rows.length.toLocaleString()} of ${allRows.length.toLocaleString()}` : rows.length.toLocaleString()} in view
          {' · click a row to trace it'}
        </span>
      </div>
      <FilterBar />
      <div className="min-h-0 flex-1 overflow-auto">{mode === 'requests' ? <RequestsTable rows={rows} /> : <GroupsTable rows={rows} />}</div>
    </div>
  );
}

/** Label text, one client session and a minimum total, applied as typed. A number field that does not parse leaves that part off. */
function FilterBar(): React.JSX.Element {
  const filter = useProfilerRequestsStore((s) => s.filter);
  const setFilter = useProfilerRequestsStore((s) => s.setFilter);
  const [sessionDraft, setSessionDraft] = useState('');
  const [minMsDraft, setMinMsDraft] = useState('');
  // A filter set from elsewhere (a click on a session or an endpoint, a cleared session) shows in the fields.
  useEffect(() => setSessionDraft(filter.session === null ? '' : String(filter.session)), [filter.session]);
  useEffect(() => {
    setMinMsDraft((d) => (filter.minTotalUs === null ? '' : Number(d) * 1000 === filter.minTotalUs ? d : String(filter.minTotalUs / 1000)));
  }, [filter.minTotalUs]);

  const parse = (text: string): number | null => {
    const n = Number(text.trim());
    return text.trim() === '' || !Number.isFinite(n) ? null : n;
  };

  return (
    <div className="flex items-center gap-2 border-b border-border px-3 py-1 text-fs-xs text-muted-foreground">
      <Input
        aria-label="Filter by label"
        placeholder="Filter: endpoint or command"
        className="h-6 max-w-64 text-fs-xs"
        value={filter.text}
        onChange={(e) => setFilter({ text: e.target.value })}
      />
      <span>session</span>
      <Input
        aria-label="Filter by session"
        className="h-6 w-16 text-fs-xs"
        value={sessionDraft}
        onChange={(e) => {
          setSessionDraft(e.target.value);
          const n = parse(e.target.value);
          setFilter({ session: n === null ? null : Math.trunc(n) });
        }}
      />
      <span>total ≥</span>
      <Input
        aria-label="Minimum total in milliseconds"
        className="h-6 w-16 text-fs-xs"
        value={minMsDraft}
        onChange={(e) => {
          setMinMsDraft(e.target.value);
          const n = parse(e.target.value);
          setFilter({ minTotalUs: n === null ? null : n * 1000 });
        }}
      />
      <span>ms</span>
      {isRequestFilterActive(filter) && (
        <button type="button" className="rounded px-1.5 hover:text-foreground" onClick={() => setFilter(NO_REQUEST_FILTER)}>
          Clear
        </button>
      )}
    </div>
  );
}

function RequestsTable({ rows }: { rows: RequestRow[] }): React.JSX.Element {
  const selectedKey = useProfilerRequestsStore((s) => s.selectedKey);
  const select = useProfilerRequestsStore((s) => s.select);
  const setFilter = useProfilerRequestsStore((s) => s.setFilter);
  const commitViewRange = useProfilerViewStore((s) => s.commitViewRange);
  const sorted = useMemo(() => rows.slice().sort((a, b) => b.totalUs - a.totalUs).slice(0, ROW_LIMIT), [rows]);

  const onClick = (row: RequestRow): void => {
    // The row shows in the Details pane either way; a second click on the pinned row unpins its flow.
    useSelectionStore.getState().select('span', { kind: 'request', row });
    if (row.key === selectedKey) {
      select(null);
      return;
    }
    select(row.key);
    const pad = Math.max((row.endUs - row.startUs) * 0.1, 50);
    commitViewRange({ startUs: row.startUs - pad, endUs: row.endUs + pad });
  };

  return (
    <table className="w-full text-fs-sm">
      <thead className="sticky top-0 z-10 bg-background">
        <tr className="text-left text-muted-foreground">
          <Th>Root</Th>
          <Th right>Session</Th>
          <Th right>Status</Th>
          <Th right>Total ↓</Th>
          <Th right>Engine</Th>
          <Th right>Spans</Th>
          <Th right>Ring wait</Th>
          <Th right>Tick wait</Th>
          <Th right>Send wait</Th>
        </tr>
      </thead>
      <tbody>
        {sorted.map((r) => (
          <tr
            key={r.id}
            data-testid="request-row"
            className={cn('cursor-pointer border-t border-border/50 hover:bg-accent/40', r.key === selectedKey && 'bg-accent/60')}
            onClick={() => onClick(r)}
          >
            <td className={cn('truncate px-3 py-1 font-mono', r.kind === 'dropped' ? 'text-destructive' : 'text-foreground')}>{r.label}</td>
            <td className="px-3 py-1 text-right font-mono tabular-nums text-foreground">
              {r.session !== undefined && (
                <button
                  type="button"
                  title="Show only this client"
                  className="hover:underline"
                  onClick={(e) => {
                    e.stopPropagation();
                    setFilter({ session: r.session ?? null });
                  }}
                >
                  {r.session}
                </button>
              )}
            </td>
            <Num>{r.status ?? ''}</Num>
            <Num>{formatUs(r.totalUs)}</Num>
            <Num>{formatUs(r.engineUs)}</Num>
            <Num>{r.spanCount}</Num>
            <Num>{maybe(r.ringWaitUs)}</Num>
            <Num>{maybe(r.tickWaitUs)}</Num>
            <Num>{maybe(r.sendWaitUs)}</Num>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function GroupsTable({ rows }: { rows: RequestRow[] }): React.JSX.Element {
  const groups = useMemo(() => groupRequests(rows).sort((a, b) => b.p99Us - a.p99Us), [rows]);
  const setFilter = useProfilerRequestsStore((s) => s.setFilter);
  return (
    <table className="w-full text-fs-sm">
      <thead className="sticky top-0 z-10 bg-background">
        <tr className="text-left text-muted-foreground">
          <Th>Endpoint / command</Th>
          <Th right>Count</Th>
          <Th right>P50</Th>
          <Th right>P99 ↓</Th>
          <Th right>Max</Th>
          <Th right>Total</Th>
        </tr>
      </thead>
      <tbody>
        {groups.map((g: RequestGroup) => (
          <tr
            key={g.label}
            data-testid="request-group"
            title="Show only this endpoint"
            className="cursor-pointer border-t border-border/50 hover:bg-accent/40"
            onClick={() => setFilter({ text: g.label })}
          >
            <td className={cn('truncate px-3 py-1 font-mono', g.kind === 'dropped' ? 'text-destructive' : 'text-foreground')}>{g.label}</td>
            <Num>{g.count.toLocaleString()}</Num>
            <Num>{formatUs(g.p50Us)}</Num>
            <Num>{formatUs(g.p99Us)}</Num>
            <Num>{formatUs(g.maxUs)}</Num>
            <Num>{formatUs(g.totalUs)}</Num>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function Empty({ text }: { text: string }): React.JSX.Element {
  return (
    <div className="flex h-full items-center justify-center bg-background p-3">
      <p className="max-w-xl text-center text-fs-sm text-muted-foreground">{text}</p>
    </div>
  );
}

function ModeButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }): React.JSX.Element {
  return (
    <button
      type="button"
      className={cn('rounded px-1.5 hover:text-foreground', active ? 'bg-accent text-foreground' : 'text-muted-foreground')}
      onClick={onClick}
    >
      {children}
    </button>
  );
}

function Th({ children, right = false }: { children: React.ReactNode; right?: boolean }): React.JSX.Element {
  return <th className={cn('px-3 py-2 font-normal', right ? 'text-right' : 'text-left')}>{children}</th>;
}

function Num({ children }: { children: React.ReactNode }): React.JSX.Element {
  return <td className="px-3 py-1 text-right font-mono tabular-nums text-foreground">{children}</td>;
}

function maybe(us: number | undefined): string {
  return us === undefined ? '—' : formatUs(us);
}

/** Coarsest readable unit, as Top Spans formats durations. */
function formatUs(us: number): string {
  const abs = Math.abs(us);
  if (abs === 0) return '0 µs';
  if (abs < 1000) return `${abs.toFixed(1)} µs`;
  if (abs < 1_000_000) return `${(abs / 1000).toFixed(2)} ms`;
  return `${(abs / 1_000_000).toFixed(2)} s`;
}
