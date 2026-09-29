import { describe, expect, it } from 'vitest';
import { NullEngine } from '@babylonjs/core/Engines/nullEngine';
import { Scene } from '@babylonjs/core/scene';
import { RealmFrame } from '@typhondb/client';
import { realmViewOf, type RealmView } from '../src/data/realm-view';
import { InteriorProfile } from '../src/render/interior-profile';
import { PlanetProfile } from '../src/render/planet-profile';
import { cameraBoundsFor } from '../src/render/scene-profile';
import { PALETTES, paletteFor } from '../src/render/ground';
import { buildBox, buildRoom } from '../src/render/room-mesh';
import { SpaceProfile } from '../src/render/space-profile';
import { MapCamera } from '../src/camera/map-camera';
import { Heightfield } from '../src/terrain/heightfield';
import { FLAT_GROUND } from '../src/terrain/ground-sampler';

const PLANET: RealmView = realmViewOf(new RealmFrame(0, 1, 0, 0, 24, 64, false, [-8192, -8192, 0], [8192, 8192, 64]))!;
const INTERIOR: RealmView = realmViewOf(new RealmFrame(7, 1, 1, 0x10000004, 24, 64, false, [0, 0, 0], [64, 64, 64]))!;
const SPACE: RealmView = realmViewOf(
  new RealmFrame(9, 1, 2, 0x20000000, 24, 500, true, [-8000, -8000, -8000], [8000, 8000, 8000]),
)!;

describe('cameraBoundsFor', () => {
  it('takes the box from the realm, so an interior is not a 16 km square', () => {
    const planet = cameraBoundsFor(PLANET);
    const interior = cameraBoundsFor(INTERIOR);
    expect([planet.minX, planet.maxX]).toEqual([-8192, 8192]);
    expect([interior.minX, interior.maxX]).toEqual([0, 64]);
  });

  it('scales the pull-back from the realm rather than tabulating it per kind', () => {
    // The whole reason RealmView carries a half-extent. A table of distances per scene would be wrong for the first
    // realm sized differently from the one it was written against.
    expect(cameraBoundsFor(PLANET).maxDistanceM).toBeGreaterThan(20_000);
    expect(cameraBoundsFor(INTERIOR).maxDistanceM).toBeLessThan(200);
    expect(cameraBoundsFor(INTERIOR).maxDistanceM).toBeGreaterThan(cameraBoundsFor(INTERIOR).minDistanceM);
  });

  it('will not let a room be grazed from the side the way a planet can be', () => {
    expect(cameraBoundsFor(INTERIOR).minPitch).toBeGreaterThan(cameraBoundsFor(PLANET).minPitch);
  });
});

describe('the room mesh', () => {
  it('is a floor and four walls with no ceiling, wound inward', () => {
    const room = buildRoom(64, 64, 4);
    // Five quads: nothing on top. A lid would be an opaque quad between a map camera and everything in the room.
    expect(room.indices).toHaveLength(5 * 6);
    expect(room.positions).toHaveLength(5 * 4 * 3);

    // Exactly one face points up — the floor — and none points down.
    const normals = room.normals as number[];
    let up = 0;
    let down = 0;
    for (let i = 0; i < normals.length; i += 3) {
      up += normals[i + 1] > 0.5 ? 1 : 0;
      down += normals[i + 1] < -0.5 ? 1 : 0;
    }

    expect(up).toBe(4);
    expect(down).toBe(0);
  });

  it('is wound INWARD on every face, which is the one property the room depends on', () => {
    // Counting normals cannot see this: a room wound outward has exactly the same normals and draws its far walls in
    // front of its near ones. The winding is what decides which side is the front face, so it has to be asserted as a
    // winding — the cross of the first triangle against the declared normal, per face.
    const room = buildRoom(64, 64, 4);
    const p = room.positions as number[];
    const n = room.normals as number[];
    const i = room.indices as number[];

    for (let t = 0; t < i.length; t += 3) {
      const [a, b, c] = [i[t] * 3, i[t + 1] * 3, i[t + 2] * 3];
      const u = [p[b] - p[a], p[b + 1] - p[a + 1], p[b + 2] - p[a + 2]];
      const v = [p[c] - p[a], p[c + 1] - p[a + 1], p[c + 2] - p[a + 2]];
      const cross = [u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0]];
      const dot = cross[0] * n[a] + cross[1] * n[a + 1] + cross[2] * n[a + 2];

      // Negative: the geometric winding opposes the declared normal, which is how Babylon's own box faces are built
      // and what makes the inward side the front face.
      expect(dot, `triangle ${t / 3} faces the wrong way`).toBeLessThan(0);
    }
  });

  it('starts at the origin of room space, because an interior realm is not centred', () => {
    const room = buildRoom(64, 64, 4);
    const xs = (room.positions as number[]).filter((_, i) => i % 3 === 0);
    expect(Math.min(...xs)).toBe(0);
    expect(Math.max(...xs)).toBe(64);
  });
});

describe('space, the one realm the camera is inside', () => {
  it('lets the eye go under the ecliptic, which no other realm does', () => {
    // A negative pitch floor IS `allowBelowGround` in practice: `updateEye` puts the eye at
    // `groundY + sin(pitch) * distance`, so nothing but a negative pitch can take it below the plane.
    expect(cameraBoundsFor(SPACE).minPitch).toBeLessThan(0);
    expect(cameraBoundsFor(PLANET).minPitch).toBeGreaterThan(0);
    expect(cameraBoundsFor(INTERIOR).minPitch).toBeGreaterThan(0);
  });

  it('hits the pan plane from BELOW it, which is where a camera in space spends half its time', () => {
    // The silent failure this fixes: `groundHit` used to reject any ray that was not pointing down, so from under the
    // ecliptic pan- and zoom-to-cursor stopped working with no error at all — the drag simply did nothing.
    const out = new Float64Array(2);
    const up = { ox: 10, oy: -500, oz: 20, dx: 0, dy: 1, dz: 0 };
    expect(MapCamera.groundHit(up, out)).toBe(true);
    expect([out[0], out[1]]).toEqual([10, 20]);
  });

  it('still refuses a ray pointing AWAY from the plane, from either side of it', () => {
    // The direction test is `t >= 0`, not the sign of dy — so it has to stay correct above the plane too.
    const out = new Float64Array(2);
    expect(MapCamera.groundHit({ ox: 0, oy: 500, oz: 0, dx: 0, dy: 1, dz: 0 }, out)).toBe(false);
    expect(MapCamera.groundHit({ ox: 0, oy: -500, oz: 0, dx: 0, dy: -1, dz: 0 }, out)).toBe(false);
  });

  it('refuses a ray running parallel to the plane, however close to it', () => {
    const out = new Float64Array(2);
    expect(MapCamera.groundHit({ ox: 0, oy: 0.001, oz: 0, dx: 1, dy: 0, dz: 0 }, out)).toBe(false);
  });

  it('frames the realm it was entered with, not the size its geometry was built at', () => {
    const space = new SpaceProfile(new Scene(new NullEngine()));
    space.enter(SPACE);
    expect([space.bounds.minX, space.bounds.maxX]).toEqual([-8000, 8000]);
    expect(space.stats.triangles).toBe(0);
  });


  it('is drawn on a CLOSED box, because every direction from inside it needs something to draw', () => {
    // A room has no lid — a map camera looks down into it. Space is the opposite case, and a missing face there is a
    // hole onto the clear colour wherever the camera looks up.
    const box = buildBox(1, 1, 1);
    expect(box.indices).toHaveLength(6 * 6);

    const normals = box.normals as number[];
    let up = 0;
    let down = 0;
    for (let i = 0; i < normals.length; i += 3) {
      up += normals[i + 1] > 0.5 ? 1 : 0;
      down += normals[i + 1] < -0.5 ? 1 : 0;
    }

    expect(up).toBe(4);
    expect(down).toBe(4);
  });
});

describe('the ground palette', () => {
  it('keeps index 0 exactly as the shader had it, so the planet this client shipped with is unchanged', () => {
    // The literals `GROUND_FRAGMENT` carried before they became uniforms. A regression here recolours Tatooine.
    const desert = paletteFor(0);
    expect([desert.base.r, desert.base.g, desert.base.b]).toEqual([0.78, 0.66, 0.47]);
    expect([desert.rock.r, desert.rock.g, desert.rock.b]).toEqual([0.54, 0.47, 0.39]);
    expect([desert.peak.r, desert.peak.g, desert.peak.b]).toEqual([0.86, 0.8, 0.68]);
  });

  it('gives each planet a different one, which is the only thing telling two planets apart', () => {
    // Every planet has the same relief and the same towns — one bake, one map — so if the palettes collided the
    // planets would be indistinguishable on screen.
    const seen = new Set([0, 1, 2, 3].map((i) => paletteFor(i).base.toHexString()));
    expect(seen.size).toBe(4);
  });

  it('wraps rather than failing, so a fifth planet repeats a colour instead of rendering undefined', () => {
    expect(paletteFor(PALETTES.length).base.toHexString()).toBe(paletteFor(0).base.toHexString());
    expect(paletteFor(999).sky).toBeDefined();
  });

  it('carries a sky with each palette, so a green world is not under a desert haze', () => {
    expect(paletteFor(1).sky.toHexString()).not.toBe(paletteFor(0).sky.toHexString());
  });
});

describe('the profiles, on a NullEngine', () => {
  function scene(): Scene {
    return new Scene(new NullEngine());
  }

  it('gives the planet its heightfield and a room the flat floor', () => {
    const s = scene();
    const field = new Heightfield();
    const planet = new PlanetProfile(s, field, 8192);
    const room = new InteriorProfile(s, 64);
    expect(planet.ground).toBe(field);
    expect(room.ground).toBe(FLAT_GROUND);
  });

  it("reports no terrain figures for a room, rather than the planet's stale ones", () => {
    const room = new InteriorProfile(scene(), 64);
    expect(room.stats).toEqual({ triangles: 0, nodes: 0, finestM: 0, capped: false });
  });

  it("KEEPS the planet's heightfield across a round trip into a room", () => {
    // The 67 MB assertion, and the reason nothing is disposed on a switch. The field and its R32F texture come from a
    // bake measured at 13.2 s across eight workers, and every planet shares the one bake — so a viewer stepping into a
    // shop for ten seconds must not pay for it again on the way out.
    //
    // <b>Asserted on the GPU resources, not on the field.</b> The first version of this case checked `planet.ground`
    // and the field's array, and a mutation that made `leave()` DISPOSE the planet passed it: the `Heightfield` is
    // owned by the app and merely handed to the profile, so it survives a disposal that destroys the mesh, the material
    // and the height texture. What the round trip has to preserve is what `dispose` would take.
    const s = scene();
    const field = new Heightfield();
    const planet = new PlanetProfile(s, field, 8192);
    const room = new InteriorProfile(s, 64);

    planet.enter(PLANET);
    const meshes = s.meshes.length;
    const materials = s.materials.length;
    const textures = s.textures.length;

    planet.leave();
    room.enter(INTERIOR);
    room.leave();
    planet.enter(PLANET);

    expect(s.meshes.length).toBe(meshes);
    expect(s.materials.length).toBe(materials);
    expect(s.textures.length).toBe(textures);
    expect(planet.ground).toBe(field);
  });

  it('takes its bounds from the realm it was entered with, not from the one it was built at', () => {
    const room = new InteriorProfile(scene(), 64);
    room.enter(INTERIOR);
    expect([room.bounds.minX, room.bounds.maxX]).toEqual([0, 64]);

    // A server whose interiors are a different size: the geometry was authored at 64 and must still describe this one.
    const wide: RealmView = realmViewOf(new RealmFrame(8, 1, 1, 0x10000000, 24, 64, false, [0, 0, 0], [200, 120, 64]))!;
    room.enter(wide);
    expect([room.bounds.minX, room.bounds.maxX]).toEqual([0, 200]);
    expect([room.bounds.minZ, room.bounds.maxZ]).toEqual([0, 120]);
  });
});

describe('the camera reach below the plane', () => {
  it('reaches as far from under the ecliptic as from above it', () => {
    // `groundHit`'s sign test was one of two ways pan- and zoom-to-cursor died in space; this is the other. The reach
    // is derived from the eye's distance from the plane, and the signed form collapsed to a 1e-3 floor below it —
    // returning about 23 m, which then failed `groundHit`'s own `t <= maxDistance` for a ray ten kilometres long.
    const pitch = (60 * Math.PI) / 180;
    const wide = { minX: -9e9, maxX: 9e9, minZ: -9e9, maxZ: 9e9, minDistanceM: 6, maxDistanceM: 5e4, minPitch: -1.5 };

    const above = new MapCamera();
    above.groundY = 0;
    above.pitch = pitch;
    // `jumpTo` re-derives the eye from the pitch; `rotateBy` would only move the GOAL, which `update` settles later.
    above.jumpTo(0, 0, 10_000);
    const reachAbove = above.maxGroundHitM(1080);

    const below = new MapCamera();
    below.groundY = 0;
    below.setBounds(wide);
    below.pitch = -pitch;
    below.jumpTo(0, 0, 10_000);
    const reachBelow = below.maxGroundHitM(1080);

    expect(below.eyeY).toBeLessThan(0);
    expect(reachBelow).toBeGreaterThan(1000);
    expect(reachBelow).toBeCloseTo(reachAbove, 0);
  });
});
