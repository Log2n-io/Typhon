import { afterEach, describe, expect, it, vi } from 'vitest';
import { fetchRealmInventory, readRealmInventory, realmPanelView } from '../src/data/realm-inventory';

/**
 * A document a running server served, with its row list cut short.
 *
 * <b>Captured from a real response rather than composed</b>, for the reason `realm-directory.test.ts` states: the server
 * writes this by hand with a `StringBuilder` and no serializer, so only a body that came off the wire can prove the two
 * sides agree about field names. A fixture built from this parser's own expectations would keep passing against a server
 * that had stopped emitting a field.
 *
 * The header and every row here are verbatim; the response carried 64 rows and the other 58 are interiors identical in
 * shape to the two kept. `omitted` and `counts` are the real numbers from that run, which is why they do not add up to
 * the six rows below.
 *
 * `--serve 8097 --planets 3 --interiors --space --dungeons 2 --pop 0.05 --hz 20`
 */
const LIVE_BODY =
  '{"tick":122,"censusTick":120,"maxRows":64,"counts":{"active":1,"simulated":1855,"dormant":0,"closing":0,"divided":0},"omitted":1792,' +
  '"realms":[' +
  '{"id":0,"generation":0,"appTag":0,"state":"simulated","divisor":1,"sleepAfterTicks":0,"players":9,"npcs":57,"creatures":518,"structures":1212},' +
  '{"id":1,"generation":0,"appTag":1048577,"state":"simulated","divisor":1,"sleepAfterTicks":0,"players":16,"npcs":57,"creatures":518,"structures":1212},' +
  '{"id":2,"generation":0,"appTag":2097154,"state":"simulated","divisor":1,"sleepAfterTicks":0,"players":16,"npcs":57,"creatures":518,"structures":1212},' +
  '{"id":1854,"generation":0,"appTag":536870912,"state":"simulated","divisor":1,"sleepAfterTicks":0,"players":0,"npcs":0,"creatures":0,"structures":0},' +
  '{"id":3,"generation":0,"appTag":268435456,"state":"simulated","divisor":1,"sleepAfterTicks":200,"players":0,"npcs":3,"creatures":0,"structures":0},' +
  '{"id":4,"generation":0,"appTag":268435457,"state":"simulated","divisor":1,"sleepAfterTicks":200,"players":0,"npcs":3,"creatures":0,"structures":0}' +
  ']}';

function parsed(body: string): unknown {
  return JSON.parse(body) as unknown;
}

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

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('the realm inventory, against a document a real server served', () => {
  it('reads the header a panel puts above the rows', () => {
    const inventory = readRealmInventory(parsed(LIVE_BODY));
    expect(inventory).not.toBeNull();
    expect(inventory?.tick).toBe(122);
    expect(inventory?.maxRows).toBe(64);
    expect(inventory?.omitted).toBe(1792);
    expect(inventory?.counts).toEqual({ active: 1, simulated: 1855, dormant: 0, closing: 0, divided: 0 });
  });

  it('keeps the census tick apart from the state tick, because they are taken at different instants', () => {
    // The states are read when the document is built; the populations come from a walk the server does about once a
    // second. Collapsing the two into one timestamp would be the quiet half of claiming counts are current.
    const inventory = readRealmInventory(parsed(LIVE_BODY));
    expect(inventory?.censusTick).toBe(120);
    expect(inventory?.censusTick).toBeLessThan(inventory?.tick ?? 0);
  });

  it('reads a server that is up and has not counted anything yet', () => {
    // -1 is a real answer and the only negative one: a process that started serving before its first census walk.
    const inventory = readRealmInventory(parsed(LIVE_BODY.replace('"censusTick":120', '"censusTick":-1')));
    expect(inventory?.censusTick).toBe(-1);
  });

  it('reads every field of every row', () => {
    const rows = readRealmInventory(parsed(LIVE_BODY))?.realms ?? [];
    expect(rows).toHaveLength(6);
    expect(rows[0]).toEqual({
      id: 0,
      generation: 0,
      appTag: 0,
      state: 'simulated',
      divisor: 1,
      sleepAfterTicks: 0,
      players: 9,
      npcs: 57,
      creatures: 518,
      structures: 1212,
    });

    // The interior carries the sleep delay the planet does not: a planet never sleeps and says so with 0.
    expect(rows[4]?.sleepAfterTicks).toBe(200);
    expect(rows[0]?.sleepAfterTicks).toBe(0);
  });

  it('carries the realm ids the directory names, so a row and a selector entry are the same realm', () => {
    const rows = readRealmInventory(parsed(LIVE_BODY))?.realms ?? [];
    expect(rows.map((row) => row.id)).toEqual([0, 1, 2, 1854, 3, 4]);
  });
});

describe('the realm inventory, when it cannot be read', () => {
  it('rejects a document whose counts are incomplete', () => {
    expect(readRealmInventory(parsed(LIVE_BODY.replace('"dormant":0,', '')))).toBeNull();
  });

  it('rejects a document with one unreadable row, rather than dropping it', () => {
    // A panel built from half a list is worse than one that says it does not know: the missing half is invisible, so
    // the viewer cannot tell a short list from a small world. Same policy as the realm directory's.
    expect(readRealmInventory(parsed(LIVE_BODY.replace('"players":16,', '')))).toBeNull();
  });

  it('rejects a row in a state it does not know', () => {
    // The panel branches on the name, so an unknown one would draw a realm in no state at all.
    expect(readRealmInventory(parsed(LIVE_BODY.replace('"state":"simulated"', '"state":"snoozing"')))).toBeNull();
  });

  it('rejects a negative count', () => {
    expect(readRealmInventory(parsed(LIVE_BODY.replace('"players":9', '"players":-1')))).toBeNull();
  });

  it('rejects a document that breaks its own row bound', () => {
    // Without this a server bug sending a million rows becomes a million objects and millions of DOM nodes, once a
    // second. The document states the bound; a document that breaks it is not one this client trusts.
    expect(readRealmInventory(parsed(LIVE_BODY.replace('"maxRows":64', '"maxRows":3')))).toBeNull();
    expect(readRealmInventory(parsed(LIVE_BODY.replace('"maxRows":64', '"maxRows":6')))).not.toBeNull();
  });

  it('rejects a document with no counts at all', () => {
    expect(readRealmInventory(parsed(LIVE_BODY.replace(/"counts":\{[^}]*\},/, '')))).toBeNull();
  });

  it('rejects a document whose realms are not an array', () => {
    const body = parsed(LIVE_BODY) as Record<string, unknown>;
    expect(readRealmInventory({ ...body, realms: { 0: {} } })).toBeNull();
    expect(readRealmInventory({ ...body, realms: null })).toBeNull();
  });

  it('rejects anything that is not an object', () => {
    for (const value of [null, undefined, 7, 'realms', [], true]) {
      expect(readRealmInventory(value)).toBeNull();
    }
  });
});

describe('fetching the realm inventory', () => {
  it('reads a document the server serves', async () => {
    serve(LIVE_BODY);
    const inventory = await fetchRealmInventory();
    expect(inventory?.realms).toHaveLength(6);
  });

  it('answers null for a server that does not serve it', async () => {
    serve(LIVE_BODY, false);
    expect(await fetchRealmInventory()).toBeNull();
  });

  it('answers null rather than throwing when the fetch fails', async () => {
    // Polled about once a second for the life of the page, so an unhandled rejection here is not a one-off: a mock
    // session, a page opened from a file, or a poll that raced a shutdown all land on this path.
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.reject(new Error('offline'))),
    );
    expect(await fetchRealmInventory()).toBeNull();
  });

  it('answers null for a body that is not JSON at all', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve({
          ok: true,
          json: () => Promise.reject(new SyntaxError('unexpected token')),
        }),
      ),
    );
    expect(await fetchRealmInventory()).toBeNull();
  });
});

describe('what the panel draws', () => {
  const at = (realmId: number, generation = 0) => ({ realmId, generation });
  const view = (body: string, here: { realmId: number; generation: number } | null = null) =>
    realmPanelView(readRealmInventory(parsed(body)), here);

  const text = (body: string) => (view(body)?.summary ?? []).map((part) => part.text);

  it('leads with the aggregate, which is the claim the panel exists to show', () => {
    expect(text(LIVE_BODY)).toEqual(['Realms', '1 active', '1855 simulated', '0 asleep']);
  });

  it('accents the sleeping count and nothing else', () => {
    // It is the only number this panel exists for. A first cut coloured the `dormant` STATE in the rows instead, which
    // was a rule that could never match: the server lists a realm only while it is awake, so no row is ever dormant.
    const accented = (view(LIVE_BODY)?.summary ?? []).filter((part) => part.accent).map((part) => part.text);
    expect(accented).toEqual(['0 asleep']);
  });

  it('says how many are asleep even when the rows show none of them', () => {
    // The point of the row policy: a dormant interior has no row, so the only place its cost shows is this number.
    expect(text(LIVE_BODY.replace('"dormant":0', '"dormant":1197'))).toContain('1197 asleep');
  });

  it('mentions closing and divided realms only when there are some', () => {
    // A realm being torn down is rare, and a permanent "0 closing" would be noise on every line.
    expect(text(LIVE_BODY)).not.toContain('0 closing');
    expect(text(LIVE_BODY.replace('"closing":0', '"closing":2'))).toContain('2 closing');
    expect(text(LIVE_BODY.replace('"divided":0', '"divided":3'))).toContain('3 divided');
  });

  it('says how fresh the populations are, separately from the states', () => {
    expect(view(LIVE_BODY)?.freshness).toBe('populations as of tick 120 · 1792 more not shown');
    expect(view(LIVE_BODY.replace('"omitted":1792', '"omitted":0'))?.freshness).toBe('populations as of tick 120');
  });

  it('says so when the server has not counted anything yet', () => {
    const fresh = view(LIVE_BODY.replace('"censusTick":120', '"censusTick":-1').replace('"omitted":1792', '"omitted":0'))?.freshness;
    expect(fresh).toBe('populations not counted yet');
  });

  it('names every row the way the HUD names the realm the session is in', () => {
    expect(view(LIVE_BODY)?.rows.map((row) => row.label)).toEqual(['Planet 0', 'Planet 1', 'Planet 2', 'Space', 'Interior 0', 'Interior 1']);
  });

  it('marks the realm the session is in, and only that one', () => {
    // The mark is also what stops the row being a button: asking for the realm already on screen produces no REALM
    // block, so nothing would ever end the fade the ask starts.
    const rows = view(LIVE_BODY, at(2))?.rows ?? [];
    expect(rows.filter((row) => row.here).map((row) => row.id)).toEqual([2]);
  });

  it('marks nothing when the session is in no realm the server listed', () => {
    expect((view(LIVE_BODY, at(900))?.rows ?? []).some((row) => row.here)).toBe(false);
    expect((view(LIVE_BODY, null)?.rows ?? []).some((row) => row.here)).toBe(false);
  });

  it('draws nothing at all when there is no inventory', () => {
    expect(realmPanelView(null, at(0))).toBeNull();
  });
});

describe('naming two realms that carry the same tag', () => {
  /**
   * Two interiors with the SAME `appTag`, which a multi-planet world really produces: the tag's slot field holds an
   * interior's index within ITS planet, so planet 0's fifth interior and planet 2's fifth interior are both
   * `0x10000005`. Captured shape, hand-built ids — one document cannot hold two planets' fifth interiors and also stay
   * short enough to read.
   */
  const COLLIDING =
    '{"tick":10,"censusTick":9,"maxRows":64,"counts":{"active":2,"simulated":0,"dormant":0,"closing":0,"divided":0},"omitted":0,' +
    '"realms":[' +
    '{"id":0,"generation":0,"appTag":0,"state":"active","divisor":1,"sleepAfterTicks":0,"players":1,"npcs":0,"creatures":0,"structures":0},' +
    '{"id":8,"generation":0,"appTag":268435461,"state":"active","divisor":1,"sleepAfterTicks":150,"players":1,"npcs":3,"creatures":0,"structures":0},' +
    '{"id":1240,"generation":0,"appTag":268435461,"state":"active","divisor":1,"sleepAfterTicks":150,"players":4,"npcs":3,"creatures":0,"structures":0}' +
    ']}';

  it('tells them apart by realm id, because the tag cannot', () => {
    // Without this the panel shows two "Interior 5" rows with different populations and nothing to distinguish them —
    // in a view whose whole purpose is watching one specific room.
    const rows = realmPanelView(readRealmInventory(parsed(COLLIDING)), null)?.rows ?? [];
    expect(rows.map((row) => row.label)).toEqual(['Planet 0', 'Interior 5 · #8', 'Interior 5 · #1240']);
  });

  it('leaves a name alone when it appears once', () => {
    // A one-planet world is the default, and its names must stay readable.
    const rows = realmPanelView(readRealmInventory(parsed(LIVE_BODY)), null)?.rows ?? [];
    expect(rows.every((row) => !row.label.includes('#'))).toBe(true);
  });
});

describe('numbers that are not whole', () => {
  it('rejects a fractional realm id', () => {
    // A row's id is what the panel hands to ViewRealm, which puts it in a VarUInt on the wire. The toolbar's interior
    // box already had to fix exactly this with Math.trunc.
    expect(readRealmInventory(parsed(LIVE_BODY.replace('"id":1,', '"id":1.5,')))).toBeNull();
  });

  it('rejects a fractional population', () => {
    expect(readRealmInventory(parsed(LIVE_BODY.replace('"npcs":57,', '"npcs":57.5,')))).toBeNull();
  });

  it('rejects a census tick that is not whole', () => {
    expect(readRealmInventory(parsed(LIVE_BODY.replace('"censusTick":120', '"censusTick":120.5')))).toBeNull();
  });
});

describe('a realm id that was recycled', () => {
  /** The same slot, two incarnations: the row is the NEW dungeon, the session is still reported in the old one. */
  const RECYCLED =
    '{"tick":10,"censusTick":9,"maxRows":64,"counts":{"active":1,"simulated":0,"dormant":0,"closing":0,"divided":0},"omitted":0,' +
    '"realms":[{"id":1237,"generation":4,"appTag":805306368,"state":"active","divisor":1,"sleepAfterTicks":50,' +
    '"players":3,"npcs":9,"creatures":0,"structures":0}]}';

  it('is not the realm the session is in, even though the number matches', () => {
    // A realm's identity is the pair (12-realms § 1.1). Matching on the id alone would mark this row "you are here" for
    // a dungeon the session left, and suppress the one control that would take it back.
    const rows = realmPanelView(readRealmInventory(parsed(RECYCLED)), { realmId: 1237, generation: 3 })?.rows ?? [];
    expect(rows.map((row) => row.here)).toEqual([false]);
  });

  it('is the realm the session is in when both halves match', () => {
    const rows = realmPanelView(readRealmInventory(parsed(RECYCLED)), { realmId: 1237, generation: 4 })?.rows ?? [];
    expect(rows.map((row) => row.here)).toEqual([true]);
  });

  it('rejects a row with no generation at all, rather than assuming zero', () => {
    // Assuming 0 would make every row of an older server compare equal to a first-incarnation realm — right by accident
    // today and wrong after the first restart, which is the worst of both.
    expect(readRealmInventory(parsed(RECYCLED.replace('"generation":4,', '')))).toBeNull();
  });
});
