import { useEffect } from 'react';
import { PauseCircle } from 'lucide-react';
import { getApiSessionsId, useGetApiSessionsIdState } from '@/api/generated/sessions/sessions';
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
      </div>
    </div>
  );
}
