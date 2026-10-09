import { describe, expect, it } from 'vitest';
import { RealmFrame } from '@typhondb/client';
import { crossingKindOf, decodeAppTag, interiorRealmOf, realmViewOf, sameRealm, type RealmView } from '../src/data/realm-view';

/**
 * The `AppTag` layout, restated here as literals rather than built from the decoder's own shifts.
 *
 * A test that composes a tag with the same `<<` the decoder uses with `>>>` passes whatever the layout is, which
 * is the one thing this file must not do: the layout is a CONTRACT with `TatooineSim.cs`, and only a
 * hand-written number can catch the two sides drifting apart.
 */
const TAG_PLANET_0 = 0x00000000; // planet, palette 0, placeSet 0, slot 0
const TAG_PLANET_1 = 0x00100001; // planet, palette 1 (green), placeSet 0, slot 1
const TAG_INTERIOR_5 = 0x10002005; // interior, palette 0, placeSet 2, slot 5
const TAG_SPACE = 0x20000000; // space
const TAG_DUNGEON_3 = 0x30000003; // dungeon, slot 3

/** The planet, as `TatooineSim` registers it: -8192..+8192 on both ground axes, flat, 64 m cells. */
function planetFrame(appTag = TAG_PLANET_0, realmId = 0, generation = 1): RealmFrame {
  return new RealmFrame(realmId, generation, 0, appTag, 24, 64, false, [-8192, -8192, 0], [8192, 8192, 64]);
}

/** An interior, as `TatooineSim` registers it: (0,0)..(64,64) — deliberately NOT centred on the origin. */
function interiorFrame(appTag = TAG_INTERIOR_5, realmId = 5, generation = 1): RealmFrame {
  return new RealmFrame(realmId, generation, 1, appTag, 24, 64, false, [0, 0, 0], [64, 64, 64]);
}

/** Space: a 16 km cube centred on the origin, deep, 500 m cells. */
function spaceFrame(realmId = 9, generation = 1): RealmFrame {
  return new RealmFrame(realmId, generation, 2, TAG_SPACE, 24, 500, true, [-8000, -8000, -8000], [8000, 8000, 8000]);
}

describe('decodeAppTag', () => {
  it('splits the four fields at the boundaries TatooineSim writes them at', () => {
    expect(decodeAppTag(TAG_PLANET_0)).toEqual({ scene: 'planet', palette: 0, placeSet: 0, slot: 0 });
    expect(decodeAppTag(TAG_PLANET_1)).toEqual({ scene: 'planet', palette: 1, placeSet: 0, slot: 1 });
    expect(decodeAppTag(TAG_INTERIOR_5)).toEqual({ scene: 'interior', palette: 0, placeSet: 2, slot: 5 });
    expect(decodeAppTag(TAG_SPACE)).toEqual({ scene: 'space', palette: 0, placeSet: 0, slot: 0 });
    expect(decodeAppTag(TAG_DUNGEON_3)).toEqual({ scene: 'dungeon', palette: 0, placeSet: 0, slot: 3 });
  });

  it('keeps the fields independent at their widest, so none bleeds into its neighbour', () => {
    // Every field at its maximum at once. A shift or mask off by one bit shows up here and nowhere else.
    expect(decodeAppTag(0x3fffffff)).toEqual({ scene: 'dungeon', palette: 0xff, placeSet: 0xff, slot: 0xfff });
  });

  it('reads a tag past 2^31 without sign trouble', () => {
    // A u32 with the top bit set arrives in JS as a number above 2^31, and the same bits arrive as a NEGATIVE
    // number from anything that read them signed. `>>> 0` is what makes both decode identically; without it the
    // second form's shifts sign-extend and every field below `scene` comes back wrong.
    expect(decodeAppTag(0xffffffff)).toEqual({ scene: 'planet', palette: 0xff, placeSet: 0xff, slot: 0xfff });
    expect(decodeAppTag(-1)).toEqual(decodeAppTag(0xffffffff));
  });

  it('falls back to a planet for a scene this build does not know', () => {
    // A newer server naming a fifth realm kind should cost the scene, not the session.
    expect(decodeAppTag(0x40000000).scene).toBe('planet');
  });
});

describe('realmViewOf', () => {
  it('is null in no realm', () => {
    expect(realmViewOf(null)).toBeNull();
  });

  it('takes the planet bounds from the frame, never from PLANET_HALF_EXTENT_M', () => {
    const view = realmViewOf(planetFrame())!;
    expect(view.scene).toBe('planet');
    expect([view.minX, view.maxX, view.minZ, view.maxZ]).toEqual([-8192, 8192, -8192, 8192]);
    expect([view.centreX, view.centreZ]).toEqual([0, 0]);
    expect([view.halfX, view.halfZ]).toEqual([8192, 8192]);
  });

  it('centres an interior at 32, not at 0 — its grid is (0,0)..(64,64) and is not centred', () => {
    // The whole reason RealmView carries a centre at all. A client that assumed ±half would put the camera
    // outside the room, at its corner, and clamp every pan to a box the room does not occupy.
    const view = realmViewOf(interiorFrame())!;
    expect(view.scene).toBe('interior');
    expect([view.centreX, view.centreZ]).toEqual([32, 32]);
    expect([view.halfX, view.halfZ]).toEqual([32, 32]);
    expect([view.minX, view.maxX]).toEqual([0, 64]);
  });

  it('gives a flat realm no altitude, whatever axis 2 of its grid happens to hold', () => {
    // A flat SpatialGridConfig leaves a degenerate slab on axis 2. It is not a height, and reading it as one
    // would give the interior a 0.1 mm ceiling.
    const view = realmViewOf(planetFrame())!;
    expect(view.deep).toBe(false);
    expect([view.minY, view.maxY]).toEqual([0, 0]);
  });

  it('reads altitude from axis 2 of a deep realm', () => {
    const view = realmViewOf(spaceFrame())!;
    expect(view.deep).toBe(true);
    expect([view.minY, view.maxY]).toEqual([-8000, 8000]);
    expect(view.cellM).toBe(500);
    expect(view.scene).toBe('space');
  });

  it('keys on the generation as well as the id, because ids are reused', () => {
    const first = realmViewOf(planetFrame(TAG_PLANET_0, 4, 1))!;
    const reused = realmViewOf(planetFrame(TAG_PLANET_0, 4, 2))!;
    expect(first.key).toBe('4:1');
    expect(reused.key).toBe('4:2');
    expect(sameRealm(first, reused)).toBe(false);
  });

  it('is the same realm across two decodes of one frame', () => {
    expect(sameRealm(realmViewOf(planetFrame()), realmViewOf(planetFrame()))).toBe(true);
    expect(sameRealm(null, null)).toBe(true);
    expect(sameRealm(realmViewOf(planetFrame()), null)).toBe(false);
  });
});

describe('interiorRealmOf', () => {
  const LAYOUT = { first: 3, perPlanet: 617 };
  const planet1 = realmViewOf(planetFrame(TAG_PLANET_1, 1))!;

  it('applies the layout the server published to the door slot the building carries', () => {
    // Neither half is derivable on the client: `portal` comes from `IsEnterable(i)` over a building's index within its
    // city, which is on no wire, and the layout comes from `/typhon/demo.json`.
    expect(interiorRealmOf(planet1, 4, LAYOUT)).toBe(3 + 617 + 4);
    expect(interiorRealmOf(realmViewOf(planetFrame(TAG_PLANET_0, 0)), 0, LAYOUT)).toBe(3);
  });

  it('is null for a building with no door', () => {
    // -1 is what a building that cannot be entered carries, and it must not become realm `first - 1`.
    expect(interiorRealmOf(planet1, -1, LAYOUT)).toBeNull();
  });

  it("is null for a slot outside the planet's door list", () => {
    expect(interiorRealmOf(planet1, 617, LAYOUT)).toBeNull();
    expect(interiorRealmOf(planet1, 1.5, LAYOUT)).toBeNull();
  });

  it('is null anywhere that is not a planet, because only a planet has doors', () => {
    expect(interiorRealmOf(realmViewOf(interiorFrame()), 0, LAYOUT)).toBeNull();
    expect(interiorRealmOf(realmViewOf(spaceFrame()), 0, LAYOUT)).toBeNull();
    expect(interiorRealmOf(null, 0, LAYOUT)).toBeNull();
  });

  it('is null without a layout, rather than guessing one', () => {
    expect(interiorRealmOf(planet1, 4, null)).toBeNull();
  });
});

describe('crossingKindOf', () => {
  const planet0 = realmViewOf(planetFrame(TAG_PLANET_0, 0))!;
  const planet1 = realmViewOf(planetFrame(TAG_PLANET_1, 1))!;
  const interior = realmViewOf(interiorFrame())!;
  const space = realmViewOf(spaceFrame())!;

  it('arrives and leaves when there is nothing on one side', () => {
    expect(crossingKindOf(null, planet0, false)).toBe('arrive');
    expect(crossingKindOf(planet0, null, false)).toBe('leave');
    expect(crossingKindOf(null, null, false)).toBe('leave');
  });

  it('is travel only when the client asked: the same move is a shuttle otherwise', () => {
    expect(crossingKindOf(planet0, planet1, true)).toBe('travel');
    expect(crossingKindOf(planet0, planet1, false)).toBe('transition');
  });

  it('is a transition through a door, in both directions', () => {
    expect(crossingKindOf(planet0, interior, false)).toBe('transition');
    expect(crossingKindOf(interior, planet0, false)).toBe('transition');
  });

  it('is a channel change to or from space, even when the client asked for it', () => {
    // Nothing travels into space in this demo — no CrossingKind names it and no Teleport reaches it. The camera
    // looks somewhere else, so presenting it as a journey would claim a continuity that does not exist.
    expect(crossingKindOf(planet0, space, true)).toBe('channel');
    expect(crossingKindOf(space, planet0, false)).toBe('channel');
  });
});

describe('the shape the renderer consumes', () => {
  it('never needs the server realm-id arithmetic to answer a scene question', () => {
    // An interior's realm id is `Planets + planet * InteriorsPerPlanet + portal`, which depends on two CLI flags
    // the client cannot see. Every question below is answered by the tag alone, at any realm id.
    const odd: RealmView = realmViewOf(interiorFrame(TAG_INTERIOR_5, 4919))!;
    expect(odd.scene).toBe('interior');
    expect(odd.slot).toBe(5);
    expect(odd.placeSet).toBe(2);
  });
});
