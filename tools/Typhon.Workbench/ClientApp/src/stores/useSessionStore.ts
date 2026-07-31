import { create } from 'zustand';
import type { SessionDto, SessionDiagnosticDto } from '@/api/generated/model';

export type SessionKind = 'none' | 'open' | 'attach' | 'trace';
export type SessionState = 'Ready' | 'MigrationRequired' | 'Incompatible' | 'Attached' | 'Trace';

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
  setSession: (dto) =>
    set({
      kind: (dto.kind?.toLowerCase() ?? 'open') as SessionKind,
      capabilities: ((dto.capabilities as string[] | null | undefined) ?? []) as SessionCapability[],
      activeProfileId: (dto.activeProfileId as string | null | undefined) ?? null,
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
 * True when the session's profiler data comes from a capture **file** — a trace session, or an open database with a
 * profile attached — as opposed to a live attach stream.
 *
 * A few profiler surfaces genuinely need this narrower question rather than "can this session profile at all": a
 * recorded capture has a source file on disk to re-read, a CPU-frame manifest and a reload-on-overwrite check, none of
 * which a live socket has. Keeping the distinction explicit here beats scattering `kind !== 'attach'` through the
 * panels, where it would read as an arbitrary exclusion rather than a statement about where the data comes from.
 */
export const useTraceBackedSession = (): boolean =>
  useSessionStore((s) => s.capabilities.includes('profiler') && s.kind !== 'attach');
