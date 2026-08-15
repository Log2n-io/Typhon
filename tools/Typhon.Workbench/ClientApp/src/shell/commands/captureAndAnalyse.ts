import { postApiSessionsSessionIdProfilerSaveReplay } from '@/api/generated/profiler/profiler';
import { getApiSessionsId } from '@/api/generated/sessions/sessions';
import { useSessionStore } from '@/stores/useSessionStore';
import { logError, logInfo } from '@/stores/useLogStore';
import { extractDetail } from '@/shell/dialogs/connectErrors';
import { toggleViewProfiler } from '@/shell/commands/profilerCommands';

/**
 * Capture & Analyse (#377 Stage 4 Phase 4, GAP-22) — the one-gesture freeze → save → analyse flow.
 *
 * `POST /save-replay` accepts an empty `path`, in which case the server picks a default under
 * `%LOCALAPPDATA%/Typhon/Workbench/captures/typhon-capture-{ISO}.typhon-replay`, writes it, and — since #621 —
 * **attaches it back to this same live session as its active profile**, returning the new `profileId`.
 *
 * <p><b>Why the attach happens server-side.</b> The standalone trace session was removed with the move to two entry
 * modes, so a capture is always attached TO a session. A replay taken over TCP has no database the Workbench can reach
 * (blocker B1), which leaves the live session it came from as its only home. Attaching it in the same request that
 * wrote it also keeps the "attach any file by path" hole closed: the only path ever attached is one the server itself
 * just produced, so no client-supplied path is ever trusted.</p>
 *
 * <p>Why server-side default-path resolution: the client cannot compute `%LOCALAPPDATA%` without a system file picker,
 * and the server already knows both that root and the captures layout.</p>
 *
 * <p>Throws on any step's failure so callers can surface the error — the ReconnectBanner button and the Engine Live
 * Health "Capture & Analyse" button both rely on a thrown error to keep the banner up.</p>
 */
export interface CaptureAndAnalyseResult {
  /** Resolved absolute path of the saved replay file (echoed by the server). */
  replayPath: string;
  /** Size of the written replay in bytes — surfaced for logs / future telemetry. */
  bytesWritten: number;
  /**
   * The session now showing the replay. Unchanged from the one passed in since #621 — the replay attaches to the live
   * session rather than replacing it with a new one, so the session id, its token and all client state survive.
   */
  newSessionId: string;
}

export async function captureAndAnalyse(sessionId: string): Promise<CaptureAndAnalyseResult> {
  logInfo('Capture & Analyse — saving live attach session', { sessionId });
  try {
    // Step 1 — POST /save-replay with an empty path → server picks the default location, writes it, and attaches it.
    const saveResponse = await postApiSessionsSessionIdProfilerSaveReplay(sessionId, {});
    // The response DTO marks `path` and `bytesWritten` as nullable / int64-as-string (Orval's faithful mirror of the
    // OpenAPI generic spec). In practice the server always populates them on a 200; we narrow defensively.
    const replayPath = saveResponse.data.path ?? '';
    const bytesWritten = Number(saveResponse.data.bytesWritten ?? 0);
    if (replayPath === '') {
      throw new Error('Capture & Analyse: server returned 200 but no path field.');
    }
    if (!saveResponse.data.profileId) {
      // The file is on disk and the save genuinely succeeded — but with nothing attached there is no capture to land
      // in, so reporting success would leave the user in an unchanged live view and look like the command did nothing.
      throw new Error(`Capture & Analyse: replay saved to ${replayPath} but could not be attached for analysis.`);
    }
    logInfo('Capture & Analyse — replay saved and attached', { sessionId, replayPath, bytesWritten });

    // Step 2 — re-read the session. Attaching changed its capabilities and active profile server-side, and the profiler
    // panels gate on those; pushing a stale DTO is the #617 seam that made an attached capture render nothing.
    const refreshed = await getApiSessionsId(sessionId);
    useSessionStore.getState().setSession(refreshed.data);
    toggleViewProfiler();

    logInfo('Capture & Analyse — landed in Profiler on the saved replay', { sessionId, replayPath });
    return { replayPath, bytesWritten, newSessionId: sessionId };
  } catch (err) {
    logError('Capture & Analyse — failed', { sessionId, error: extractDetail(err) || String(err) });
    throw err;
  }
}
