import { useEffect, useRef } from 'react';
import { getInitialTracePath } from '@/api/bootstrapToken';
import { getApiSessionsId, usePostApiSessionsFile } from '@/api/generated/sessions/sessions';
import { usePostApiSessionsSessionIdProfile } from '@/api/generated/profiles/profiles';
import { useSessionStore } from '@/stores/useSessionStore';
import { useRecentFilesStore } from '@/stores/useRecentFilesStore';
import { logError, logInfo, logWarn } from '@/stores/useLogStore';
import { extractDetail } from '@/shell/dialogs/connectErrors';
import { toggleViewProfiler } from '@/shell/commands/profilerCommands';
import { bundleOfCapture, captureFileName } from '@/libs/profiles/captureLocation';

/**
 * On first mount, opens the capture `typhon ui --trace <path>` (or `--open-latest`) handed us in the launch-URL
 * fragment. No-op when no path was passed. Runs exactly once, guarded against StrictMode's double-invoke.
 *
 * <p><b>Since #621 this opens the database, then attaches the capture to it.</b> There is no longer a standalone trace
 * session to open into — a capture belongs to the database it was recorded against, and reaching it through that
 * database is what makes the correlation bridges available at all. The bundle is derived from the capture's own path
 * (D-1 co-location), so nothing is guessed or looked up.</p>
 *
 * <p>If that database is held by the running application, the open lands in a <b>paused</b> session and the capture
 * still attaches — captures are files. That is precisely the case this launch path exists for: you ran your app, it
 * wrote a capture, and you want to look at it while the app is still running.</p>
 */
export function useInitialTraceAutoOpen(): void {
  const setSession = useSessionStore((s) => s.setSession);
  const recordRecent = useRecentFilesStore((s) => s.record);
  const postFile = usePostApiSessionsFile();
  const postProfile = usePostApiSessionsSessionIdProfile();
  const startedRef = useRef(false);

  useEffect(() => {
    if (startedRef.current) {
      return;
    }
    const capturePath = getInitialTracePath();
    if (!capturePath) {
      return;
    }
    startedRef.current = true;

    const bundle = bundleOfCapture(capturePath);
    if (!bundle) {
      // A capture outside a `profilings/` directory has no database to open. Say so rather than opening something
      // arbitrary — the user asked for a specific file and deserves to know why it did not appear.
      logError('Cannot open capture: it is not inside a database profilings/ directory', { capturePath });
      return;
    }

    const fileName = captureFileName(capturePath);
    logInfo(`Opening database for capture: ${fileName}`, { capturePath, bundle });

    void (async () => {
      try {
        const opened = await postFile.mutateAsync({ data: { filePath: bundle } });
        const dbSession = opened.data;
        setSession(dbSession);
        recordRecent({
          filePath: dbSession.filePath ?? bundle,
          schemaDllPaths: [],
          lastOpenedAt: new Date().toISOString(),
          lastState: 'Ready',
          kind: 'db',
        });

        await postProfile.mutateAsync({ sessionId: dbSession.sessionId, data: { fileName } });

        // Re-read the session: attaching changes its capabilities server-side, and the profiler panels gate on those.
        // Pushing the attach response alone would leave the store advertising a session that cannot profile — the #617
        // seam, where the session is not query-backed so nothing invalidates it for us.
        const refreshed = await getApiSessionsId(dbSession.sessionId);
        setSession(refreshed.data);
        logInfo('Capture attached', { sessionId: dbSession.sessionId, fileName });
        toggleViewProfiler();
      } catch (err) {
        // The database may have opened even if the attach failed; leaving the session in place is more useful than
        // tearing it down, so this only reports.
        logWarn(`Failed to open capture: ${fileName}`, {
          capturePath,
          error: extractDetail(err) || String(err),
        });
      }
    })();
  }, [postFile, postProfile, setSession, recordRecent]);
}
