// Jupyter-style bootstrap-token handoff (#429).
//
// When the SPA is served directly by Kestrel (the `typhon ui` tool — no Vite dev-proxy to inject the
// X-Workbench-Token header), the host opens the browser at `…/#wbtoken=<token>[&db=<path>]`. We read that
// token from the URL *fragment* (which is never sent to the server or written to request logs), move it into
// sessionStorage, and strip it from the address bar so it doesn't linger in history/referrer. Every API call
// then attaches it as the X-Workbench-Token header (see `client.ts`). The custom header can't be forged
// cross-origin without a CORS grant the server never gives, so the loopback CSRF protection is preserved.
//
// In Vite dev there is no fragment, so `getBootstrapToken()` returns null and the dev-proxy remains the sole
// injector — this module is a no-op there.

const BOOTSTRAP_TOKEN_KEY = 'wb.bootstrapToken';

let initialDbPath: string | null = null;
let initialSchemaPaths: string[] = [];
let initialTracePath: string | null = null;
// Fallback when sessionStorage is unavailable (private mode / storage disabled): keep the token for this page load only.
let inMemoryToken: string | null = null;

/**
 * Reads `wbtoken` and optional `db` / `schema` / `trace` from the URL fragment, persists the token to sessionStorage,
 * records the db / schema / trace paths for the initial session, and strips the fragment. Call once, before the first
 * API request (from main.tsx).
 */
export function captureLaunchParamsFromUrl(): void {
  if (typeof window === 'undefined' || !window.location.hash) {
    return;
  }

  const params = new URLSearchParams(window.location.hash.slice(1));
  const token = params.get('wbtoken');
  const db = params.get('db');
  const schema = params.get('schema');
  const trace = params.get('trace');

  if (!token && !db && !trace) {
    return;
  }

  if (token) {
    try {
      window.sessionStorage.setItem(BOOTSTRAP_TOKEN_KEY, token);
    } catch {
      // Private-mode / storage-disabled: fall back to keeping it in memory only for this page load.
      inMemoryToken = token;
    }
  }

  if (db) {
    initialDbPath = db;
  }

  // Schema assembly for the db auto-open (`typhon ui --open-db` / --schema). Passed as an explicit schema DLL so the
  // Workbench can interpret the database's archetypes/entities instead of falling back to the incompatible banner.
  if (schema) {
    initialSchemaPaths = [schema];
  }

  if (trace) {
    initialTracePath = trace;
  }

  // Strip the fragment so the token/db/schema/trace never persist in the address bar, browser history, or referrer.
  window.history.replaceState(null, '', window.location.pathname + window.location.search);
}

/** The bootstrap token captured from the launch URL, or null when running under the Vite dev-proxy. */
export function getBootstrapToken(): string | null {
  try {
    return window.sessionStorage.getItem(BOOTSTRAP_TOKEN_KEY) ?? inMemoryToken;
  } catch {
    return inMemoryToken;
  }
}

/** The database path passed via `typhon ui <db>` (URL fragment), or null. Consumed once at startup. */
export function getInitialDbPath(): string | null {
  return initialDbPath;
}

/**
 * Schema-assembly DLL paths passed via `typhon ui --open-db` / `--schema` (URL fragment) for interpreting the
 * initial database, or an empty array when none was given. Consumed once at startup by the db auto-open.
 */
export function getInitialSchemaPaths(): string[] {
  return initialSchemaPaths;
}

/** The trace path passed via `typhon ui --trace <path>` / `--open-latest` (URL fragment), or null. Consumed once at startup. */
export function getInitialTracePath(): string | null {
  return initialTracePath;
}

/**
 * Attaches the Workbench auth headers to a raw-`fetch` {@link Headers} object and returns it: the bootstrap token
 * (`X-Workbench-Token`, captured from the launch-URL fragment under `typhon ui`), the optional per-session token
 * (`X-Session-Token`), and the `X-Workbench-Api` marker. This mirrors `customFetch` (api/client.ts) — which is the
 * single source of this policy and delegates here.
 *
 * Hand-rolled `fetch` call sites (the profiler polling hooks that answer 202 while a cache builds — a shape the
 * Orval-generated client can't express) MUST use this. Under the Vite dev-proxy the missing bootstrap token is
 * injected server-side, so a raw fetch that only sent `X-Session-Token` still worked; under `typhon ui` there is no
 * proxy, so an un-tokenized request 401s on the `[RequireBootstrapToken]` filter. Never re-open that gap.
 */
export function applyWorkbenchAuthHeaders(headers: Headers, sessionToken?: string | null): Headers {
  const bootstrapToken = getBootstrapToken();
  if (bootstrapToken && !headers.has('X-Workbench-Token')) {
    headers.set('X-Workbench-Token', bootstrapToken);
  }
  if (sessionToken && !headers.has('X-Session-Token')) {
    headers.set('X-Session-Token', sessionToken);
  }
  headers.set('X-Workbench-Api', '1');
  return headers;
}
