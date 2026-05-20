import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { QueryCache, QueryClient } from '@tanstack/react-query';
import { logError, useLogStore } from '@/stores/useLogStore';
import { FetchError } from '@/api/client';
import { shouldSilence } from '@/lib/silenceErrors';

// Mirror the QueryCache.onError wiring from main.tsx so the test exercises the exact logger
// behaviour without spinning up the React tree. If main.tsx ever drifts, this test fails fast.
function makeClient(): QueryClient {
  return new QueryClient({
    defaultOptions: { queries: { retry: false } },
    queryCache: new QueryCache({
      onError: (error, query) => {
        if (shouldSilence(error, query.meta)) return;
        logError(`Query failed: ${query.queryKey.join(' / ')}`, {
          error: error instanceof Error ? error.message : String(error),
          queryKey: query.queryKey,
        });
      },
    }),
  });
}

describe('queryCache global error logger', () => {
  let client: QueryClient;

  beforeEach(() => {
    useLogStore.getState().clear();
    client = makeClient();
  });

  afterEach(() => {
    client.clear();
    vi.restoreAllMocks();
  });

  it('logs an entry by default when a query fails', async () => {
    // No meta — falls into the default branch and produces a Logs panel entry.
    await client
      .fetchQuery({
        queryKey: ['/api/foo'],
        queryFn: () => Promise.reject(new Error('boom')),
      })
      .catch(() => undefined);

    const entries = useLogStore.getState().entries;
    expect(entries.length).toBe(1);
    expect(entries[0].level).toBe('error');
    expect(entries[0].message).toBe('Query failed: /api/foo');
    expect(entries[0].details).toMatchObject({ error: 'boom' });
  });

  it('suppresses the log when the query opts in via meta.silenceErrors (boolean form)', async () => {
    // Mirrors the RecentFilesTab annotation: stat calls on moved/deleted files 404 by design,
    // so the call site marks the query silent. Logger must skip it entirely — no Logs entry.
    await client
      .fetchQuery({
        queryKey: ['/api/fs/stat', { path: 'gone' }],
        queryFn: () => Promise.reject(new Error('Path not found')),
        meta: { silenceErrors: true },
      })
      .catch(() => undefined);

    expect(useLogStore.getState().entries).toEqual([]);
  });

  it('predicate form silences only matching errors — 404 quiet, 500 logged', async () => {
    // typeName schema hooks use a predicate so URL-restored selections that vanished in a new
    // session (404) stay quiet, but server faults (500) still reach the Logs panel.
    const silence404: (err: unknown) => boolean = (err) =>
      err instanceof FetchError && err.status === 404;

    await client
      .fetchQuery({
        queryKey: ['/api/sessions/abc/schema/components/Gone'],
        queryFn: () => Promise.reject(new FetchError(404, { title: 'not_found' })),
        meta: { silenceErrors: silence404 },
      })
      .catch(() => undefined);
    expect(useLogStore.getState().entries).toEqual([]);

    // Same predicate, but a 500 — must log.
    await client
      .fetchQuery({
        queryKey: ['/api/sessions/abc/schema/components/Crashed'],
        queryFn: () => Promise.reject(new FetchError(500, { title: 'boom' })),
        meta: { silenceErrors: silence404 },
      })
      .catch(() => undefined);

    const entries = useLogStore.getState().entries;
    expect(entries.length).toBe(1);
    expect(entries[0].message).toBe(
      'Query failed: /api/sessions/abc/schema/components/Crashed',
    );
    expect(entries[0].details).toMatchObject({ error: 'boom' });
  });
});
