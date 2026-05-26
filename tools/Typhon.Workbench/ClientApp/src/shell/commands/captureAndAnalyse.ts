import { postApiSessionsSessionIdProfilerSaveReplay } from '@/api/generated/profiler/profiler';
import { postApiSessionsTrace } from '@/api/generated/sessions/sessions';
import { useSessionStore } from '@/stores/useSessionStore';
import { useRecentFilesStore } from '@/stores/useRecentFilesStore';
import { logError, logInfo } from '@/stores/useLogStore';
import { extractDetail } from '@/shell/dialogs/connectErrors';
import { toggleViewProfiler } from '@/shell/commands/profilerCommands';

/**
 * Capture & Analyse (#377 Stage 4 Phase 4, GAP-22) — the one-gesture freeze → save → reopen flow.
 *
 * Server-side seam: `POST /save-replay` accepts an empty `path`, in which case it picks a default
 * under `%LOCALAPPDATA%/Typhon/Workbench/captures/typhon-capture-{ISO}.typhon-replay` and returns
 * the resolved absolute path. We then post `/sessions/trace` with that path — server creates a fresh
 * Trace session (single-session-at-a-time invariant drops the old Attach automatically) and the
 * client lands in the Profiler timeline on the saved replay (J2).
 *
 * Why server-side default-path resolution: the client can't compute `%LOCALAPPDATA%` without going
 * through a system file picker; the server already knows both the user's local-app-data root and
 * the captures directory layout, so it's the natural authority.
 *
 * Throws on any step's failure so callers can surface the error (the ReconnectBanner button + the
 * Engine Live Health "Capture & Analyse" button both rely on a thrown error to keep the banner up).
 */
export interface CaptureAndAnalyseResult {
  /** Resolved absolute path of the saved replay file (echoed by the server). */
  replayPath: string;
  /** Size of the written replay in bytes — surfaced for logs / future telemetry. */
  bytesWritten: number;
  /** Session id of the newly-opened Trace session that wraps the saved replay. */
  newSessionId: string;
}

export async function captureAndAnalyse(sessionId: string): Promise<CaptureAndAnalyseResult> {
  logInfo('Capture & Analyse — saving live attach session', { sessionId });
  try {
    // Step 1 — POST /save-replay with empty path → server picks the default capture location.
    const saveResponse = await postApiSessionsSessionIdProfilerSaveReplay(sessionId, {});
    // The response DTO marks `path` and `bytesWritten` as nullable / int64-as-string (Orval's faithful
    // mirror of the OpenAPI generic spec). In practice the server always populates them on a 200; we
    // narrow defensively and bail with a clear error if either is missing.
    const replayPath = saveResponse.data.path ?? '';
    const bytesWritten = Number(saveResponse.data.bytesWritten ?? 0);
    if (replayPath === '') {
      throw new Error('Capture & Analyse: server returned 200 but no path field.');
    }
    logInfo('Capture & Analyse — replay saved', { sessionId, replayPath, bytesWritten });

    // Step 2 — POST /sessions/trace with the saved path → new Trace session, old Attach is dropped.
    const traceResponse = await postApiSessionsTrace({ filePath: replayPath });
    const dto = traceResponse.data;

    // Step 3 — swap the active session in the client store + record the file for "Recent" + land in Profiler.
    useSessionStore.getState().setSession(dto);
    useRecentFilesStore.getState().record({
      filePath: dto.filePath ?? replayPath,
      schemaDllPaths: [],
      lastOpenedAt: new Date().toISOString(),
      lastState: 'Ready',
      kind: 'trace',
    });
    toggleViewProfiler();

    logInfo('Capture & Analyse — landed in Profiler on the saved replay', { sessionId: dto.sessionId, replayPath });
    return { replayPath, bytesWritten, newSessionId: dto.sessionId };
  } catch (err) {
    logError('Capture & Analyse — failed', { sessionId, error: extractDetail(err) || String(err) });
    throw err;
  }
}
