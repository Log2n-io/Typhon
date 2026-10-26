import { create } from 'zustand';

import { forget, logIn, signUp, storedGrant, type AuthGrant } from '../data/auth';

/**
 * Who this tab is logged in as (A8).
 *
 * <b>Separate from the UI store because its lifetime is different.</b> `ui-store` holds toggles and selections, which are
 * meaningless across a login; this holds the one fact that decides whether there is a game to show at all. Keeping them
 * apart also means the renderer, which reads the UI store with `getState()` every frame, never touches the token.
 *
 * <b>The token is in memory AND in `sessionStorage`</b> — see `data/auth.ts` for why that storage and not the other. The
 * copy here is the one everything reads; the stored one exists so a reload does not ask again.
 */
export interface SessionState {
  /** The grant, or `null` when nobody is logged in. */
  readonly grant: AuthGrant | null;
  /** Whether a signup or login is in flight, so the form can refuse to send twice. */
  readonly busy: boolean;
  /** The last refusal to show, or `null`. Cleared when a new attempt starts. */
  readonly error: string | null;
  /**
   * Why the session ended, when the server ended it, or `null`.
   *
   * <b>Distinct from {@link error}, which is about an attempt that never became a session.</b> "that account is already
   * playing" arrives on a CLOSE after a successful login, and showing it in the form's error slot would read as "your
   * password is wrong".
   */
  readonly kicked: string | null;
  // Declared as PROPERTIES rather than methods, deliberately: every caller selects one out of the store and calls it
  // detached (`useSessionStore((s) => s.logOut)`), which is exactly the shape `@typescript-eslint/unbound-method`
  // forbids for a method — rightly, since a method's `this` would be lost. None of these uses `this`; they close over
  // `set`. Property syntax says so in the type.
  readonly signUp: (name: string, password: string, outfit?: number) => Promise<boolean>;
  readonly logIn: (name: string, password: string) => Promise<boolean>;
  /** Drops the token and the stored copy. The socket is closed by the app noticing there is no grant. */
  readonly logOut: () => void;
  /** Forgets the last refusal: the login screen switching between its two forms, where one's refusal is not the other's. */
  readonly clearError: () => void;
  /** Records why the server closed the session, and drops the grant when it is a credential problem. */
  readonly noteClosed: (code: number, reason: string) => void;
}

/**
 * Close codes this client reacts to by name.
 *
 * Mirrored from `TatooineReplication`'s constants rather than read from the wire: the catalog carries no close codes, and
 * a client that treated every 41xx the same could not tell "log in again" from "come back later" — which is the one
 * distinction these three exist to make.
 */
export const CloseCodes = {
  /** `RequestShutdown` — the server is going away. Retrying the same credentials later is correct. */
  shutdown: 4100,
  /** The house is full. Also a retry, with no re-login. */
  houseFull: 4101,
  /** No valid token. The credential is the problem, so the grant is dropped and the form comes back. */
  notAuthenticated: 4102,
  /** That account is already playing. NOT a credential problem — the grant stays, because logging in again will not help. */
  alreadyPlaying: 4103,
} as const;

export const useSessionStore = create<SessionState>((set) => ({
  // Adopted at construction, so a reload with a live token goes straight into the world. `storedGrant` discards an
  // expired one, so this is never a token the server is about to refuse.
  grant: storedGrant(),
  busy: false,
  error: null,
  kicked: null,

  signUp: async (name, password, outfit = 0) => {
    set({ busy: true, error: null, kicked: null });
    const result = await signUp(name, password, outfit);
    set(result.ok ? { grant: result.grant, busy: false } : { busy: false, error: result.refusal.error });
    return result.ok;
  },

  logIn: async (name, password) => {
    set({ busy: true, error: null, kicked: null });
    const result = await logIn(name, password);
    set(result.ok ? { grant: result.grant, busy: false } : { busy: false, error: result.refusal.error });
    return result.ok;
  },

  logOut: () => {
    forget();
    set({ grant: null, error: null, kicked: null });
  },

  clearError: () => {
    set({ error: null });
  },

  noteClosed: (code, reason) => {
    // A credential the server will not accept must not be presented again: keeping it would reconnect, be refused, and
    // loop. Everything else keeps the grant, because the token is fine and the problem is the server's side.
    if (code === CloseCodes.notAuthenticated) {
      forget();
      set({ grant: null, kicked: reason.length > 0 ? reason : 'the server did not accept the login — try again' });
      return;
    }

    set({ kicked: reason.length > 0 ? reason : `the server closed the session (${code})` });
  },
}));
