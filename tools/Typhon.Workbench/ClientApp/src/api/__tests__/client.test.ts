import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { customFetch, FetchError } from '../client';

// Stub the session store before importing client — the module reads the token at call time via
// `useSessionStore.getState()`, so a vi.mock on the store keeps the test self-contained.
vi.mock('@/stores/useSessionStore', () => ({
  useSessionStore: {
    getState: () => ({ token: null }),
  },
}));

// Control the bootstrap token the client reads (#429). Hoisted so the vi.mock factory can reference it.
const { getBootstrapTokenMock } = vi.hoisted(() => ({
  getBootstrapTokenMock: vi.fn<() => string | null>(() => null),
}));
// customFetch delegates header-stamping to applyWorkbenchAuthHeaders; the mock reproduces its (small) contract
// over the mocked getBootstrapToken. The helper's own behavior is tested authoritatively in bootstrapToken.test.ts.
vi.mock('@/api/bootstrapToken', () => ({
  getBootstrapToken: getBootstrapTokenMock,
  applyWorkbenchAuthHeaders: (headers: Headers, sessionToken?: string | null) => {
    const bootstrapToken = getBootstrapTokenMock();
    if (bootstrapToken && !headers.has('X-Workbench-Token')) {
      headers.set('X-Workbench-Token', bootstrapToken);
    }
    if (sessionToken && !headers.has('X-Session-Token')) {
      headers.set('X-Session-Token', sessionToken);
    }
    headers.set('X-Workbench-Api', '1');
    return headers;
  },
}));

interface FetchInit {
  method?: string;
  headers?: HeadersInit;
  body?: BodyInit;
}

describe('customFetch', () => {
  const realFetch = globalThis.fetch;

  beforeEach(() => {
    vi.restoreAllMocks();
  });

  afterEach(() => {
    globalThis.fetch = realFetch;
  });

  function stubFetch(response: Response) {
    globalThis.fetch = vi.fn((_url: string, _init?: FetchInit) =>
      Promise.resolve(response),
    ) as unknown as typeof fetch;
  }

  it('returns envelope on 200 with parsed JSON body', async () => {
    stubFetch(
      new Response(JSON.stringify({ hello: 'world' }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );
    const env = await customFetch<{ data: { hello: string }; status: number; headers: Headers }>(
      '/api/anything',
    );
    expect(env.status).toBe(200);
    expect(env.data).toEqual({ hello: 'world' });
  });

  it('returns envelope with data:undefined on 202 (build in progress)', async () => {
    // 202 with empty body is the trace-cache-building signal. customFetch must not throw and not
    // attempt JSON.parse on an empty body (would raise SyntaxError) — schema hooks key off
    // `envelope.data === undefined` to schedule a refetch.
    stubFetch(new Response(null, { status: 202 }));
    const env = await customFetch<{ data: unknown; status: number; headers: Headers }>(
      '/api/anything',
    );
    expect(env.status).toBe(202);
    expect(env.data).toBeUndefined();
  });

  it('returns envelope with data:undefined on 204 (no content)', async () => {
    stubFetch(new Response(null, { status: 204 }));
    const env = await customFetch<{ data: unknown; status: number; headers: Headers }>(
      '/api/anything',
    );
    expect(env.status).toBe(204);
    expect(env.data).toBeUndefined();
  });

  it('throws FetchError with ProblemDetails fields on 404', async () => {
    // RFC 7807 ProblemDetails — the most common error envelope across the Workbench API.
    // The thrown error must expose `title`/`detail` via its message + `.problem` so the global
    // query-cache logger surfaces something better than [object Object].
    stubFetch(
      new Response(
        JSON.stringify({
          title: 'schema_unavailable',
          detail: 'Session 123 has no schema data available.',
          status: 404,
        }),
        { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    );

    let caught: unknown;
    try {
      await customFetch('/api/anything');
    } catch (e) {
      caught = e;
    }
    expect(caught).toBeInstanceOf(FetchError);
    const err = caught as FetchError;
    expect(err.status).toBe(404);
    expect(err.message).toBe('Session 123 has no schema data available.');
    expect(err.problem.title).toBe('schema_unavailable');
    // Most importantly: a global error logger that calls `String(err)` or accesses `err.message`
    // gets a real string, not `[object Object]`.
    expect(String(err)).not.toContain('[object Object]');
  });

  it('throws FetchError with status fallback on non-JSON error body', async () => {
    stubFetch(new Response('plain text gateway error', { status: 502 }));
    let caught: unknown;
    try {
      await customFetch('/api/anything');
    } catch (e) {
      caught = e;
    }
    expect(caught).toBeInstanceOf(FetchError);
    expect((caught as FetchError).status).toBe(502);
    // No title/detail — synthetic problem `{ status: 502 }` → message falls back to `HTTP 502`.
    expect((caught as FetchError).message).toBe('HTTP 502');
  });

  // Capturing variant of stubFetch: records the RequestInit so a test can assert on the outgoing headers.
  function stubFetchCapturing(response: Response): () => RequestInit | undefined {
    let captured: RequestInit | undefined;
    globalThis.fetch = vi.fn((_url: string, init?: RequestInit) => {
      captured = init;
      return Promise.resolve(response);
    }) as unknown as typeof fetch;
    return () => captured;
  }

  it('attaches X-Workbench-Token when a bootstrap token is present (typhon ui)', async () => {
    getBootstrapTokenMock.mockReturnValue('abc123');
    const init = stubFetchCapturing(
      new Response(JSON.stringify({}), { status: 200, headers: { 'Content-Type': 'application/json' } }),
    );

    await customFetch('/api/anything');

    const headers = new Headers(init()?.headers);
    expect(headers.get('X-Workbench-Token')).toBe('abc123');
  });

  it('omits X-Workbench-Token when no bootstrap token (Vite dev-proxy injects it server-side)', async () => {
    getBootstrapTokenMock.mockReturnValue(null);
    const init = stubFetchCapturing(
      new Response(JSON.stringify({}), { status: 200, headers: { 'Content-Type': 'application/json' } }),
    );

    await customFetch('/api/anything');

    const headers = new Headers(init()?.headers);
    expect(headers.has('X-Workbench-Token')).toBe(false);
  });
});
