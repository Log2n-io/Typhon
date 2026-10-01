import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { customFetch } from '@/api/client';
import { useSessionStore } from '@/stores/useSessionStore';
import type { Realm, RealmList } from './types';

interface Envelope<T> {
  data: T;
  status: number;
  headers: Headers;
}

/**
 * The realm **catalog** of the current session: what worlds this database holds (#1083).
 *
 * **Gated on the `realms` capability, not on the session kind.** A single-realm database does not have it and the route
 * answers 409 there, so asking anyway would be a guaranteed round trip to a refusal on the most common kind of database.
 * The capability is the server's own statement that there is something to show.
 *
 * **And on `database` as well, which is not the derivation WB-01 warned against.** That warning was about deriving the
 * *availability of realms* from `database` — it would hide the live realm board in attach, the mode that most needs it.
 * This is the narrower and literally true statement that *the catalog* is read out of the file: an attach session has
 * the realms capability and no file, so its rows arrive on the trace stream instead and the board reads them there.
 * Without this the chip and the picker would fetch a guaranteed 409 on every live session.
 */
export function useRealmList() {
  const sessionId = useSessionStore((s) => s.sessionId);
  const capabilities = useSessionStore((s) => s.capabilities);
  const hasRealms = !!capabilities?.includes('realms');
  const hasCatalog = hasRealms && !!capabilities?.includes('database');

  const query = useQuery({
    queryKey: ['realms', sessionId],
    enabled: !!sessionId && hasCatalog,
    // The catalog is fixed for the life of an open session — realms register and unregister at run time, but a database
    // opened as a file is not running. The live half will want a much shorter window when it lands (rung 6).
    staleTime: 30_000,
    queryFn: () =>
      customFetch<Envelope<RealmList> | Envelope<undefined>>(`/api/sessions/${sessionId}/realms`, { method: 'GET' }),
  });

  const list: Realm[] = useMemo(() => query.data?.data?.realms ?? [], [query.data]);

  return {
    /** One row per realm, ascending by id. Empty until loaded, and empty for a session without the capability. */
    list,
    /** The whole document, for the coverage fields a panel shows instead of a zero. */
    catalog: query.data?.data,
    /** Whether this session offers realms at all. False means "not a realm database", not "still loading". */
    hasRealms,
    /** Whether this session can serve the catalog — realms *and* a browsable file. False on a live attach session. */
    hasCatalog,
    isLoading: query.isLoading,
    error: query.error,
  };
}
