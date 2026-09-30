import { useMemo } from 'react';
import { useProfilerCache } from '@/hooks/profiler/useProfilerCache';
import { selectEffectiveScope, useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useTelemetrySession } from '@/hooks/profiler/useTelemetrySession';
import type { TickData } from '@/libs/profiler/model/traceModel';

/**
 * The ticks the Realms panel should read per-realm records from, and whether this session has any at all.
 *
 * The "which sessions have telemetry" question is answered once, in {@link useTelemetrySession}; this hook adds only
 * the windowing, because the Realms panel wants every tick in scope rather than the bounded rate window the gauge
 * panels compute over.
 *
 * **Not `kind === 'attach'`, and that distinction is the whole point of this hook.** Per-realm telemetry is a property
 * of having profiler data, which is orthogonal to how the session was opened:
 *
 * | Session | kind | catalog | telemetry |
 * |---|---|---|---|
 * | a `.typhon` file | `open` | yes | no |
 * | a live engine over TCP | `attach` | no | yes, live |
 * | a file with a **capture attached** | `open` | yes | **yes, recorded** |
 * | a file another process **holds**, opened paused and watching it | `open` | yes | **yes, live** |
 *
 * The last two are `kind === 'open'` and carry kind-67 rows, so a kind test hides the live view on exactly the sessions
 * that have both halves of a realm. `useSessionStore` warns about this in so many words — `isLiveStreamSession` is
 * "deliberately NOT `kind === 'attach'` … the only thing that ever made live a property of Attach was call sites asking
 * about `kind`. That is what left the Record control missing on a watching database session."
 *
 * **The ticks are narrowed by the global time scope, which is what makes a past tick readable at all.** Realm records
 * are per tick, and the panel shows one tick — so "which tick" is the question, and the answer is the same one every
 * other profiler surface gives: the committed view range, honouring its unlink toggle (IA §3.4). Select a range on the
 * timeline and the panel answers for the last tick in it; select nothing and it tracks the head of the stream. That is
 * one concept the user already has, rather than a realm-specific notion of "selected tick".
 *
 * Note the Spatial Maintenance panel has both of these shapes too (`sessionKind === 'attach'` and a head-of-window
 * read). Fixing it is its own change; this hook is the piece it would reuse.
 */
export interface RealmTelemetry {
  /** Ticks inside the effective scope that the panel may read realm records from. Empty when there is no telemetry. */
  readonly ticks: TickData[];
  /** Whether this session has per-realm telemetry at all — live or recorded. False means "no such data", not "loading". */
  readonly hasTelemetry: boolean;
  /** Whether that telemetry is arriving now, as opposed to being replayed from a capture. */
  readonly isLive: boolean;
}

export function useRealmTelemetry(): RealmTelemetry {
  const { sessionId, hasTelemetry, isLive } = useTelemetrySession();

  // `isLive` drives the registry's live-tail prefetch; a replayed capture must not claim it (#289).
  const { ticks } = useProfilerCache(sessionId, isLive);
  const scope = useProfilerViewStore(selectEffectiveScope);
  const scopeLinked = useProfilerViewStore((s) => s.scopeLinked);

  // **Linked means "follow", and on a live stream what it follows is the head.** A live session's view range starts at
  // the beginning of the capture and does not chase the stream, so honouring it unconditionally would pin this panel to
  // tick 1 for the rest of the session — measured, not feared. Unlinking is the app's own freeze affordance (GAP-11),
  // and it is what a user reaches for to hold a moment still, so that is the gesture this respects.
  //
  // A replay has no head to chase: its range IS the scrub control, so it is honoured while linked. That is the whole
  // of "display them for a selected tick" — one concept the user already has, rather than a realm-specific one.
  const followHead = isLive && scopeLinked;

  const scoped = useMemo(() => {
    if (ticks.length === 0 || followHead) {
      return ticks;
    }
    // A degenerate or unset scope means "everything" rather than "nothing": the panel must show the head of the stream
    // before anyone has touched the timeline, which is the state every session starts in.
    if (!(scope.endUs > scope.startUs)) {
      return ticks;
    }
    const inScope = ticks.filter((t) => t.endUs >= scope.startUs && t.startUs <= scope.endUs);
    // Never answer "no realms" merely because the scope fell between two ticks — that reads as a dormant engine.
    return inScope.length > 0 ? inScope : ticks;
  }, [ticks, scope, followHead]);

  return { ticks: scoped, hasTelemetry, isLive };
}
