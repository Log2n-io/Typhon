import { useEffect, useRef } from 'react';
import { getInitialDbPath } from '@/api/bootstrapToken';
import { useOpenDatabaseFile } from '@/hooks/useOpenDatabaseFile';

/**
 * On first mount, if `typhon ui <db>` handed a database path in the launch-URL fragment, open it automatically
 * into the initial session — reusing the same flow as the Connect dialog (#429 AC8). No-op when no path was
 * passed (the normal welcome-screen path). Runs exactly once, guarded against StrictMode's double-invoke.
 */
export function useInitialDbAutoOpen(): void {
  const { openDatabaseFile } = useOpenDatabaseFile();
  const startedRef = useRef(false);

  useEffect(() => {
    if (startedRef.current) {
      return;
    }
    const db = getInitialDbPath();
    if (!db) {
      return;
    }
    startedRef.current = true;
    void openDatabaseFile(db, []).catch(() => {
      // Failure already logged by useOpenDatabaseFile; the welcome screen stays up for a manual retry.
    });
  }, [openDatabaseFile]);
}
