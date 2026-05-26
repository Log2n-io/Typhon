import { useState } from 'react';
import { AlertTriangle, PlugZap } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useSessionStore } from '@/stores/useSessionStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { postApiSessionsAttach } from '@/api/generated/sessions/sessions';
import { captureAndAnalyse } from '@/shell/commands/captureAndAnalyse';
import { logError, logInfo } from '@/stores/useLogStore';
import { extractDetail } from '@/shell/dialogs/connectErrors';

/**
 * Reconnect / shutdown banner (#377 Stage 4 Phase 4, GAP-22). Surfaces an actionable affordance when the
 * live attach SSE stream drops — distinguishes the two server-emitted shutdown reasons:
 *
 *   - **`init_mismatch`** — engine restarted with incompatible schema (the reconnect's Init signature differs
 *     from the original). Recovery: start over with a fresh Attach session to the same endpoint.
 *   - **generic disconnect** — transient TCP drop (silent retry in the runtime hasn't recovered yet, or the
 *     user-initiated Disconnect just landed). Recovery: re-Attach to the stored endpoint, OR Capture & Analyse
 *     to preserve what we have as a Trace session.
 *
 * Visibility: `sessionKind === 'attach' && connectionStatus === 'disconnected'` AND user hasn't dismissed.
 * Mounted alongside `IncompatibleBanner` / `MigrationRequiredBanner` in `DockHost.tsx` so the banner is global
 * (a session-level signal belongs at the session-level frame, not inside any one panel).
 *
 * Manual dismissal: the close (×) button hides until the next connection-state transition (the dismissed
 * flag resets when sessionId or disconnectReason changes — see the deps below).
 */
export default function ReconnectBanner() {
  const sessionKind = useSessionStore((s) => s.kind);
  const sessionId = useSessionStore((s) => s.sessionId);
  const endpoint = useSessionStore((s) => s.filePath);
  const setSession = useSessionStore((s) => s.setSession);
  const connectionStatus = useProfilerSessionStore((s) => s.connectionStatus);
  const disconnectReason = useProfilerSessionStore((s) => s.disconnectReason);
  // Local dismissal state — keyed implicitly on the (sessionId, disconnectReason) pair below: a new
  // disconnect event re-shows the banner. We don't persist across reload — banner visibility is purely
  // about the current session lifetime.
  const [dismissed, setDismissed] = useState<string | null>(null);
  const [isReconnecting, setIsReconnecting] = useState(false);
  const [isCapturing, setIsCapturing] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  // Visibility gate (PC-2 / suite D — no banner without a meaningful signal).
  if (sessionKind !== 'attach') return null;
  if (connectionStatus !== 'disconnected') return null;

  // Key the dismissal on a stable identity (`sessionId|reason`) so a *new* disconnect reason on the same
  // session re-shows the banner; only the user's explicit dismiss for THIS exact event hides it.
  const dismissKey = `${sessionId ?? ''}|${disconnectReason ?? 'transient'}`;
  if (dismissed === dismissKey) return null;

  const isInitMismatch = disconnectReason === 'init_mismatch';

  async function onReconnect(): Promise<void> {
    if (!endpoint || isReconnecting) return;
    setIsReconnecting(true);
    setActionError(null);
    logInfo('Reconnect banner — re-attaching to endpoint', { endpoint });
    try {
      const response = await postApiSessionsAttach({ endpointAddress: endpoint });
      setSession(response.data);
      // The server's single-per-endpoint invariant dropped the old session; the new one is now active.
      // connectionStatus will transition back to 'connected' as the SSE stream lands metadata + heartbeat.
    } catch (e) {
      const detail = extractDetail(e) || (e as Error)?.message || String(e);
      setActionError(detail);
      logError('Reconnect banner — re-attach failed', { endpoint, error: detail });
    } finally {
      setIsReconnecting(false);
    }
  }

  async function onCapture(): Promise<void> {
    if (!sessionId || isCapturing) return;
    setIsCapturing(true);
    setActionError(null);
    try {
      await captureAndAnalyse(sessionId);
      // Session transitions to 'trace'; banner self-hides via the visibility gate above.
    } catch (e) {
      setActionError((e as Error)?.message ?? 'Capture & Analyse failed.');
      setIsCapturing(false);
    }
  }

  const Icon = isInitMismatch ? AlertTriangle : PlugZap;
  const title = isInitMismatch
    ? 'Engine restarted with incompatible schema'
    : 'Engine connection dropped';
  const body = isInitMismatch
    ? 'The engine reconnected with a different schema signature, so the previous session can\'t be resumed. Reconnect to start a fresh Attach session, or Capture what we have to inspect the frozen buffer as a Trace.'
    : 'The live SSE stream is no longer delivering events. Reconnect to the same endpoint, or Capture what we have to keep the frozen buffer for analysis.';

  return (
    <div
      role="alert"
      className="flex items-start gap-3 border-b border-destructive/50 bg-destructive/10 px-4 py-2 text-fs-lg text-destructive"
      data-testid="engine-reconnect-banner"
      data-reason={disconnectReason ?? 'transient'}
    >
      <Icon className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
      <div className="min-w-0 flex-1">
        <p className="font-semibold" data-testid="engine-reconnect-banner-title">{title}</p>
        <p className="mt-0.5 text-fs-sm opacity-90" data-testid="engine-reconnect-banner-body">{body}</p>
        {endpoint && (
          <p className="mt-0.5 font-mono text-fs-sm opacity-70" data-testid="engine-reconnect-banner-endpoint">{endpoint}</p>
        )}
        {actionError && (
          <p className="mt-1 text-fs-sm text-destructive" data-testid="engine-reconnect-banner-error">{actionError}</p>
        )}
      </div>
      <div className="flex shrink-0 gap-2">
        <Button
          variant="outline"
          size="sm"
          className="h-6 text-fs-sm"
          onClick={onReconnect}
          disabled={!endpoint || isReconnecting}
          data-testid="engine-reconnect-banner-reconnect"
          title={endpoint ? `Re-Attach to ${endpoint}` : 'No endpoint to re-attach to'}
        >
          {isReconnecting ? 'Reconnecting…' : 'Reconnect'}
        </Button>
        <Button
          variant="outline"
          size="sm"
          className="h-6 text-fs-sm"
          onClick={onCapture}
          disabled={!sessionId || isCapturing}
          data-testid="engine-reconnect-banner-capture"
          title="Save the frozen buffer as a replay and open it as a Trace"
        >
          {isCapturing ? 'Capturing…' : 'Capture what we have'}
        </Button>
        <Button
          variant="ghost"
          size="sm"
          className="h-6 text-fs-sm"
          onClick={() => setDismissed(dismissKey)}
          data-testid="engine-reconnect-banner-dismiss"
          title="Hide this banner until the next disconnect event"
          aria-label="Dismiss banner"
        >
          ×
        </Button>
      </div>
    </div>
  );
}
