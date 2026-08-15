import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { customFetch } from '@/api/client';
import type { KnownDatabaseList } from '@/libs/databases/knownDatabases';

interface Envelope<T> {
  data: T;
  status: number;
  headers: Headers;
}

const QUERY_KEY = ['knownDatabases'] as const;

/**
 * The machine-local database registry (#622, design D-7) — every database any Typhon process on this machine has
 * opened, whether or not the Workbench was the one that opened it.
 *
 * **Deliberately session-free.** This is how you *find* a database, so it has to answer before a session exists; the
 * route sits beside `/api/fs` rather than under `/api/sessions/{id}` for the same reason. `enabled` lets the caller
 * hold the request until its tab is actually shown, so opening the Connect dialog on another tab costs nothing.
 *
 * Both mutations return the refreshed list, which is written straight into the cache. That is not just a saved
 * round-trip: it removes the window where the UI still shows a row the server has already dropped.
 */
export function useKnownDatabases(enabled = true) {
  const queryClient = useQueryClient();

  const query = useQuery({
    queryKey: QUERY_KEY,
    enabled,
    // A directory listing of small files — cheap, but not worth re-reading on every focus change while a dialog is open.
    staleTime: 5_000,
    queryFn: async () => {
      const res = await customFetch<Envelope<KnownDatabaseList>>('/api/databases', { method: 'GET' });
      return res.data;
    },
  });

  const forget = useMutation({
    mutationFn: async (bundlePath: string) => {
      const res = await customFetch<Envelope<KnownDatabaseList>>(
        `/api/databases?path=${encodeURIComponent(bundlePath)}`,
        { method: 'DELETE' },
      );
      return res.data;
    },
    onSuccess: (list) => queryClient.setQueryData(QUERY_KEY, list),
  });

  const pruneMissing = useMutation({
    mutationFn: async () => {
      const res = await customFetch<Envelope<KnownDatabaseList>>('/api/databases/prune', { method: 'POST' });
      return res.data;
    },
    onSuccess: (list) => queryClient.setQueryData(QUERY_KEY, list),
  });

  return {
    list: query.data ?? null,
    isLoading: query.isLoading,
    isError: query.isError,
    error: query.error,
    forget,
    pruneMissing,
  };
}
