import { useEffect, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { Button } from '@/components/ui/button';
import { useSessionStore } from '@/stores/useSessionStore';
import { useProfilerSessionStore, type ConnectionStatus } from '@/stores/useProfilerSessionStore';
import { postApiSessionsSessionIdProfilerDisconnect } from '@/api/generated/profiler/profiler';
import { useAnomalyDetection } from '@/hooks/profiler/useAnomalyDetection';
import { captureAndAnalyse } from '@/shell/commands/captureAndAnalyse';
import EngineHealthScalars from './EngineHealthScalars';
import AnomalyLog from './AnomalyLog';

/**
 * Engine Live Health — the consolidated live-attach surface (#377 Stage 4). The panel surfaces the
 * connection-state header + Disconnect (P1), the engine-runtime scalar tiles (`EngineHealthScalars`,
 * P2), the anomaly log + Jump affordance (`AnomalyLog`, P3, GAP-21 jump), and Capture & Analyse
 * (P4, GAP-22). The five gauge groups (Memory · Page Cache · Transient · WAL · Tx+UoW) are NOT
 * surfaced here — they live in the Profiler timeline (`TimeArea`), which is the canonical place to
 * inspect them. Removed post-P5 (2026-05-26) to keep this panel focused on at-a-glance health.
 *
 * Reads `connectionStatus` / `latestTickNumber` from `useProfilerSessionStore` (populated by the
 * SSE live-stream drain) and the endpoint from `useSessionStore.filePath` (which carries `host:port`
 * for attach sessions). Uptime is panel-local (mount time + 1 Hz tick) — we don't have a server-side
 * session-start timestamp to thread through, and "uptime since the panel opened" is the more
 * frequently-useful number anyway (the live drain may have been running before the user opened it).
 */
export default function EngineLiveHealthPanel(_props: IDockviewPanelProps) {
  const sessionKind = useSessionStore((s) => s.kind);
  const sessionId = useSessionStore((s) => s.sessionId);
  const endpoint = useSessionStore((s) => s.filePath);
  const connectionStatus = useProfilerSessionStore((s) => s.connectionStatus);
  const latestTickNumber = useProfilerSessionStore((s) => s.latestTickNumber);

  // Uptime: capture mount time + tick a 1 Hz interval. Only runs while the panel is mounted on an
  // attach session — the early-return below stops the interval from firing in the cold-state branch.
  const [mountedAt] = useState(() => Date.now());
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (sessionKind !== 'attach') {
      return;
    }
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [sessionKind]);

  const [isDisconnecting, setIsDisconnecting] = useState(false);
  const [disconnectError, setDisconnectError] = useState<string | null>(null);
  const [isCapturing, setIsCapturing] = useState(false);
  const [captureError, setCaptureError] = useState<string | null>(null);

  // P3 — anomaly detection. Side-effect hook; consumers read from useProfilerSessionStore.anomalies.
  // Always-on while the session is attached so the log fills incrementally as ticks arrive.
  useAnomalyDetection(sessionKind === 'attach' ? sessionId : null, true);

  if (sessionKind !== 'attach') {
    return (
      <ColdState>
        Engine Health is available in <b>Attach</b> sessions only. Open <i>Connect → Attach</i> and point it at a running engine to see live tick rate, gauges, and anomalies here.
      </ColdState>
    );
  }

  const isConnected = connectionStatus === 'connected';
  const uptimeLabel = formatUptime(now - mountedAt);

  async function onDisconnect(): Promise<void> {
    if (!sessionId || isDisconnecting) {
      return;
    }
    setIsDisconnecting(true);
    setDisconnectError(null);
    try {
      await postApiSessionsSessionIdProfilerDisconnect(sessionId);
    } catch (e) {
      setDisconnectError((e as Error)?.message ?? 'Disconnect failed.');
    } finally {
      setIsDisconnecting(false);
    }
  }

  /**
   * P4 (#377) — Capture & Analyse — one-gesture freeze → save → reopen-as-Trace. Delegates to the shared
   * orchestration command so the ReconnectBanner button can call the same logic. The captureAndAnalyse
   * command mutates `useSessionStore` on success, so this very panel will unmount as the session kind
   * transitions away from `'attach'`.
   */
  async function onCaptureAndAnalyse(): Promise<void> {
    if (!sessionId || isCapturing) {
      return;
    }
    setIsCapturing(true);
    setCaptureError(null);
    try {
      await captureAndAnalyse(sessionId);
      // On success the session kind transitions; the panel unmounts and there's nothing to clean up here.
    } catch (e) {
      setCaptureError((e as Error)?.message ?? 'Capture & Analyse failed.');
      setIsCapturing(false);
    }
  }

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background" data-testid="engine-live-health">
      {/* Status header — dot + label + endpoint + uptime + tick count. */}
      <div className="flex items-center gap-3 border-b border-border px-3 py-2 text-fs-sm" data-testid="engine-live-health-header">
        <StatusDot status={connectionStatus} />
        <span className="font-mono text-foreground" data-testid="engine-live-health-status">{statusLabel(connectionStatus)}</span>
        {endpoint && (
          <span className="font-mono text-muted-foreground" data-testid="engine-live-health-endpoint" title={endpoint}>
            {endpoint}
          </span>
        )}
        <span className="ml-auto font-mono text-muted-foreground" data-testid="engine-live-health-uptime">
          up {uptimeLabel}
        </span>
        <span className="font-mono text-muted-foreground" data-testid="engine-live-health-tick">
          tick {latestTickNumber.toLocaleString()}
        </span>
      </div>

      {/* Engine-runtime scalar tiles — DOM, refreshes with the SSE batch flush. */}
      <EngineHealthScalars />

      {/* Anomaly log — P3 (GAP-21 jump). Rows + Jump button + Jump to last; empty-state when none. */}
      <div className="flex flex-1 flex-col overflow-hidden border-b border-border">
        <AnomalyLog />
      </div>

      {/* Controls — Disconnect (P1) + Capture & Analyse (P4, GAP-22 one-gesture). */}
      <div className="flex items-center gap-2 px-3 py-2" data-testid="engine-live-health-controls">
        <Button
          size="sm"
          variant="secondary"
          onClick={onDisconnect}
          disabled={!isConnected || isDisconnecting || !sessionId}
          data-testid="engine-live-health-disconnect"
          title={isConnected ? 'Freeze the live stream — the session stays open for analysis' : 'Already disconnected — the session is frozen'}
        >
          {isDisconnecting ? 'Disconnecting…' : 'Disconnect'}
        </Button>
        <Button
          size="sm"
          variant="default"
          onClick={onCaptureAndAnalyse}
          disabled={isCapturing || !sessionId}
          data-testid="engine-live-health-capture"
          title="Save the live session as a replay file and reopen it as a Trace for full inspection — one gesture"
        >
          {isCapturing ? 'Capturing…' : 'Capture & Analyse'}
        </Button>
        {disconnectError && (
          <span className="text-fs-sm text-destructive" data-testid="engine-live-health-disconnect-error">
            {disconnectError}
          </span>
        )}
        {captureError && (
          <span className="text-fs-sm text-destructive" data-testid="engine-live-health-capture-error">
            {captureError}
          </span>
        )}
      </div>
    </div>
  );
}

// ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

const STATUS_LABEL: Record<ConnectionStatus | 'unknown', string> = {
  connecting: 'Connecting…',
  connected: 'Connected',
  reconnecting: 'Reconnecting…',
  disconnected: 'Disconnected',
  unknown: 'Unknown',
};

function statusLabel(s: ConnectionStatus | null): string {
  return STATUS_LABEL[s ?? 'unknown'];
}

const STATUS_DOT_TINT: Record<ConnectionStatus | 'unknown', string> = {
  connecting: 'bg-amber-500',
  connected: 'bg-emerald-500',
  reconnecting: 'bg-amber-500',
  disconnected: 'bg-slate-500',
  unknown: 'bg-slate-500',
};

function StatusDot({ status }: { status: ConnectionStatus | null }) {
  const tint = STATUS_DOT_TINT[status ?? 'unknown'];
  return (
    <span
      aria-hidden
      className={`inline-block h-2 w-2 shrink-0 rounded-full ${tint}`}
      data-testid="engine-live-health-status-dot"
      data-status={status ?? 'unknown'}
    />
  );
}

function ColdState({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex h-full w-full items-center justify-center bg-background p-4 text-center" data-testid="engine-live-health-cold">
      <div className="max-w-md text-fs-base text-muted-foreground">{children}</div>
    </div>
  );
}

/** Format an uptime in ms as `0s` / `42s` / `3m07s` / `1h02m`. Width-stable enough for a live counter. */
function formatUptime(ms: number): string {
  if (ms < 1000) {
    return '0s';
  }
  const s = Math.floor(ms / 1000);
  if (s < 60) {
    return `${s}s`;
  }
  const m = Math.floor(s / 60);
  const rs = s % 60;
  if (m < 60) {
    return `${m}m${rs.toString().padStart(2, '0')}s`;
  }
  const h = Math.floor(m / 60);
  const rm = m % 60;
  return `${h}h${rm.toString().padStart(2, '0')}m`;
}
