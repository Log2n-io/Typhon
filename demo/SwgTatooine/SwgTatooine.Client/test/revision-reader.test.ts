import { describe, expect, it } from 'vitest';
import { REVISION_RETRY_MS, RevisionReader } from '../src/data/revision-reader';

/**
 * The owner read behind a revision (inventory-loot.md § 6): the wallet's rules, extracted so the inventory reads the same
 * way. The wallet's own cases stay in `wallet.test.ts`; these pin the reader on a path and a parser of its own.
 */

interface Answer {
  readonly n: number;
}

const parseAnswer = (raw: unknown): Answer | null =>
  typeof raw === 'object' && raw !== null && typeof (raw as Record<string, unknown>).n === 'number'
    ? { n: (raw as { n: number }).n }
    : null;

const harness = (answers: unknown[], token: () => string | null = () => 'tok') => {
  let clock = 0;
  const urls: { url: string; auth: string | undefined }[] = [];
  let resolveNext: (() => void) | null = null;
  const reader = new RevisionReader<Answer>(
    'swg/thing',
    parseAnswer,
    token,
    (url, init) => {
      urls.push({ url, auth: init.headers['Authorization'] });
      const next = answers.shift();
      return new Promise((resolve) => {
        resolveNext = () => {
          resolve(
            next === 'fail'
              ? { ok: false, status: 500, json: () => Promise.resolve(null) }
              : next === 401
                ? { ok: false, status: 401, json: () => Promise.resolve(null) }
                : { ok: true, status: 200, json: () => Promise.resolve(next) },
          );
        };
      });
    },
    () => clock,
  );
  const settle = async (): Promise<void> => {
    const r = resolveNext;
    resolveNext = null;
    r?.();
    for (let i = 0; i < 5; i++) {
      await Promise.resolve();
    }
  };
  return { reader, urls, settle, advance: (ms: number) => (clock += ms) };
};

describe('a revision reader', () => {
  it('reads its own path on a moved revision, with the bearer token, and parses the answer', async () => {
    const h = harness([{ n: 7 }, { n: 8 }]);
    h.reader.update(Number.NaN);
    expect(h.urls).toHaveLength(0);
    h.reader.update(3);
    expect(h.urls).toEqual([{ url: 'swg/thing', auth: 'Bearer tok' }]);
    await h.settle();
    expect(h.reader.value).toEqual({ n: 7 });
    h.reader.update(3);
    expect(h.urls).toHaveLength(1);
    h.reader.update(4);
    await h.settle();
    expect(h.reader.value).toEqual({ n: 8 });
  });

  it('takes an answer its parser refuses as a failed read, retried after the delay', async () => {
    const h = harness([{ wrong: true }, { n: 1 }]);
    h.reader.update(1);
    await h.settle();
    expect(h.reader.value).toBeNull();
    h.reader.update(1);
    expect(h.urls).toHaveLength(1);
    h.advance(REVISION_RETRY_MS);
    h.reader.update(1);
    await h.settle();
    expect(h.reader.value).toEqual({ n: 1 });
  });

  it('does not read without a token, and reads a refused token again only once the token changed', async () => {
    let token: string | null = null;
    const h = harness([401, { n: 2 }], () => token);
    h.reader.update(1);
    expect(h.urls).toHaveLength(0);
    token = 'old';
    h.reader.update(1);
    await h.settle();
    h.advance(REVISION_RETRY_MS * 10);
    h.reader.update(1);
    expect(h.urls).toHaveLength(1);
    token = 'new';
    h.reader.update(1);
    await h.settle();
    expect(h.urls.map((u) => u.auth)).toEqual(['Bearer old', 'Bearer new']);
    expect(h.reader.value).toEqual({ n: 2 });
  });

  it('forgets on clear, bumps its version, and drops an answer that lands after it', async () => {
    const h = harness([{ n: 5 }]);
    h.reader.update(1);
    const version = h.reader.version;
    h.reader.clear();
    expect(h.reader.version).toBe(version + 1);
    await h.settle();
    expect(h.reader.value).toBeNull();
    expect(h.reader.version).toBe(version + 1);
  });
});
