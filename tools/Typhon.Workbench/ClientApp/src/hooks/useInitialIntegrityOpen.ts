import { useEffect, useRef } from 'react';
import { getInitialIntegrityPath } from '@/api/bootstrapToken';
import { openIntegrity } from '@/shell/commands/openIntegrity';

/**
 * On first mount, if the launch URL carried `integrity=<bundle>`, open the Integrity view on it.
 *
 * The sibling of {@link useInitialDbAutoOpen}, with one deliberate difference: it does **not** open a session.
 * `db=` means "open this database"; `integrity=` means "look at this file without opening it". Auto-opening a
 * session here would take the exclusive lock — blocking the repair the operator arrived to perform, and
 * failing outright on the database that would not open, which is the whole reason for arriving this way.
 *
 * Runs exactly once, guarded against StrictMode's double-invoke.
 */
export function useInitialIntegrityOpen(): void {
  const startedRef = useRef(false);

  useEffect(() => {
    if (startedRef.current) {
      return;
    }
    const bundle = getInitialIntegrityPath();
    if (!bundle) {
      return;
    }
    startedRef.current = true;
    openIntegrity(bundle);
  }, []);
}
