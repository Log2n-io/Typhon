import { FLAT_GROUND } from '../src/terrain/ground-sampler';
import { WorldStore } from '@typhondb/client';
import { describe, expect, it } from 'vitest';
import { SHUTTLEPORT_KIND } from '../src/app/shuttle';
import { Archetype, StructureKind, SWG_SCHEMA } from '../src/data/swg-schema';
import { LayerPacker, type FrameView } from '../src/render/layer-packer';
import { buildShape } from '../src/render/shapes';
import { SHUTTLEPORT_STYLE, styleFor } from '../src/render/styles';
import { extractFrustumPlanes, packedStyle } from '../src/render/view-math';

/**
 * The shuttleport's own look: a blue dome, drawn by a second layer over the structure archetype.
 *
 * <b>What is being protected is that a port is findable.</b> It is one of a city's ~190 structures and used to be a
 * slightly paler box among them, so the only way to find the building a flight leaves from was to click buildings and
 * read `kind` in the inspector. The two halves below are the two ways that can silently stop working: the dome's
 * geometry coming out inside-out or black, and the style filter drawing a port twice or not at all.
 */

/** Eye at the render origin looking down +Z, 90° vertical field of view, 800 × 800 CSS px: 400 px per metre at 1 m. */
function view(): FrameView {
  const m = new Float32Array(16);
  m[0] = 1;
  m[5] = 1;
  m[10] = 1001 / 999;
  m[11] = 1;
  m[14] = -2000 / 999;
  const planes = new Float64Array(24);
  extractFrustumPlanes(m, planes);
  return {
    renderTick: 1,
    renderFrac: 0,
    originX: 0,
    originZ: 0,
    eyeX: 0,
    eyeY: 0,
    eyeZ: 0,
    planes,
    pixelsPerMetre: 400,
    viewportWidth: 800,
    viewportHeight: 800,
    selectedNetId: 0,
    ground: FLAT_GROUND,
    predicted: null,
    hidden: new Set<number>(),
    crowd: null,
    crowdDraws: false,
    crowdCount: 0,
    perSecond: 1,
  };
}

/** A structure store holding one entity per `kind` asked for, all in front of the eye and close enough to be meshes. */
function setup(kinds: readonly number[]) {
  const world = new WorldStore(SWG_SCHEMA, { maxNetId: 4096 });
  const store = world.archetypeStore(Archetype.WorldObject);
  world.beginFrame(1);
  const kindField = store.fieldAt(store.fieldIndex('kind'));
  const regions = store.fieldAt(store.fieldIndex('region'));
  kinds.forEach((kind, i) => {
    const slot = world.enter(Archetype.WorldObject, i + 1);
    // Spread along x at a fixed distance, so every one of them is in view and in the near band.
    store.resetMotion(slot, [i * 40 - 60, 90], [0, 0], 1, 0);
    kindField[slot] = kind;
    regions[slot] = 1;
  });

  return { store };
}

/** The netIds one packer put in its near band, ascending. */
function drawn(packer: LayerPacker): number[] {
  const out: number[] = [];
  for (let k = 0; k < packer.nearCount; k++) {
    out.push(packer.nearNetIds[k]);
  }

  return out.sort((a, b) => a - b);
}

describe('the shuttleport dome', () => {
  it('has outward normals everywhere, and no degenerate face at the pole', () => {
    const dome = buildShape('dome');
    expect(dome.indices.length).toBeGreaterThan(0);

    // Every normal is a unit vector: `face` divides by `Math.hypot(...) || 1`, so a degenerate triangle — which is what
    // a quad band run all the way to the pole produces — yields (0, 0, 0) and shades black instead of throwing.
    for (let v = 0; v < dome.normals.length; v += 3) {
      const length = Math.hypot(dome.normals[v], dome.normals[v + 1], dome.normals[v + 2]);
      expect(length).toBeCloseTo(1, 6);
    }

    // And every normal points AWAY from the centre of the hemisphere, which is (0, 0, 0) in the unit shape: the dot of
    // the normal with the vertex's own offset from there is positive. Inverted winding fails this on every face, which
    // is the difference between a dome and a hollow shell you can see the inside of.
    for (let v = 0; v < dome.positions.length; v += 3) {
      const [px, py, pz] = [dome.positions[v], dome.positions[v + 1], dome.positions[v + 2]];
      const [nx, ny, nz] = [dome.normals[v], dome.normals[v + 1], dome.normals[v + 2]];
      expect(px * nx + py * ny + pz * nz).toBeGreaterThan(0);
    }
  });

  it('spans the unit box the per-instance size scales: x and z in [-0.5, 0.5], y in [0, 1]', () => {
    const dome = buildShape('dome');
    for (let v = 0; v < dome.positions.length; v += 3) {
      expect(Math.abs(dome.positions[v])).toBeLessThanOrEqual(0.5 + 1e-9);
      expect(dome.positions[v + 1]).toBeGreaterThanOrEqual(0);
      expect(dome.positions[v + 1]).toBeLessThanOrEqual(1 + 1e-9);
      expect(Math.abs(dome.positions[v + 2])).toBeLessThanOrEqual(0.5 + 1e-9);
    }
  });

  it('is taller than a building, so it is visible over the city it stands in', () => {
    const port = styleFor(SHUTTLEPORT_STYLE).sizes[StructureKind.Shuttleport];
    const building = styleFor('WorldObject').sizes[StructureKind.Building];
    expect(port[1]).toBeGreaterThan(building[1] * 2);

    // The footprint stays inside the server's 20 m boarding radius (`TatooineReplication.ShuttleBoardM`): a player who
    // can see the prompt is standing BESIDE the dome, not inside it.
    expect(port[0] / 2).toBeLessThan(20);
  });
});

describe('a layer that filters by style', () => {
  it('draws only its own kind, and the structure layer draws everything else — each entity exactly once', () => {
    const { store } = setup([
      StructureKind.Building,
      StructureKind.Shuttleport,
      StructureKind.CraftingStation,
      StructureKind.Shuttleport,
    ]);

    const ports = new LayerPacker(Archetype.WorldObject, styleFor(SHUTTLEPORT_STYLE), SHUTTLEPORT_KIND, -1);
    const rest = new LayerPacker(Archetype.WorldObject, styleFor('WorldObject'), -1, SHUTTLEPORT_KIND);
    ports.bind(store);
    rest.bind(store);
    const frame = view();
    ports.pack(frame);
    rest.pack(frame);

    expect(drawn(ports)).toEqual([2, 4]);
    expect(drawn(rest)).toEqual([1, 3]);

    // The union is the whole store and the intersection is empty: a port drawn by both layers would be a box inside a
    // dome, z-fighting, and one drawn by neither would be the bug this replaced.
    expect(ports.nearCount + rest.nearCount + ports.farCount + rest.farCount).toBe(store.liveCount);
  });

  it('keeps the kind in the packed style word, so the dome is coloured by its own entry and not by index 0', () => {
    const { store } = setup([StructureKind.Shuttleport]);
    const ports = new LayerPacker(Archetype.WorldObject, styleFor(SHUTTLEPORT_STYLE), SHUTTLEPORT_KIND, -1);
    ports.bind(store);
    ports.pack(view());

    expect(ports.nearCount).toBe(1);
    expect(packedStyle(ports.nearData[3])).toBe(StructureKind.Shuttleport);
  });

  it('draws nothing when the store holds none of its kind, rather than falling back to drawing all of them', () => {
    const { store } = setup([StructureKind.Building, StructureKind.CraftingStation]);
    const ports = new LayerPacker(Archetype.WorldObject, styleFor(SHUTTLEPORT_STYLE), SHUTTLEPORT_KIND, -1);
    ports.bind(store);
    ports.pack(view());

    expect(ports.nearCount).toBe(0);
    expect(ports.farCount).toBe(0);
  });

  it('an unfiltered layer is unchanged: both defaults off draws the whole store', () => {
    const { store } = setup([StructureKind.Building, StructureKind.Shuttleport]);
    const all = new LayerPacker(Archetype.WorldObject, styleFor('WorldObject'));
    all.bind(store);
    all.pack(view());

    expect(drawn(all)).toEqual([1, 2]);
  });
});
