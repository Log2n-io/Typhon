import { useState } from 'react';
import { useOpenDatabaseFile } from '@/hooks/useOpenDatabaseFile';
import { useSessionStore } from '@/stores/useSessionStore';
import { extractDetail } from '@/shell/dialogs/connectErrors';

/** Session states a reopen can plausibly change the outcome of. Anything else is already as good as it gets. */
const RETRYABLE_STATES: ReadonlySet<string> = new Set(['Incompatible', 'MigrationRequired']);

/**
 * Whether reopening the current database could plausibly help — an open file session in a state a schema fix resolves.
 *
 * **Deliberately store-only, and separate from {@link useRetryDatabaseOpen}.** The retry itself needs a TanStack
 * mutation; this predicate needs three fields off the session store. Keeping them apart lets a caller *decide whether
 * to offer* the retry without acquiring a network dependency — which is what stops the blocked-state banners (five
 * tests about their wording) from all needing a `QueryClientProvider` to render.
 */
export function useCanRetryDatabaseOpen(): boolean {
  const filePath = useSessionStore((s) => s.filePath);
  const kind = useSessionStore((s) => s.kind);
  const sessionState = useSessionStore((s) => s.sessionState);

  return kind === 'open' && !!filePath && RETRYABLE_STATES.has(sessionState ?? '');
}

/**
 * Re-open the current database file, so a schema fix made *after* the open is actually tried.
 *
 * **The gap this closes.** Registering a schema directory (Options → Schema) writes a setting and stops there:
 * `PATCH /api/options/schema` normalises the list, stores it, and returns — nothing re-resolves the schema, nothing
 * re-evaluates the session, and the `Incompatible` banner stays up unchanged. The user did the right thing and the
 * app gave no sign of it. The only way forward was to close the database and open it again, which nothing said.
 *
 * **A reopen is the whole mechanism, because the server already does the work.** `CreateFileSession` reads the
 * registered directories fresh from the options store on every open (`registeredSchemaDirs`), and drops any existing
 * session for the same path *before* opening — so re-POSTing the same file releases the old engine's lock and
 * resolves the manifest again against the new directory list. There is nothing to add server-side.
 *
 * **Schema DLLs are deliberately re-resolved, not carried over.** The session DTO's `schemaDllPaths` is what the last
 * open *resolved*, not what the caller asked for; feeding it back as an explicit list would pin the retry to the
 * paths that just failed and skip the directories the user registered — the exact opposite of the intent.
 *
 * Only call this from a component that is mounted **because** a retry is on offer (see `useCanRetryDatabaseOpen`).
 */
export function useRetryDatabaseOpen() {
  const filePath = useSessionStore((s) => s.filePath);
  const { openDatabaseFile } = useOpenDatabaseFile();

  const [isRetrying, setIsRetrying] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function retry(): Promise<void> {
    if (!filePath || isRetrying) {
      return;
    }
    setIsRetrying(true);
    setError(null);
    try {
      await openDatabaseFile(filePath, []);
    } catch (err) {
      // `openDatabaseFile` already logged it; surface it beside the button too, so a retry that fails for a NEW
      // reason (the file moved, another process took the lock) does not read as "nothing happened" — which is
      // precisely the complaint this retry path exists to answer.
      setError(extractDetail(err) || String(err));
    } finally {
      setIsRetrying(false);
    }
  }

  return { isRetrying, error, retry };
}
