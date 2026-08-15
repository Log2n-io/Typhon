import { create } from 'zustand';
import type { SessionDto, SessionDiagnosticDto } from '@/api/generated/model';

/** Two entry modes since #621: open a database, or attach to a live engine. A capture attaches TO one of those. */
export type SessionKind = 'none' | 'open' | 'attach';
export type SessionState = 'Ready' | 'MigrationRequired' | 'Incompatible' | 'Attached';

/**
 * What a session can *do*, as opposed to what it *is* (#617, design D-10).
 *
 * Panels used to decide their own visibility from `kind`. That stopped working when a capture could be attached to an
 * open database: the session is still `kind === 'open'`, and it can now profile. The capability is acquired and released
 * during the session's life while its kind never changes, so no kind check can express it.
 */
export type SessionCapability = 'profiler' | 'database';

interface SessionStoreState {
  kind: SessionKind;
  sessionId: string | null;
  token: string | null;
  sessionState: SessionState | null;
  filePath: string | null;
  schemaDllPaths: string[] | null;
  schemaStatus: string | null;
  loadedComponentTypes: number;
  schemaDiagnostics: SessionDiagnosticDto[] | null;
  /** Capabilities the server reported for this session. Never derived from `kind` on the client. */
  capabilities: SessionCapability[];
  /** Which attached profile is driving the profiler panels, or null when the session has none. */
  activeProfileId: string | null;
  /**
   * True while the session has released its database to another process (#621).
   *
   * Distinct from "loading" and from "failed", and the difference matters: a paused session is a *working* session whose
   * profiler panels are fully usable, so rendering it as an error would teach the user to close and reopen — the exact
   * dance pausing exists to remove. Read from the server rather than inferred from the absence of the `database`
   * capability, which is also absent on Attach sessions that were never paused at all.
   */
  isPaused: boolean;
  /** Human-readable explanation for the paused banner — names the holding process. Null when not paused. */
  pausedReason: string | null;
  /**
   * The profiler endpoint the holding process advertises in the database's `db.lock`, or null when it advertises none.
   *
   * Non-null is the whole condition for offering *Watch live*: the process that took the database is a Typhon app with
   * a live port open. A pause caused by an editor, a backup or an older build reports null, and offering to watch that
   * would only produce a connection refused.
   */
  profilerEndpoint: string | null;
  /** True while this session is watching that endpoint. Server-owned, so it survives a reload and a second tab. */
  isWatchingLive: boolean;
  setSession: (dto: SessionDto) => void;
  clearSession: () => void;
}

export const useSessionStore = create<SessionStoreState>()((set) => ({
  kind: 'none',
  sessionId: null,
  token: null,
  sessionState: null,
  filePath: null,
  schemaDllPaths: null,
  schemaStatus: null,
  loadedComponentTypes: 0,
  schemaDiagnostics: null,
  capabilities: [],
  activeProfileId: null,
  isPaused: false,
  pausedReason: null,
  profilerEndpoint: null,
  isWatchingLive: false,
  setSession: (dto) =>
    set({
      kind: (dto.kind?.toLowerCase() ?? 'open') as SessionKind,
      profilerEndpoint: (dto.profilerEndpoint as string | null | undefined) ?? null,
      isWatchingLive: dto.isWatchingLive === true,
      capabilities: ((dto.capabilities as string[] | null | undefined) ?? []) as SessionCapability[],
      activeProfileId: (dto.activeProfileId as string | null | undefined) ?? null,
      isPaused: dto.isPaused === true,
      pausedReason: dto.isPaused === true ? ((dto.reason as string | null | undefined) ?? null) : null,
      sessionId: dto.sessionId,
      token: dto.sessionId,
      sessionState: (dto.state as SessionState) ?? null,
      filePath: dto.filePath ?? null,
      schemaDllPaths: (dto.schemaDllPaths as string[] | null | undefined) ?? null,
      schemaStatus: (dto.schemaStatus as string | null | undefined) ?? null,
      loadedComponentTypes: dto.loadedComponentTypes != null ? Number(dto.loadedComponentTypes) : 0,
      schemaDiagnostics:
        (dto.schemaDiagnostics as SessionDiagnosticDto[] | null | undefined) ?? null,
    }),
  clearSession: () =>
    set({
      kind: 'none',
      sessionId: null,
      token: null,
      sessionState: null,
      filePath: null,
      schemaDllPaths: null,
      schemaStatus: null,
      loadedComponentTypes: 0,
      schemaDiagnostics: null,
      capabilities: [],
      activeProfileId: null,
      isPaused: false,
      pausedReason: null,
      profilerEndpoint: null,
      isWatchingLive: false,
    }),
}));

/**
 * True when the current session can do `capability`.
 *
 * Use this instead of testing `kind`. A profiler panel asking "am I in a trace session?" gets the wrong answer for an
 * open database with a capture attached — the question it actually means is "is there a capture to show?".
 *
 * Written as a plain selector rather than a hook so it works in the non-React call sites too (command predicates,
 * `viewRegistry`, `DockHost` layout decisions), which read the store imperatively.
 */
export const sessionHasCapability = (
  state: Pick<SessionStoreState, 'capabilities'>,
  capability: SessionCapability,
): boolean => state.capabilities.includes(capability);

/** Hook form of {@link sessionHasCapability}, for components. */
export const useSessionCapability = (capability: SessionCapability): boolean =>
  useSessionStore((s) => s.capabilities.includes(capability));

/**
 * True when the session's profiler data comes from a capture **file** — as opposed to a live attach stream.
 *
 * A few profiler surfaces genuinely need this narrower question rather than "can this session profile at all": a
 * recorded capture has a source file on disk to re-read, a CPU-frame manifest and a reload-on-overwrite check, none of
 * which a live socket has.
 *
 * The attach clause is not simply `kind !== 'attach'` any more (#621): an attach session that saved a replay and
 * attached it back IS reading a file, and the surfaces gated here work for it. `activeProfileId` is exactly the
 * "a capture is attached" signal, so the test states the real condition instead of approximating it by kind.
 *
 * P5 removed the kind clause entirely. It read "an Open session is always file-backed", which was true only while
 * `open` implied a database and nothing live. A paused Open session watching its holder has the profiler capability
 * and **no file at all** — it would have been reported as trace-backed, offering Call Tree scoping (CPU sampling is
 * file-mode only) against a live socket with nothing on disk to scope. Since #613 deleted the standalone trace
 * session, every file-backed session reaches its capture as an attached profile, so `activeProfileId` alone is the
 * condition for all kinds.
 */
export const useTraceBackedSession = (): boolean =>
  useSessionStore((s) => s.capabilities.includes('profiler') && s.activeProfileId !== null);

/**
 * True when the session has a live profiler stream to subscribe to — the counterpart of
 * {@link useTraceBackedSession}, and deliberately NOT `kind === 'attach'`.
 *
 * A paused Open session watching its holder's engine is live too (P5). The stream endpoint and the SSE hook are both
 * kind-agnostic — the server gates on `ILiveProfilerHost`, which an `OpenSession` implements once it is watching — so
 * the only thing that ever made live a property of Attach was call sites asking about `kind`. That is what left the
 * Record control missing on a watching database session while the whole server side was ready for it.
 *
 * Written as a plain predicate over the two fields so it can be unit-tested without rendering a panel.
 */
export const isLiveStreamSession = (state: Pick<SessionStoreState, 'kind' | 'isWatchingLive'>): boolean =>
  state.kind === 'attach' || state.isWatchingLive;

/** Hook form of {@link isLiveStreamSession}, for components. */
export const useLiveStreamSession = (): boolean => useSessionStore(isLiveStreamSession);

/**
 * True when a database-backed panel should show a *paused* state rather than an error (#621).
 *
 * The precise question is "this session has a database, but not right now", which is why it is both flags and not
 * either alone. `!hasDatabase` is also true of an Attach session, where there is no database to come back and nothing
 * to wait for; `isPaused` alone would hold panels in a paused state for the instant after resume, before the capability
 * refresh lands.
 */
export const useDatabasePaused = (): boolean =>
  useSessionStore((s) => s.isPaused && !s.capabilities.includes('database'));
