import { useMemo } from 'react';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { Button } from '@/components/ui/button';
import { type Anomaly, suggestJumpRange } from './anomalies';

/**
 * Engine Live Health anomaly log (#377 Stage 4 Phase 3, GAP-21 jump). Lists detected anomalies most-
 * recent-first; each row exposes a Jump button that calls `useProfilerViewStore.commitViewRange` with
 * a context window around the anomaly tick (`suggestJumpRange` heuristic). A "Jump to last" button
 * scopes to the latest anomaly without scrolling the list. Empty state ("No anomalies detected (last
 * 60 s)") keeps the surface non-blank for PC-2 / suite D.
 *
 * Why DOM (not canvas): rows are < 50 ms apart on screen, screen readers + keyboard nav need real
 * elements, and the row count is bounded by the rolling window — a virtual list isn't justified.
 */
export default function AnomalyLog() {
  const anomalies = useProfilerSessionStore((s) => s.anomalies);
  const commitViewRange = useProfilerViewStore((s) => s.commitViewRange);

  // Sort descending by tickNumber for display ("most recent first"). Don't mutate the store array —
  // copy via slice() so the store invariants stay intact.
  const sorted = useMemo(() => anomalies.slice().sort((a, b) => b.tickNumber - a.tickNumber), [anomalies]);
  const last = sorted[0];

  function onJump(anomaly: Anomaly): void {
    commitViewRange(suggestJumpRange(anomaly));
  }

  if (sorted.length === 0) {
    return (
      <div className="flex-1 overflow-auto p-3" data-testid="engine-live-health-anomalies">
        <div className="flex items-center justify-between">
          <div className="text-fs-sm font-medium text-foreground">Anomalies</div>
          <Button size="sm" variant="ghost" disabled data-testid="engine-live-health-jump-last">
            Jump to last
          </Button>
        </div>
        <p className="mt-2 text-fs-sm text-muted-foreground" data-testid="engine-live-health-anomalies-empty">
          No anomalies detected. The detector flags tick-duration outliers (≥ 3× p95 baseline) and GC pauses
          ≥ 16 ms. Heuristics tune later — see <code>stage-4-observe.md</code>.
        </p>
      </div>
    );
  }

  return (
    <div className="flex-1 overflow-auto" data-testid="engine-live-health-anomalies">
      <div className="flex items-center justify-between border-b border-border px-3 py-2">
        <div className="text-fs-sm font-medium text-foreground">
          Anomalies <span className="ml-1 text-fs-xs text-muted-foreground">({sorted.length})</span>
        </div>
        <Button
          size="sm"
          variant="secondary"
          onClick={() => onJump(last)}
          data-testid="engine-live-health-jump-last"
          title="Scope the timeline to the most recent anomaly"
        >
          Jump to last
        </Button>
      </div>
      <ul className="divide-y divide-border/50">
        {sorted.map((a) => (
          <AnomalyRow key={`${a.tickNumber}-${a.kind}`} a={a} onJump={onJump} />
        ))}
      </ul>
    </div>
  );
}

function AnomalyRow({ a, onJump }: { a: Anomaly; onJump: (a: Anomaly) => void }) {
  const tone = severityTone(a.magnitude);
  const toneClass =
    tone === 'bad' ? 'bg-destructive' : tone === 'warn' ? 'bg-amber-500' : 'bg-slate-500';
  const icon = a.kind === 'gc-pause' ? 'GC' : 'TK';
  return (
    <li
      className="flex items-center gap-2 px-3 py-1.5 text-fs-sm hover:bg-muted/40"
      data-testid={`engine-live-health-anomaly-${a.tickNumber}-${a.kind}`}
      data-kind={a.kind}
      data-tone={tone}
    >
      <span aria-hidden className={`inline-block h-2 w-2 shrink-0 rounded-full ${toneClass}`} />
      <span className="font-mono text-fs-xs text-muted-foreground w-7 shrink-0" title={a.kind === 'gc-pause' ? 'GC pause' : 'Tick-duration outlier'}>
        {icon}
      </span>
      <span className="font-mono text-foreground w-24 shrink-0">tick {a.tickNumber.toLocaleString()}</span>
      <span className="font-mono text-muted-foreground w-16 shrink-0" data-testid={`anomaly-magnitude-${a.tickNumber}`}>
        {a.magnitude.toFixed(1)}×
      </span>
      <span className="flex-1 truncate text-muted-foreground" data-testid={`anomaly-details-${a.tickNumber}`}>
        {a.details}
      </span>
      <Button
        size="sm"
        variant="ghost"
        onClick={() => onJump(a)}
        data-testid={`anomaly-jump-${a.tickNumber}`}
        title="Scope the timeline to this anomaly"
      >
        Jump
      </Button>
    </li>
  );
}

function severityTone(magnitude: number): 'normal' | 'warn' | 'bad' {
  if (magnitude >= 5) return 'bad';
  if (magnitude >= 2) return 'warn';
  return 'normal';
}
