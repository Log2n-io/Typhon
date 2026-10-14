import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { forget, logIn, signUp, storedGrant } from '../src/data/auth';

/**
 * `auth.ts` is pure over `fetch` and `sessionStorage`, which is why it is worth testing directly: every interesting failure here is
 * a server or a storage saying something unexpected, and both are one stub away.
 *
 * The suite's environment is `node`, so there is no `sessionStorage` to borrow. Stubbed in memory below rather than pulling in jsdom
 * for one API with four methods — and the stub is the real contract: keys are strings, values are strings, a missing key is null.
 */

/** The three methods `auth.ts` uses, over a Map. */
function memoryStorage(): Storage {
  const entries = new Map<string, string>();
  return {
    get length() {
      return entries.size;
    },
    clear: () => {
      entries.clear();
    },
    getItem: (key: string) => entries.get(key) ?? null,
    key: (index: number) => [...entries.keys()][index] ?? null,
    removeItem: (key: string) => {
      entries.delete(key);
    },
    setItem: (key: string, value: string) => {
      entries.set(key, value);
    },
  };
}

vi.stubGlobal('sessionStorage', memoryStorage());

/** A `fetch` that answers once with this status and body. */
function answers(status: number, body: unknown): typeof fetch {
  return vi.fn(() =>
    Promise.resolve({
      ok: status >= 200 && status < 300,
      status,
      json: () => Promise.resolve(body),
    } as Response),
  );
}

const goodGrant = { token: 'abc.def', name: 'han', expiresInSeconds: 7200 };

beforeEach(() => {
  sessionStorage.clear();
});

afterEach(() => {
  // NOT restoreAllMocks: that would take the sessionStorage stub with it, and the next case would have no storage at all.
  vi.unstubAllEnvs();
  sessionStorage.clear();
});

describe('logIn', () => {
  it('returns the grant and remembers it', async () => {
    vi.stubGlobal('fetch', answers(200, goodGrant));

    const result = await logIn('Han', 'secret');
    expect(result.ok).toBe(true);
    expect(result.ok && result.grant.token).toBe('abc.def');

    // And it survives a reload of the same tab.
    expect(storedGrant()?.token).toBe('abc.def');
  });

  it('passes the server its own sentence on a refusal', async () => {
    vi.stubGlobal('fetch', answers(401, { error: 'that name and password do not match' }));

    const result = await logIn('han', 'wrong');
    expect(result.ok).toBe(false);
    expect(!result.ok && result.refusal.error).toBe('that name and password do not match');
    expect(!result.ok && result.refusal.status).toBe(401);
    expect(storedGrant()).toBeNull();
  });

  it('reports a status when the body is not the server speaking', async () => {
    // What a proxy's HTML error page looks like: a non-2xx whose body is not the server's own refusal shape.
    vi.stubGlobal('fetch', answers(502, 'not json at all'));

    const result = await logIn('han', 'secret');
    expect(result.ok).toBe(false);
    expect(!result.ok && result.refusal.status).toBe(502);
  });

  it('turns a network failure into a refusal rather than a throw', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.reject(new TypeError('failed to fetch'))),
    );

    const result = await logIn('han', 'secret');
    expect(result.ok).toBe(false);
    expect(!result.ok && result.refusal.status).toBe(0);
  });

  /**
   * The case that mattered: a 200 whose body is not a grant. Cast rather than checked, every field failed silently downstream — a
   * missing `token` is how the viewport spells GOD, so the client connected with no account while the UI said it was logged in.
   */
  it.each([
    ['no token', { name: 'han', expiresInSeconds: 7200 }],
    ['an empty token', { token: '', name: 'han', expiresInSeconds: 7200 }],
    ['a non-string token', { token: 42, name: 'han', expiresInSeconds: 7200 }],
    ['no expiry', { token: 'abc', name: 'han' }],
    ['a non-finite expiry', { token: 'abc', name: 'han', expiresInSeconds: Number.NaN }],
    ['no name', { token: 'abc', expiresInSeconds: 7200 }],
    ['not an object', 'a string'],
    ['null', null],
  ])('refuses a 200 with %s rather than logging in', async (_label, body) => {
    vi.stubGlobal('fetch', answers(200, body));

    const result = await logIn('han', 'secret');
    expect(result.ok).toBe(false);
    expect(storedGrant()).toBeNull();
  });
});

describe('signUp', () => {
  it('posts to the signup endpoint', async () => {
    const fetching = answers(200, goodGrant);
    vi.stubGlobal('fetch', fetching);

    await signUp('han', 'secret');
    expect(fetching).toHaveBeenCalledWith('/auth/signup', expect.objectContaining({ method: 'POST' }));
  });

  it('never puts the password in the URL', async () => {
    const fetching = answers(200, goodGrant);
    vi.stubGlobal('fetch', fetching);

    await signUp('han', 'hunter2');
    const [url, init] = (fetching as unknown as { mock: { calls: [string, RequestInit][] } }).mock.calls[0];
    expect(url).not.toContain('hunter2');

    // The body is the JSON string this module built, so a string check is the whole assertion: it is in the body and not the URL.
    expect(typeof init.body).toBe('string');
    expect(init.body as string).toContain('hunter2');
  });
});

describe('storedGrant', () => {
  it('is null when nothing was stored', () => {
    expect(storedGrant()).toBeNull();
  });

  it('discards a token that expired while the tab was closed', () => {
    sessionStorage.setItem('swg.auth', JSON.stringify({ token: 'abc', name: 'han', expiresAtMs: Date.now() - 1000 }));
    expect(storedGrant()).toBeNull();
    expect(sessionStorage.getItem('swg.auth')).toBeNull();
  });

  it('discards a token too close to expiry to be worth opening a socket with', () => {
    sessionStorage.setItem('swg.auth', JSON.stringify({ token: 'abc', name: 'han', expiresAtMs: Date.now() + 5_000 }));
    expect(storedGrant()).toBeNull();
  });

  /**
   * A stamp that is not a finite number cannot be rejected by the expiry comparison — every comparison against NaN is false — so
   * without its own check a corrupted entry read as a grant that never expires.
   */
  it.each([
    ['a missing stamp', { token: 'abc', name: 'han' }],
    ['a NaN stamp', { token: 'abc', name: 'han', expiresAtMs: Number.NaN }],
    ['a string stamp', { token: 'abc', name: 'han', expiresAtMs: 'soon' }],
  ])('discards an entry with %s', (_label, stored) => {
    sessionStorage.setItem('swg.auth', JSON.stringify(stored));
    expect(storedGrant()).toBeNull();
  });

  it('survives another writer putting rubbish under the key', () => {
    sessionStorage.setItem('swg.auth', 'not json');
    expect(storedGrant()).toBeNull();
  });
});

describe('forget', () => {
  it('drops the grant', () => {
    sessionStorage.setItem(
      'swg.auth',
      JSON.stringify({ token: 'abc', name: 'han', expiresAtMs: Date.now() + 7_200_000 }),
    );
    expect(storedGrant()).not.toBeNull();

    forget();
    expect(storedGrant()).toBeNull();
  });
});
