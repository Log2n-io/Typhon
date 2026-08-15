import { useEffect, useState } from 'react';
import { PauseCircle } from 'lucide-react';
import { getApiSessionsId, useGetApiSessionsIdState } from '@/api/generated/sessions/sessions';
import { Button } from '@/components/ui/button';
import { applyWorkbenchAuthHeaders } from '@/api/bootstrapToken';
import { logError, logInfo } from '@/stores/useLogStore';
import { useSessionStore } from '@/stores/useSessionStore';

/**
 * How often to ask whether the database's availability has changed.
 *
 * Both transitions this watches for are decided **unilaterally by the server**: it yields when an application publishes
 * a claim, and it resumes when the database frees up. Neither is a reply to anything the client did, so there is no
 * request to piggyback on and nothing to invalidate — polling is the only way to learn about them. Two seconds is well
 * inside the window where a developer would notice their app was blocked or their data had come back.
 */
const STATE_POLL_MS = 2000;

/**
 * Shown while the session has released its database to another process (#621).
 *
 * <p>Self-gating, like `ReconnectBanner`: mount it unconditionally and it decides. Nothing renders unless the session
 * is paused.</p>
 *
 * <p><b>Why this component owns the poll.</b> The session is not backed by a TanStack query — every entry point pushes
 * the DTO into Zustand imperatively, which is the seam that made an attached capture render nothing in #617 — so
 * nothing refreshes it on its own. The component that displays the paused state is therefore also the one responsible
 * for noticing it start and end.</p>
 *
 * <p>It polls the small <c>/state</c> projection rather than the full session, and only re-reads the whole DTO when the
 * flag actually flips. Resuming restores the <c>database</c> capability, the resolved schema paths and the loaded-type
 * count, and panels gate on the capability — so a cheap flag alone would leave every data panel refusing to render
 * against a database that is open again.</p>
 */
export default function PausedBanner() {
  const isPaused = useSessionStore((s) => s.isPaused);
  const reason = useSessionStore((s) => s.pausedReason);
  const sessionId = useSessionStore((s) => s.sessionId);
  const kind = useSessionStore((s) => s.kind);
  const setSession = useSessionStore((s) => s.setSession);
  const profilerEndpoint = useSessionStore((s) => s.profilerEndpoint);
  const isWatchingLive = useSessionStore((s) => s.isWatchingLive);

  const [busy, setBusy] = useState(false);
  const [watchError, setWatchError] = useState<string | null>(null);

  // Poll whenever there is a database session, paused or not: a LIVE session is exactly the one that can be asked to
  // yield, and gating this on `isPaused` meant the Workbench released the database while the UI carried on as though
  // nothing had happened.
  const { data } = useGetApiSessionsIdState(sessionId ?? '', {
    query: {
      enabled: !!sessionId && kind === 'open',
      refetchInterval: STATE_POLL_MS,
      staleTime: 0,
    },
  });

  const serverPaused = data?.data?.isPaused;
  useEffect(() => {
    if (serverPaused === undefined || !sessionId || serverPaused === isPaused) {
      return;
    }
    // The flag flipped in either direction — re-read the full session so capabilities follow.
    void getApiSessionsId(sessionId).then((r) => setSession(r.data));
  }, [serverPaused, isPaused, sessionId, setSession]);

  /**
   * Start or stop watching the holder's live engine.
   *
   * The session is re-read on success rather than patched locally: watching flips the session's CAPABILITIES
   * server-side (P3 reports `profiler` once a live runtime exists), and every profiler panel gates on those. Guessing
   * the new capability set on the client is the #617 seam that made an attached capture render nothing.
   */
  const toggleWatch = async (watch: boolean) => {
    if (!sessionId || busy) {
      return;
    }
    setBusy(true);
    setWatchError(null);
    try {
      const headers = applyWorkbenchAuthHeaders(new Headers({ 'Content-Type': 'application/json' }), sessionId);
      const resp = await fetch(`/api/sessions/${sessionId}/profiler/watch`, {
        method: watch ? 'POST' : 'DELETE',
        headers,
        // Cherry-pick: watching a live app is the case the on-demand capture exists for. Recording every tick of a
        // session you opened to observe someone else's run is exactly the volume problem #805 set out to remove.
        body: watch ? JSON.stringify({ cherryPick: true }) : undefined,
      });
      if (!resp.ok) {
        throw new Error(`HTTP ${resp.status}`);
      }
      const refreshed = await getApiSessionsId(sessionId);
      setSession(refreshed.data);
      logInfo(watch ? 'Watching the live engine' : 'Stopped watching the live engine', { sessionId, profilerEndpoint });
    } catch (err) {
      const detail = (err as Error)?.message ?? String(err);
      setWatchError(watch ? `Could not watch ${profilerEndpoint}: ${detail}` : `Could not stop watching: ${detail}`);
      logError('Watch live failed', { sessionId, profilerEndpoint, error: detail });
    } finally {
      // Always — clearing this only on the error path would leave the button reading "Connecting…" for ever on the
      // success path, since nothing here unmounts the banner (the database is still paused).
      setBusy(false);
    }
  };

  if (!isPaused) {
    return null;
  }

  return (
    <div
      role="status"
      data-testid="paused-banner"
      className="flex items-start gap-3 border-b border-sky-600/40 bg-sky-500/10 px-4 py-2
                 text-fs-lg text-sky-700 dark:text-sky-300"
    >
      <PauseCircle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
      <div className="min-w-0 flex-1">
        <p className="font-semibold">Database released</p>
        <p className="mt-0.5 text-fs-sm opacity-90">
          {reason ??
            'Another process is using this database. The Workbench reopens it automatically when that process exits.'}
        </p>
        {isWatchingLive && (
          <p className="mt-0.5 text-fs-sm opacity-90" data-testid="paused-banner-watching">
            Watching {profilerEndpoint} — arm a recording from the Profiler panel.
          </p>
        )}
        {watchError && (
          <p className="mt-0.5 text-fs-sm text-destructive" data-testid="paused-banner-watch-error">
            {watchError}
          </p>
        )}
      </div>
      {/* The holder advertises a profiler port only when it is a Typhon app with live telemetry enabled, which is
          exactly when watching it is possible — and this banner is the moment the user learns their app took the
          database, so it is where the offer belongs. */}
      {profilerEndpoint && (
        <Button
          size="sm"
          variant={isWatchingLive ? 'secondary' : 'default'}
          className="h-6 shrink-0 text-fs-sm"
          onClick={() => void toggleWatch(!isWatchingLive)}
          disabled={busy}
          data-testid="paused-banner-watch"
          title={
            isWatchingLive
              ? 'Stop watching the running engine — the database stays paused'
              : `Watch the running engine live at ${profilerEndpoint}`
          }
        >
          {busy ? 'Working…' : isWatchingLive ? 'Stop watching' : 'Watch live'}
        </Button>
      )}
    </div>
  );
}
