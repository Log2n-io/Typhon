import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AssetLibrary } from '../src/assets/asset-library';
import { fetchServerConfig } from '../src/data/server-config';

/**
 * S1 — what the server announces about its asset set, and what the library does with it.
 *
 * The announcement is parsed input: the base becomes a URL every GLB is fetched from and the catalogue name becomes a
 * file name, so a field this page cannot stand behind has to read as "no assets" rather than as a slightly wrong one.
 */

const CATALOGUE = {
  schema: 1,
  version: '5cc29ba99154',
  assets: {
    tat_house_small: { rev: 4, descriptor: 'tat_house_small.aaaa.json', glb: 'tat_house_small.bbbb.glb' },
  },
  textures: ['sandstone_basecolor.cccc.ktx2'],
};

const DEMO = {
  godRegionMaxEdgeM: 4096,
  maxViewRadiusM: 2048,
  playerRadiusM: 192,
  playerMaxRadiusM: 1280,
  assets: { base: '/assets/', catalogue: 'catalogue.5cc29ba99154.json' },
};

/** A fetch that answers one body for everything, so a test says what it is testing and nothing else. */
function answering(body: unknown, ok = true, status = 200): typeof fetch {
  return vi.fn(() =>
    Promise.resolve({
      ok,
      status,
      json: () => Promise.resolve(body),
    }),
  ) as unknown as typeof fetch;
}

describe('the announced asset set', () => {
  const original = globalThis.fetch;

  beforeEach(() => {
    vi.restoreAllMocks();
  });

  afterEach(() => {
    globalThis.fetch = original;
  });

  it('is read from demo.json', async () => {
    globalThis.fetch = answering(DEMO);
    const config = await fetchServerConfig();
    // `structures` is the server's asset table: a structure replicates an INDEX into it, so the client never derives
    // which building it is looking at. A server too old to publish it yields an empty list, read everywhere as "no
    // asset", which draws the renderer's own shape.
    // `collision` is the geometry the server pushes bodies out of, published so the client predicts the same thing
    // rather than re-deriving it from descriptors it would also have to filter.
    expect(config?.assets).toEqual({
      base: '/assets/',
      catalogue: 'catalogue.5cc29ba99154.json',
      structures: [],
      collision: [],
    });
  });

  it('is absent from a server that serves none, which is a working run', async () => {
    globalThis.fetch = answering({ godRegionMaxEdgeM: 4096, maxViewRadiusM: 2048 });
    const config = await fetchServerConfig();
    expect(config).not.toBeNull();
    expect(config?.assets).toBeNull();
  });

  it('is refused when the base is not a path on this origin', async () => {
    for (const base of [
      'https://cdn.example.com/assets/',
      '//cdn.example.com/assets/',
      'assets/',
      '/assets',
      '/assets/../',
      '',
    ]) {
      globalThis.fetch = answering({ ...DEMO, assets: { ...DEMO.assets, base } });
      const config = await fetchServerConfig();
      expect(config?.assets, `base ${JSON.stringify(base)}`).toBeNull();
    }
  });

  it('is refused when the catalogue is not an immutable catalogue name', async () => {
    // The pointer is the interesting rejection: it is a real file the server serves, and reading it would put this page
    // on a version that can change under it while the server stays pinned to the one it read at startup.
    for (const catalogue of [
      'catalogue.json',
      'catalogue..json',
      '../catalogue.abc.json',
      'catalogue.abc.json.br',
      '',
    ]) {
      globalThis.fetch = answering({ ...DEMO, assets: { ...DEMO.assets, catalogue } });
      const config = await fetchServerConfig();
      expect(config?.assets, `catalogue ${JSON.stringify(catalogue)}`).toBeNull();
    }
  });

  it('is refused when a field is missing or the wrong type', async () => {
    for (const assets of [
      {},
      { base: '/assets/' },
      { catalogue: 'catalogue.ab.json' },
      { base: 1, catalogue: 2 },
      null,
      'x',
    ]) {
      globalThis.fetch = answering({ ...DEMO, assets });
      const config = await fetchServerConfig();
      expect(config?.assets, JSON.stringify(assets)).toBeNull();
    }
  });
});

describe('the asset library', () => {
  const original = globalThis.fetch;

  afterEach(() => {
    globalThis.fetch = original;
  });

  it('fetches the catalogue the server pinned, from the announced base', async () => {
    const calls: string[] = [];
    globalThis.fetch = vi.fn((url: string) => {
      calls.push(url);
      return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(CATALOGUE) } as Response);
    }) as unknown as typeof fetch;

    // The scene is never touched by `open`, which is why this runs in a node environment with no engine at all.
    const library = await AssetLibrary.open(null as never, '/assets/', 'catalogue.5cc29ba99154.json');
    expect(calls).toEqual(['/assets/catalogue.5cc29ba99154.json']);
    expect(library.version).toBe('5cc29ba99154');
    expect(library.ids).toEqual(['tat_house_small']);
  });

  it('refuses a catalogue schema it does not read', async () => {
    globalThis.fetch = answering({ ...CATALOGUE, schema: 2 });
    await expect(AssetLibrary.open(null as never, '/assets/', 'catalogue.ab.json')).rejects.toThrow('schema 2');
  });

  it('reports the HTTP status when the catalogue is not there', async () => {
    globalThis.fetch = answering(null, false, 404);
    await expect(AssetLibrary.open(null as never, '/assets/', 'catalogue.ab.json')).rejects.toThrow('HTTP 404');
  });

  it('fetches a descriptor once, however many callers ask for it', async () => {
    const descriptor = { schema: 1, id: 'tat_house_small', kind: 'building', render: { triangles: 2326 } };
    let catalogues = 0;
    let descriptors = 0;
    globalThis.fetch = vi.fn((url: string) => {
      const body = url.endsWith('.json') && url.includes('house') ? descriptor : CATALOGUE;
      if (body === CATALOGUE) {
        catalogues++;
      } else {
        descriptors++;
      }

      return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) } as Response);
    }) as unknown as typeof fetch;

    const library = await AssetLibrary.open(null as never, '/assets/', 'catalogue.5cc29ba99154.json');
    const [a, b] = await Promise.all([library.describe('tat_house_small'), library.describe('tat_house_small')]);
    expect(a).toBe(b);
    expect(descriptors).toBe(1);
    expect(catalogues).toBe(1);
  });

  it('names an asset the catalogue does not publish', async () => {
    globalThis.fetch = answering(CATALOGUE);
    const library = await AssetLibrary.open(null as never, '/assets/', 'catalogue.5cc29ba99154.json');
    await expect(library.describe('tat_shuttleport')).rejects.toThrow('not in catalogue 5cc29ba99154');
  });
});
