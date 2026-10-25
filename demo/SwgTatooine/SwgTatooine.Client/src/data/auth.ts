/**
 * The client's half of A2/A3: sign up, log in, and hold the bearer token the WebSocket `HELLO` presents.
 *
 * <b>The token is the only thing a session needs, and it is deliberately opaque here.</b> The server mints
 * `base64url(payload‖HMAC)` and this code never looks inside: a client that parsed the expiry would be trusting a field
 * it cannot verify, and would then have two opinions about when the session ends. What it is told instead —
 * `expiresInSeconds` — is a plain number from the same response, which is enough to plan a re-login and nothing more.
 */

/** What `/auth/signup` and `/auth/login` return on success. */
export interface AuthGrant {
  /** The bearer token to present on `HELLO`. */
  readonly token: string;
  /** The NORMALISED account name — lower-cased by the server, which is what it is actually logged in as. */
  readonly name: string;
  /** How long the token is good for. */
  readonly expiresInSeconds: number;
}

/** Why an attempt failed, in the words the server chose. */
export interface AuthRefusal {
  /** The sentence to show. Already fit for a person: the server never sends a code for the UI to translate. */
  readonly error: string;
  /** The HTTP status, so a caller can distinguish "taken" (409) from "wrong" (401) without reading the sentence. */
  readonly status: number;
}

export type AuthResult =
  { readonly ok: true; readonly grant: AuthGrant } | { readonly ok: false; readonly refusal: AuthRefusal };

/** Where the session's token lives between page loads. */
const STORAGE_KEY = 'swg.auth';

/**
 * How a stored grant is kept.
 *
 * <b>`sessionStorage`, not `localStorage`, and that is the security decision in this file.</b> A token in
 * `localStorage` outlives the tab and is readable by any script on the origin for as long as it is valid; in
 * `sessionStorage` it dies with the tab, which bounds a stolen one to a session the person is actually in. The cost is
 * that a new tab logs in again, which is the right trade for a demo with no refresh flow — and the server's two-hour
 * expiry is the only other bound there is, since a token it does not store cannot be revoked.
 */
interface StoredGrant {
  readonly token: string;
  readonly name: string;
  /** Wall-clock milliseconds, so a reload can discard a token that expired while the tab was closed. */
  readonly expiresAtMs: number;
}

/**
 * Posts a credential to one of the `/auth` endpoints.
 *
 * @param path `/auth/signup` or `/auth/login`.
 * @param name The account name as typed.
 * @param password The plaintext. Never logged, never stored, never put in a URL.
 * @returns The grant, or the server's refusal.
 *
 * @remarks
 * <b>A POST body, which is the whole reason this is not a query string.</b> A password in a URL is written to every
 * access log, every proxy's history and the browser's own — four places nobody audits. The server refuses to read one
 * from anywhere else.
 *
 * <b>Every failure shape is turned into a refusal rather than thrown</b>, including a network error. A login form has
 * one place to put a sentence and a caller that had to catch as well as check would eventually do only one of them.
 */
async function post(path: string, name: string, password: string, outfit = 0): Promise<AuthResult> {
  let response: Response;
  try {
    response = await fetch(path, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      // The outfit only when one was chosen: 0 is "none", which the server answers with its own pick.
      body: JSON.stringify(outfit > 0 ? { name, password, outfit } : { name, password }),
    });
  } catch {
    // Offline, DNS, a refused connection: the one case where the server said nothing at all.
    return { ok: false, refusal: { error: 'cannot reach the server', status: 0 } };
  }

  if (response.ok) {
    const grant = asGrant(await response.json().catch(() => null));
    if (grant === null) {
      // A 200 whose body is not a grant. Worth its own branch rather than a cast, because every field has a silent failure
      // downstream: a missing `token` becomes `grant?.token ?? null`, which is how the viewport spells GOD — so the client would
      // connect with no account at all while the UI said it was logged in — and a missing `expiresInSeconds` makes `expiresAtMs`
      // NaN, which every comparison answers false, so the grant would never be seen to expire.
      return {
        ok: false,
        refusal: { error: 'the server sent something that is not a login', status: response.status },
      };
    }

    remember(grant);
    return { ok: true, grant };
  }

  // The server's own sentence when it sent one; a status-shaped fallback when the body is not what we expect, which is
  // what a proxy's error page looks like.
  let error = `the server refused the request (${response.status})`;
  try {
    const body = (await response.json()) as { error?: string };
    if (typeof body.error === 'string' && body.error.length > 0) {
      error = body.error;
    }
  } catch {
    // Keep the fallback.
  }

  return { ok: false, refusal: { error, status: response.status } };
}

/**
 * The parsed body as a grant, or `null` when it is not one.
 *
 * @param body Whatever `response.json()` produced.
 * @returns The grant, or `null`.
 *
 * @remarks
 * Checks the three fields the client depends on and nothing else: a server that adds a field is not a server this cannot talk to.
 * `expiresInSeconds` must be a finite number — `NaN` and `Infinity` both pass `typeof === 'number'` and both break the expiry
 * arithmetic that decides whether to open a socket.
 */
function asGrant(body: unknown): AuthGrant | null {
  if (body === null || typeof body !== 'object') {
    return null;
  }

  const candidate = body as Partial<AuthGrant>;
  if (typeof candidate.token !== 'string' || candidate.token.length === 0) {
    return null;
  }

  if (typeof candidate.name !== 'string') {
    return null;
  }

  if (typeof candidate.expiresInSeconds !== 'number' || !Number.isFinite(candidate.expiresInSeconds)) {
    return null;
  }

  return { token: candidate.token, name: candidate.name, expiresInSeconds: candidate.expiresInSeconds };
}

/**
 * Creates an account and logs into it, so a signup does not need a second round trip.
 *
 * @param outfit The outfit its character will wear: one plus an index into the server's `assets.players`; 0 for the
 *   server's pick. Chosen once, here — changing it later is the economy's (Loïc, 2026-10-25).
 */
export const signUp = (name: string, password: string, outfit = 0): Promise<AuthResult> =>
  post('/auth/signup', name, password, outfit);

/** Logs into an existing account. */
export const logIn = (name: string, password: string): Promise<AuthResult> => post('/auth/login', name, password);

/** Stores a grant for the life of the tab. A failure to store is not a failure to log in. */
function remember(grant: AuthGrant): void {
  const stored: StoredGrant = {
    token: grant.token,
    name: grant.name,
    expiresAtMs: Date.now() + grant.expiresInSeconds * 1000,
  };

  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(stored));
  } catch {
    // Private browsing, blocked site data, a quota: the session still works, it just will not survive a reload.
  }
}

/**
 * The grant this tab already holds, or `null`.
 *
 * @returns A usable grant, or `null` when there is none or it has expired.
 *
 * @remarks
 * <b>The expiry is checked here so a reload does not open a doomed socket.</b> A token the server will refuse produces a
 * `CLOSE 4102` a second after connecting, which reads to a person as a broken server rather than as "log in again" — and
 * the client has the expiry in hand, so there is no reason to find out the hard way. The check is deliberately generous
 * about clock skew in only one direction: a minute of margin, so a token about to expire is treated as already gone.
 */
export function storedGrant(): AuthGrant | null {
  let raw: string | null;
  try {
    raw = sessionStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }

  if (raw === null) {
    return null;
  }

  try {
    const stored = JSON.parse(raw) as StoredGrant;

    // `expiresAtMs` is checked for being a FINITE number before it is used, because the comparison below cannot reject it
    // otherwise: a missing or corrupted stamp makes `remainingMs` NaN, and every comparison against NaN is false — so
    // `remainingMs <= MARGIN_MS` would wave it through as a grant that never expires.
    if (typeof stored.expiresAtMs !== 'number' || !Number.isFinite(stored.expiresAtMs)) {
      forget();
      return null;
    }

    const remainingMs = stored.expiresAtMs - Date.now();
    if (typeof stored.token !== 'string' || stored.token.length === 0 || remainingMs <= MARGIN_MS) {
      forget();
      return null;
    }

    return { token: stored.token, name: stored.name, expiresInSeconds: Math.floor(remainingMs / 1000) };
  } catch {
    // Something else wrote the key, or an older shape is still there.
    forget();
    return null;
  }
}

/** Treat a token with less than this left as gone, so a session is not opened to be refused a moment later. */
const MARGIN_MS = 60_000;

/** Drops the stored grant. Called on logout, and whenever one is found to be unusable. */
export function forget(): void {
  try {
    sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // Nothing to do: the token is gone from memory either way, which is what logging out means.
  }
}
