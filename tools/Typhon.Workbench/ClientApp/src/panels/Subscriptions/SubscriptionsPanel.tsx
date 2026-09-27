import { useMemo } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useLiveGaugeData } from '@/hooks/profiler/useLiveGaugeData';
import {
  differentiate,
  latestServerSample,
  latestSessionRows,
  poolOccupancy,
  readSessionCoverage,
  sessionRealmLabel,
} from './subscriptionReadings';

/**
 * Push replication, as an operator sees it (#WB-02) — sessions, outbound bytes/s, skips, frame-pool pressure.
 *
 * <b>Why this panel exists.</b> Every number here was already computed once a second by the engine and sent to GAME CLIENTS in the
 * `STATS` wire block; nothing sent them to the person running the server. Until now the Workbench could not see one byte of replication:
 * a grep for the engine's Subscriptions types across `tools/Typhon.Workbench` returned nothing at all.
 *
 * <b>Attach-only, and that is a data constraint.</b> The figures ride trace kinds 68 and 69, which exist only while an engine is running.
 * A `.typhon` file has no replication history to show.
 *
 * <b>Cumulative counters are differentiated, never shown raw.</b> `framesSkipped` counts since each session opened, so on a long-lived
 * server it is a large number that moves slowly — which reads as trouble when the truth is "none recently". The slope is the diagnostic.
 *
 * <b>The session list says how complete it is.</b> The engine caps rows at 64, so at any real scale the table is a sample. The panel
 * states the difference rather than letting a reader assume they are looking at every session.
 */
export default function SubscriptionsPanel(_props: IDockviewPanelProps) {
  const sessionKind = useSessionStore((s) => s.kind);
  const sessionId = useSessionStore((s) => s.sessionId);
  const { windowedTicks } = useLiveGaugeData(sessionKind === 'attach' ? sessionId : null);

  const sample = useMemo(() => latestServerSample(windowedTicks), [windowedTicks]);
  const rows = useMemo(() => latestSessionRows(windowedTicks), [windowedTicks]);
  const coverage = useMemo(() => (sample === null ? null : readSessionCoverage(sample.row, rows)), [sample, rows]);
  // The window's tick period, from the ticks themselves rather than assumed: a demo at 50 Hz and a server at 10 Hz must both
  // differentiate correctly, and the panel has no configuration to read.
  const tickSeconds = useMemo(() => {
    if (windowedTicks.length < 2) {
      return 0;
    }
    const first = windowedTicks[0];
    const last = windowedTicks[windowedTicks.length - 1];
    const spanTicks = last.tickNumber - first.tickNumber;
    return spanTicks > 0 ? (last.startUs - first.startUs) / 1_000_000 / spanTicks : 0;
  }, [windowedTicks]);
  const skips = useMemo(() => differentiate(windowedTicks, (r) => r.framesSkipped, tickSeconds), [windowedTicks, tickSeconds]);
  const budgetSkips = useMemo(
    () => differentiate(windowedTicks, (r) => r.framePoolBudgetSkips, tickSeconds),
    [windowedTicks, tickSeconds],
  );

  if (sessionKind !== 'attach') {
    return (
      <ColdState>
        Subscriptions is available in <b>Attach</b> sessions only. Its figures are emitted by the replication track about once a second
        while an engine runs, so they exist only against a live engine. Open <i>Connect → Attach</i> and point it at one.
      </ColdState>
    );
  }

  if (sample === null || coverage === null) {
    return (
      <ColdState>
        No replication record in this window. The engine emits one about once a second when the{' '}
        <span className="font-mono">Subscriptions:ServerTelemetry</span> telemetry gate is on — it is off by default, like every trace
        subtree. Nothing here means the gate is closed or this engine replicates nothing; it does not mean zero.
      </ColdState>
    );
  }

  const occupancy = poolOccupancy(sample.row);

  return (
    <div className="h-full w-full overflow-auto bg-background" data-testid="subscriptions-panel">
      <div className="flex items-center gap-3 border-b border-border px-3 py-2 text-fs-sm" data-testid="subscriptions-header">
        <span className="text-muted-foreground">Push replication</span>
        {/* The tick is named, never implied: a per-emission figure without the tick it came from is not a reading. */}
        <span className="ml-auto font-mono text-muted-foreground" data-testid="subscriptions-tick">
          tick {sample.tickNumber.toLocaleString()}
        </span>
      </div>

      <div className="grid grid-cols-2 gap-3 p-3 md:grid-cols-4" data-testid="subscriptions-scalars">
        <Scalar label="sessions" value={sample.row.sessions.toLocaleString()} />
        <Scalar label="out" value={`${formatBytes(sample.row.netOutBytesPerSec)}/s`} />
        <Scalar label="track p99" value={`${sample.row.trackP99Ms.toFixed(2)} ms`} />
        {/* Beside the track figure on purpose: a replication track that looks slow is often a disk that is slow. */}
        <Scalar label="durability wait p99" value={`${sample.row.durabilityWaitP99Ms.toFixed(2)} ms`} />
      </div>

      <div className="grid grid-cols-2 gap-3 px-3 pb-3 md:grid-cols-3" data-testid="subscriptions-pressure">
        <Scalar
          label="frames skipped"
          value={skips?.perSecond === null || skips === null ? '—' : `${skips.perSecond.toFixed(1)}/s`}
          note={skips === null ? undefined : `${skips.total.toLocaleString()} since sessions opened`}
        />
        <Scalar
          label="pool budget skips"
          value={budgetSkips?.perSecond === null || budgetSkips === null ? '—' : `${budgetSkips.perSecond.toFixed(1)}/s`}
          note={budgetSkips === null ? undefined : `${budgetSkips.total.toLocaleString()} total — a server-side refusal, not a slow client`}
        />
        <Scalar
          label="frame pool"
          value={occupancy === null ? 'no blocks' : `${(occupancy * 100).toFixed(0)}%`}
          note={`${sample.row.framePoolRented.toLocaleString()} of ${sample.row.framePoolBlocks.toLocaleString()} blocks rented`}
        />
      </div>

      <table className="w-full text-fs-sm" data-testid="subscriptions-sessions">
        <thead className="text-muted-foreground">
          <tr className="border-y border-border">
            <th className="px-3 py-1 text-left font-normal">session</th>
            <th className="px-3 py-1 text-left font-normal">realm</th>
            <th className="px-3 py-1 text-right font-normal">out</th>
            <th className="px-3 py-1 text-right font-normal">skipped</th>
            <th className="px-3 py-1 text-right font-normal">degrade</th>
          </tr>
        </thead>
        <tbody className="font-mono">
          {rows.map((r) => {
            const realm = sessionRealmLabel(r);
            return (
              <tr key={r.sessionId} className="border-b border-border/50">
                <td className="px-3 py-1">{r.sessionId}</td>
                <td className={`px-3 py-1 ${realm === null ? 'text-muted-foreground' : ''}`}>
                  {/* 0xFFFF is "has not been told its realm yet", which is NOT realm 0 and must not render as 65535. */}
                  {realm ?? 'not yet sent'}
                </td>
                <td className="px-3 py-1 text-right">{formatBytes(r.bytesPerSec)}/s</td>
                <td className="px-3 py-1 text-right">{r.framesSkipped.toLocaleString()}</td>
                <td className={`px-3 py-1 text-right ${r.degradeLevel > 0 ? 'text-amber-300' : ''}`}>{r.degradeLevel}</td>
              </tr>
            );
          })}
        </tbody>
      </table>

      <div className="p-3 text-fs-xs text-muted-foreground" data-testid="subscriptions-coverage">
        {coverage.sessionGateClosed ? (
          <>
            <b>{coverage.open}</b> sessions open and no per-session row in this window. The row cap is 64, so it cannot produce none: the{' '}
            <span className="font-mono">Subscriptions:SessionTelemetry</span> gate is off, separately from the server record's. Enable it for
            the per-session view — the figures above are unaffected.
          </>
        ) : coverage.capped ? (
          <>
            Showing <b>{coverage.reported}</b> of <b>{coverage.open}</b> open sessions, busiest first —{' '}
            <b>{coverage.hidden}</b> not shown. The engine caps the per-session rows so the trace cost does not scale with the session
            count; this table is a sample, not the population.
          </>
        ) : (
          <>
            Showing all <b>{coverage.open}</b> open sessions, busiest first.
          </>
        )}
      </div>
    </div>
  );
}

function Scalar({ label, value, note }: { label: string; value: string; note?: string }) {
  return (
    <div className="rounded border border-border px-2 py-1">
      <div className="text-fs-xs text-muted-foreground">{label}</div>
      <div className="font-mono text-fs-lg text-foreground">{value}</div>
      {note !== undefined && <div className="text-fs-xs text-muted-foreground">{note}</div>}
    </div>
  );
}

/** Bytes/s at operator scale: a server pushing megabytes should not be read as a seven-digit number. */
function formatBytes(bytesPerSec: number): string {
  if (bytesPerSec >= 1_000_000) {
    return `${(bytesPerSec / 1_000_000).toFixed(2)} MB`;
  }
  if (bytesPerSec >= 1_000) {
    return `${(bytesPerSec / 1_000).toFixed(1)} kB`;
  }
  return `${Math.round(bytesPerSec)} B`;
}

function ColdState({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex h-full w-full items-center justify-center bg-background p-4 text-center" data-testid="subscriptions-cold">
      <div className="max-w-md text-fs-base text-muted-foreground">{children}</div>
    </div>
  );
}
