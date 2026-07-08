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
// Fallback when sessionStorage is unavailable (private mode / storage disabled): keep the token for this page load only.
let inMemoryToken: string | null = null;

/**
 * Reads `wbtoken` and optional `db` from the URL fragment, persists the token to sessionStorage, records the db
 * path for the initial session, and strips the fragment. Call once, before the first API request (from main.tsx).
 */
export function captureLaunchParamsFromUrl(): void {
  if (typeof window === 'undefined' || !window.location.hash) {
    return;
  }

  const params = new URLSearchParams(window.location.hash.slice(1));
  const token = params.get('wbtoken');
  const db = params.get('db');

  if (!token && !db) {
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

  // Strip the fragment so the token/db never persist in the address bar, browser history, or a referrer header.
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
