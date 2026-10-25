import { describe, expect, it, vi } from 'vitest';
import { MAX_IDS_PER_REQUEST, PlayerNameDirectory, RETRY_MS } from '../src/data/player-names';

/** A fetch answering `body` (or failing when it is `null`), recording every URL asked. */
function server(body: Record<string, string> | null) {
  const urls: string[] = [];
  const fetcher = vi.fn((url: string) => {
    urls.push(url);
    return body === null
      ? Promise.reject(new Error('offline'))
      : Promise.resolve({ ok: true, json: () => Promise.resolve(body) });
  });
  return { fetcher, urls };
}

/** Lets the batched request go out and its answer land. */
async function settle(): Promise<void> {
  await new Promise((r) => setTimeout(r, 0));
  await new Promise((r) => setTimeout(r, 0));
}

describe('PlayerNameDirectory', () => {
  it('asks once, in one request, for every id met in a frame, and answers from the cache after', async () => {
    const { fetcher, urls } = server({ '3': 'leia', '7': 'han' });
    const names = new PlayerNameDirectory(fetcher);
    expect(names.nameOf(3)).toBeNull();
    expect(names.nameOf(7)).toBeNull();
    expect(names.nameOf(3)).toBeNull();
    await settle();

    expect(urls).toEqual(['typhon/names?ids=3,7']);
    expect(names.nameOf(3)).toBe('leia');
    expect(names.nameOf(7)).toBe('han');
    expect(urls).toHaveLength(1);
  });

  it('bumps its version when names arrive, so a label built before knows to be rebuilt', async () => {
    const { fetcher } = server({ '3': 'leia' });
    const names = new PlayerNameDirectory(fetcher);
    const before = names.version;
    names.nameOf(3);
    await settle();
    expect(names.version).toBeGreaterThan(before);
  });

  it('remembers an id the server left out as nameless, and never asks about it again', async () => {
    const { fetcher, urls } = server({});
    const names = new PlayerNameDirectory(fetcher);
    names.nameOf(9);
    await settle();
    names.nameOf(9);
    await settle();
    expect(names.nameOf(9)).toBeNull();
    expect(urls).toHaveLength(1);
  });

  it('never asks about 0, which is "no name"', async () => {
    const { fetcher, urls } = server({});
    const names = new PlayerNameDirectory(fetcher);
    expect(names.nameOf(0)).toBeNull();
    await settle();
    expect(urls).toHaveLength(0);
  });

  it('retries a failed request, but not before the retry delay', async () => {
    let now = 0;
    const offline = server(null);
    const names = new PlayerNameDirectory(offline.fetcher, () => now);
    names.nameOf(4);
    await settle();
    names.nameOf(4);
    await settle();
    expect(offline.urls).toHaveLength(1);

    now = RETRY_MS;
    names.nameOf(4);
    await settle();
    expect(offline.urls).toHaveLength(2);
  });

  it('splits a frame that met more ids than one request may carry', async () => {
    const { fetcher, urls } = server({});
    const names = new PlayerNameDirectory(fetcher);
    for (let id = 1; id <= MAX_IDS_PER_REQUEST + 5; id++) {
      names.nameOf(id);
    }

    await settle();
    await settle();
    expect(urls).toHaveLength(2);
    expect(urls[1]).toBe(`typhon/names?ids=${[1, 2, 3, 4, 5].map((i) => i + MAX_IDS_PER_REQUEST).join(',')}`);
  });

  it('drops an answer to a request from before it was cleared: a new session may be another server', async () => {
    const { fetcher } = server({ '3': 'leia' });
    const names = new PlayerNameDirectory(fetcher);
    names.nameOf(3);
    names.clear();
    await settle();
    expect(names.nameOf(3)).toBeNull();
  });
});
