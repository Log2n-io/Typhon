import { useMemo } from 'react';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useLiveGaugeData } from '@/hooks/profiler/useLiveGaugeData';

/**
 * Engine-runtime scalar tiles (#377 Stage 4 Phase 2). DOM tiles — NOT canvas — since these are
 * single-number readouts with sub-second refresh and need real text for accessibility (screen
 * readers, copy-to-clipboard). These cover the engine-runtime side of health: tick rate, jitter
 * (max + p95), GC pressure, total ticks. The engine-data gauges (Memory · Page Cache · Transient ·
 * WAL · Tx+UoW) live in the Profiler timeline (`TimeArea`) — removed from this panel post-P5 to
 * keep the surface focused on at-a-glance health.
 *
 * **Scope honesty** (mirrors the design's "additive API only" — `claude/design/Apps/Workbench/
 * stages/stage-4-observe.md` §7 risks): the design's full list also called for overload-multiplier
 * and queue-depth scalars. The engine doesn't surface either today — no `targetTickBudgetUs` field
 * on `GlobalMetricsDto`, no queue-depth gauge. Adding fake "0" tiles would be a broken affordance
 * (suite E / PC-6). They land when the engine API gains the fields — explicitly out of P2.
 *
 * All derivations are over the same 60-s window the Profiler timeline uses (via `useLiveGaugeData`),
 * so the tiles + the timeline gauges agree on what "now" means.
 */
export interface EngineHealthScalarsProps {
  /** Override for tests — defaults to `useLiveGaugeData(sessionId)`. */
  windowMs?: number;
}

interface ScalarTile {
  testId: string;
  label: string;
  /** Headline number (preformatted — formatter chosen by the derivation). */
  value: string;
  /** Optional sub-text (unit, context) — kept short to fit two-column layout. */
  sub?: string;
  /** Severity tint for the value — `'normal'` = foreground, `'warn'` = amber, `'bad'` = destructive. */
  tone: 'normal' | 'warn' | 'bad';
}

export default function EngineHealthScalars({ windowMs = 60_000 }: EngineHealthScalarsProps) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const sessionKind = useSessionStore((s) => s.kind);
  const isLive = sessionKind === 'attach';
  const globalMetrics = useProfilerSessionStore((s) => s.metadata?.globalMetrics ?? null);

  const { windowedTicks, gaugeData, hasData, windowStartUs, windowEndUs } = useLiveGaugeData(
    isLive ? sessionId : null,
    windowMs,
  );

  const tiles = useMemo<ScalarTile[]>(() => {
    if (!hasData || windowedTicks.length === 0) {
      return [
        { testId: 'tile-tick-rate', label: 'Tick rate', value: '—', sub: 'Hz', tone: 'normal' },
        { testId: 'tile-p95-duration', label: 'p95 tick', value: '—', sub: 'µs', tone: 'normal' },
        { testId: 'tile-max-duration', label: 'Max tick', value: '—', sub: 'µs', tone: 'normal' },
        { testId: 'tile-gc-pauses', label: 'GC pauses', value: '—', sub: 'last 60 s', tone: 'normal' },
        { testId: 'tile-total-ticks', label: 'Total ticks', value: '—', sub: 'since session start', tone: 'normal' },
      ];
    }

    // Tick rate — ticks per second over the actual window covered (uses tick endUs span, not the
    // nominal 60 s, so the rate is honest when the session is younger than the window).
    const windowSpanUs = Math.max(1, windowEndUs - windowStartUs);
    const tickRate = (windowedTicks.length * 1_000_000) / windowSpanUs;

    // p95 + max tick duration — sort copy, no in-place mutation of the cached array.
    const durations = windowedTicks.map((t) => t.durationUs).sort((a, b) => a - b);
    const maxUs = durations[durations.length - 1];
    const p95Us = durations[Math.floor(durations.length * 0.95)] ?? maxUs;

    // GC pauses inside the window — count + total pause time. gcSuspensions are already aggregated
    // by `aggregateGaugeData` for the windowed subset.
    const gcCount = gaugeData.gcSuspensions.length;
    const gcTotalUs = gaugeData.gcSuspensions.reduce((acc, s) => acc + s.durationUs, 0);

    // Session-total tick count — comes from the persisted `globalMetrics` snapshot (latest from SSE).
    const totalTicks = globalMetrics?.totalTicks !== undefined ? Number(globalMetrics.totalTicks) : null;

    // Severity thresholds — heuristic defaults (see stage-4 doc §7 risks: P95 baselines, document in tooltip).
    const p95Tone: ScalarTile['tone'] = p95Us > 5_000 ? 'bad' : p95Us > 1_000 ? 'warn' : 'normal';
    const gcTone: ScalarTile['tone'] = gcTotalUs > 50_000 ? 'bad' : gcCount >= 3 ? 'warn' : 'normal';

    return [
      {
        testId: 'tile-tick-rate',
        label: 'Tick rate',
        value: formatHz(tickRate),
        sub: 'Hz · last 60 s',
        tone: 'normal',
      },
      {
        testId: 'tile-p95-duration',
        label: 'p95 tick',
        value: formatMicros(p95Us),
        sub: '95th percentile · 60 s',
        tone: p95Tone,
      },
      {
        testId: 'tile-max-duration',
        label: 'Max tick',
        value: formatMicros(maxUs),
        sub: 'peak · 60 s',
        tone: maxUs > 5_000 ? 'bad' : maxUs > 1_000 ? 'warn' : 'normal',
      },
      {
        testId: 'tile-gc-pauses',
        label: 'GC pauses',
        value: gcCount === 0 ? '0' : `${gcCount} · ${formatMillis(gcTotalUs)}`,
        sub: gcCount === 0 ? 'none last 60 s' : 'count · total · 60 s',
        tone: gcTone,
      },
      {
        testId: 'tile-total-ticks',
        label: 'Total ticks',
        value: totalTicks !== null ? totalTicks.toLocaleString() : '—',
        sub: 'since session start',
        tone: 'normal',
      },
    ];
  }, [hasData, windowedTicks, windowStartUs, windowEndUs, gaugeData.gcSuspensions, globalMetrics]);

  return (
    <div
      className="grid grid-cols-5 gap-2 border-b border-border px-3 py-2"
      data-testid="engine-live-health-scalars"
    >
      {tiles.map((tile) => (
        <Tile key={tile.testId} {...tile} />
      ))}
    </div>
  );
}

function Tile({ testId, label, value, sub, tone }: ScalarTile) {
  const toneClass =
    tone === 'bad' ? 'text-destructive' : tone === 'warn' ? 'text-amber-500' : 'text-foreground';
  return (
    <div
      className="flex flex-col rounded border border-border/50 bg-background/40 px-2 py-1"
      data-testid={testId}
      data-tone={tone}
    >
      <div className="text-fs-xs uppercase tracking-wide text-muted-foreground">{label}</div>
      <div className={`text-fs-base font-mono font-semibold ${toneClass}`} data-testid={`${testId}-value`}>
        {value}
      </div>
      {sub && <div className="text-fs-xs text-muted-foreground">{sub}</div>}
    </div>
  );
}

// ── Formatters ──────────────────────────────────────────────────────────────────────────────────

function formatHz(hz: number): string {
  if (!Number.isFinite(hz) || hz <= 0) return '—';
  if (hz >= 100) return hz.toFixed(0);
  if (hz >= 10) return hz.toFixed(1);
  return hz.toFixed(2);
}

function formatMicros(us: number): string {
  if (!Number.isFinite(us) || us < 0) return '—';
  if (us >= 1_000_000) return `${(us / 1_000_000).toFixed(2)} s`;
  if (us >= 1_000) return `${(us / 1_000).toFixed(2)} ms`;
  return `${us.toFixed(0)} µs`;
}

function formatMillis(us: number): string {
  if (!Number.isFinite(us) || us < 0) return '—';
  if (us >= 1_000_000) return `${(us / 1_000_000).toFixed(2)} s`;
  return `${(us / 1_000).toFixed(1)} ms`;
}
