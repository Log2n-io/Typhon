import { useEffect, useRef } from 'react';
import { getInitialTracePath } from '@/api/bootstrapToken';
import { usePostApiSessionsTrace } from '@/api/generated/sessions/sessions';
import { useSessionStore } from '@/stores/useSessionStore';
import { useRecentFilesStore } from '@/stores/useRecentFilesStore';
import { logError, logInfo } from '@/stores/useLogStore';
import { extractDetail } from '@/shell/dialogs/connectErrors';
import { toggleViewProfiler } from '@/shell/commands/profilerCommands';

/**
 * On first mount, if `typhon ui --trace <path>` (or `--open-latest`) handed a profiler-trace path in the
 * launch-URL fragment, open it automatically into the initial session — mirroring {@link useInitialDbAutoOpen}
 * and reusing the same POST /api/sessions/trace flow as the Connect dialog's Open-Trace tab. No-op when no path
 * was passed. Runs exactly once, guarded against StrictMode's double-invoke.
 */
export function useInitialTraceAutoOpen(): void {
  const setSession = useSessionStore((s) => s.setSession);
  const recordRecent = useRecentFilesStore((s) => s.record);
  const postTrace = usePostApiSessionsTrace();
  const startedRef = useRef(false);

  useEffect(() => {
    if (startedRef.current) {
      return;
    }
    const filePath = getInitialTracePath();
    if (!filePath) {
      return;
    }
    startedRef.current = true;
    logInfo(`Opening trace: ${filePath}`, { filePath });
    void postTrace
      .mutateAsync({ data: { filePath } })
      .then((response) => {
        const dto = response.data;
        setSession(dto);
        recordRecent({
          filePath: dto.filePath ?? filePath,
          schemaDllPaths: [],
          lastOpenedAt: new Date().toISOString(),
          lastState: 'Ready',
          kind: 'trace',
        });
        logInfo(`Trace session opened`, { sessionId: dto.sessionId, filePath: dto.filePath ?? filePath });
        toggleViewProfiler();
      })
      .catch((err) => {
        // Failure already surfaced in the Logs panel; the welcome screen stays up for a manual retry.
        logError(`Failed to open trace: ${filePath}`, {
          filePath,
          error: extractDetail(err) || String(err),
        });
      });
  }, [postTrace, setSession, recordRecent]);
}
