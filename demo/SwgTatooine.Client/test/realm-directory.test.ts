import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fetchServerConfig } from '../src/data/server-config';

/**
 * The exact document a running server serves, captured from one.
 *
 * <b>Copied from a real response rather than composed.</b> `demo.json` is written by hand on the server side with a
 * `StringBuilder` and no serializer, so the only thing that can prove the two agree is a body that came off the wire:
 * a fixture built from the parser's own expectations would pass against a server that had stopped emitting the field.
 * Regenerate by starting the demo with these flags and fetching the document.
 *
 * `--planets 3 --interiors --space --dungeons 2`
 */
const LIVE_BODY =
  '{"godRegionMaxEdgeM":0,"maxViewRadiusM":0,"realms":{"planets":[{"id":0,"appTag":0},{"id":1,"appTag":1048577},' +
  '{"id":2,"appTag":2097154}],"interiors":{"first":3,"perPlanet":617},"space":{"id":1854,"appTag":536870912},' +
  '"dungeons":{"first":1855,"count":2}}}';

function serve(body: string, ok = true): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve({
        ok,
        json: () => Promise.resolve(JSON.parse(body) as unknown),
      }),
    ),
  );
}

beforeEach(() => {
  vi.unstubAllGlobals();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('the realm directory, against a document a real server served', () => {
  it('reads every planet with the tag the server registered it under', async () => {
    serve(LIVE_BODY);
    const config = await fetchServerConfig();
    expect(config?.realms?.planets).toEqual([
      { id: 0, appTag: 0x00000000 },
      { id: 1, appTag: 0x00100001 },
      { id: 2, appTag: 0x00200002 },
    ]);
  });

  it('reads space, and the interior layout rather than a list of interiors', async () => {
    serve(LIVE_BODY);
    const config = await fetchServerConfig();
    expect(config?.realms?.space).toEqual({ id: 1854, appTag: 0x20000000 });
    expect(config?.realms?.interiors).toEqual({ first: 3, perPlanet: 617 });
  });

  it('agrees with the server about where space lands, which is the whole layout in one number', async () => {
    // `first + planets * perPlanet` is the rule the selector applies to reach an interior. If the client's arithmetic
    // and the server's disagree, this is where it shows: space sits immediately after the last interior, so the two
    // published numbers predict the third.
    serve(LIVE_BODY);
    const realms = (await fetchServerConfig())?.realms;
    const planets = realms!.planets.length;
    expect(realms!.interiors.first + planets * realms!.interiors.perPlanet).toBe(realms!.space!.id);
  });

  it('has no dungeons in it, because a dungeon realm exists only while a party is inside one', async () => {
    serve(LIVE_BODY);
    const realms = (await fetchServerConfig())?.realms;
    expect(realms).not.toHaveProperty('dungeons');
  });
});

describe('a directory it cannot trust', () => {
  it('is null from a server that publishes none, leaving the rest of the config readable', async () => {
    serve('{"godRegionMaxEdgeM":4096,"maxViewRadiusM":2048}');
    const config = await fetchServerConfig();
    expect(config?.maxViewRadiusM).toBe(2048);
    expect(config?.realms).toBeNull();
  });

  it('is null when it is INCOMPLETE, rather than half a list', async () => {
    // A selector built from half a directory is worse than one that says it does not know: the missing half is
    // invisible, so a viewer cannot tell a short list from a small world.
    serve('{"godRegionMaxEdgeM":0,"maxViewRadiusM":0,"realms":{"planets":[{"id":0,"appTag":0}]}}');
    expect((await fetchServerConfig())?.realms).toBeNull();
  });

  it('is null when the interior layout is PRESENT but not a pair of numbers', async () => {
    // Distinct from the case above, and it has to be: an absent `interiors` is caught by the object check alone, so
    // without this the field-level validation is dead code that every test still passes over. Found by deleting it.
    serve('{"godRegionMaxEdgeM":0,"maxViewRadiusM":0,"realms":{"planets":[{"id":0,"appTag":0}],"interiors":{"first":"three","perPlanet":617}}}');
    expect((await fetchServerConfig())?.realms).toBeNull();

    serve('{"godRegionMaxEdgeM":0,"maxViewRadiusM":0,"realms":{"planets":[{"id":0,"appTag":0}],"interiors":{"first":3}}}');
    expect((await fetchServerConfig())?.realms).toBeNull();
  });

  it('is null when a planet entry is malformed', async () => {
    serve('{"godRegionMaxEdgeM":0,"maxViewRadiusM":0,"realms":{"planets":[{"id":"nought"}],"interiors":{"first":1,"perPlanet":0}}}');
    expect((await fetchServerConfig())?.realms).toBeNull();
  });

  it('survives a server with no space realm', async () => {
    serve('{"godRegionMaxEdgeM":0,"maxViewRadiusM":0,"realms":{"planets":[{"id":0,"appTag":0}],"interiors":{"first":1,"perPlanet":0}}}');
    const realms = (await fetchServerConfig())?.realms;
    expect(realms?.space).toBeNull();
    expect(realms?.planets).toHaveLength(1);
  });

  it('never throws on a body that is not JSON at all', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.reject(new Error('offline'))),
    );
    await expect(fetchServerConfig()).resolves.toBeNull();
  });
});
