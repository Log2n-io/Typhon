import { useLiveStreamSession, useSessionStore, useTraceBackedSession } from '@/stores/useSessionStore';

/**
 * Which session a profiler panel should read telemetry from, and whether that telemetry is arriving now.
 *
 * **The one thing every panel got wrong: `sessionKind === 'attach' ? sessionId : null`.** Three of the four call sites
 * of `useLiveGaugeData` were written that way, which asks "is this a TCP session" when the question is "does this
 * session have telemetry". The two coincide for a plain file and a plain attach, and diverge for the two shapes that
 * matter most:
 *
 * | Session | kind | telemetry |
 * |---|---|---|
 * | a `.typhon` file | `open` | none |
 * | a live engine over TCP | `attach` | live |
 * | a file with a **capture attached** | `open` | **recorded** |
 * | a file another process **holds**, opened paused and watching it | `open` | **live** |
 *
 * So a captured profile shows nothing at all in the Spatial, Subscriptions and Realms panels, however much data it
 * holds — the panels are keyed on how you connected rather than on what you have. `useSessionStore` already warns
 * about this in as many words: `isLiveStreamSession` is *"deliberately NOT `kind === 'attach'` … the only thing that
 * ever made live a property of Attach was call sites asking about `kind`. That is what left the Record control missing
 * on a watching database session."*
 *
 * Exported as one hook so a fourth call site cannot re-derive it wrongly.
 */
export interface TelemetrySession {
  /** The session to read telemetry from, or `null` when this session has none. Pass straight to `useLiveGaugeData`. */
  readonly sessionId: string | null;
  /** Whether this session has per-tick telemetry at all — live or recorded. False means "no such data", not "loading". */
  readonly hasTelemetry: boolean;
  /** Whether it is arriving now, as opposed to being replayed from a capture. Decides whether a window follows the head. */
  readonly isLive: boolean;
}

export function useTelemetrySession(): TelemetrySession {
  const sessionId = useSessionStore((s) => s.sessionId);
  const isLive = useLiveStreamSession();
  const traceBacked = useTraceBackedSession();
  const hasTelemetry = isLive || traceBacked;

  return { sessionId: hasTelemetry ? sessionId : null, hasTelemetry, isLive };
}
