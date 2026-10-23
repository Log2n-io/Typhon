import { describe, expect, it } from 'vitest';
import { placement } from '../src/assets/asset-frame';
import type { Descriptor } from '../src/assets/descriptor';
import { prepareShape, type PlanBox } from '../src/app/structure-collision';
import {
  colliderLines,
  INTERACTION_COLOUR,
  markerLines,
  PORTAL_COLOUR,
  SPAWN_COLOUR,
  type LineSet,
} from '../src/render/gameplay-gizmos';
import type { Vec3 } from '../src/math/vec';

/**
 * The debug gizmos are drawn from the data the game acts on, so the claims are about WHERE they land: a portal's arrow
 * must point the way a body crosses it, and a child's markers must follow the child.
 */

const IDENTITY = [0, 0, 0, 1] as const;
/** A half turn about +Y, which every interior's door back out carries (ASSET_FORMAT.md § 2). */
const HALF_TURN = [0, 1, 0, 0] as const;

function descriptor(overrides: Partial<Descriptor>): Descriptor {
  return {
    schema: 1,
    id: 'test',
    kind: 'building',
    frame: 'glTF: metres, +Y up, right-handed',
    render: { glb: 'test-1.glb', triangles: 0, materials: [] },
    bounds: {
      render: [
        [-5, 0, -5],
        [5, 5, 5],
      ],
      collision: null,
    },
    collision: null,
    portals: [],
    spawns: [],
    interactions: [],
    children: [],
    ...overrides,
  };
}

function linesOf(set: LineSet, colour: readonly number[]): Vec3[][] {
  return set.lines.filter((_, i) => set.colours[i] === colour);
}

/** The arrow shafts of one colour: two-point lines. Box edges are two-point lines too, so the caller picks by origin. */
function shaftFrom(set: LineSet, colour: readonly number[], from: Vec3): Vec3 {
  const shaft = linesOf(set, colour).find(
    (line) => line.length === 2 && line[0].every((v, k) => Math.abs(v - from[k]) < 1e-9) && line[1][1] === from[1],
  );
  expect(shaft, `an arrow from ${from.join(',')}`).toBeDefined();
  const [a, b] = shaft ?? [from, from];
  const length = Math.hypot(b[0] - a[0], b[2] - a[2]);
  return [(b[0] - a[0]) / length, 0, (b[2] - a[2]) / length];
}

describe('markerLines', () => {
  it('points an entrance portal into the building, wherever the building stands', () => {
    // A door on the building's front (+Z), its box in front of the facade, forward "-Z": crossing it is walking in.
    const house = descriptor({
      portals: [
        {
          id: 'front',
          center: [0, 1, 6],
          rot: IDENTITY,
          half: [0.6, 1, 0.4],
          forward: '-Z',
          target: 'room',
          target_spawn: 'in',
          exit_spawn: 'out',
        },
      ],
    });

    for (const yaw of [0, Math.PI / 2, Math.PI, -Math.PI / 2]) {
      const pose = placement([100, 3, 200], yaw);
      const set = markerLines(house, pose, () => null);
      const centre = linesOf(set, PORTAL_COLOUR)
        .flat()
        .reduce<Vec3>((acc, p) => [acc[0] + p[0], acc[1] + p[1], acc[2] + p[2]], [0, 0, 0]);
      // The box's two rings and four verticals, and the arrow's shaft and head.
      expect(linesOf(set, PORTAL_COLOUR).length, 'a box and an arrow').toBe(8);

      // The arrow starts at the portal's centre and heads towards the building's origin.
      const portalCentre = pose.pos;
      const doorAt: Vec3 = [
        portalCentre[0] + 6 * Math.sin(yaw),
        portalCentre[1] + 1,
        portalCentre[2] + 6 * Math.cos(yaw),
      ];
      const direction = shaftFrom(set, PORTAL_COLOUR, doorAt);
      const towardsOrigin: Vec3 = [(portalCentre[0] - doorAt[0]) / 6, 0, (portalCentre[2] - doorAt[2]) / 6];
      expect(direction[0], `x at yaw ${yaw}`).toBeCloseTo(towardsOrigin[0], 9);
      expect(direction[2], `z at yaw ${yaw}`).toBeCloseTo(towardsOrigin[2], 9);
      expect(Number.isFinite(centre[0])).toBe(true);
    }
  });

  it("points an interior's exit portal out of the room: its half turn is part of its forward", () => {
    const room = descriptor({
      kind: 'interior',
      portals: [
        {
          id: 'front',
          center: [0, 1, 4],
          rot: HALF_TURN,
          half: [0.6, 1, 0.4],
          forward: '-Z',
          target: 'house',
          target_spawn: 'out',
          exit_spawn: 'in',
        },
      ],
    });

    const set = markerLines(room, placement([0, 0, 0], 0), () => null);
    // Out of the room is +Z, away from its centre: reading "-Z" as a direction would draw the exit backwards.
    expect(shaftFrom(set, PORTAL_COLOUR, [0, 1, 4])[2]).toBeCloseTo(1, 9);
  });

  it("draws a child's markers where the child stands, composed with the parent's placement", () => {
    const terminal = descriptor({
      id: 'terminal',
      kind: 'interactable',
      interactions: [{ kind: 'shuttle_ticket', pos: [0, 0, 1], rot: IDENTITY, forward: '-Z', radius: 1.5 }],
    });
    const port = descriptor({
      id: 'port',
      spawns: [{ id: 'arrive', pos: [0, 0, 3], rot: IDENTITY, forward: '-Z' }],
      children: [{ asset: 'terminal', pos: [0, 0, 10], rot: IDENTITY }],
    });

    const set = markerLines(port, placement([50, 0, 50], 0), (id) => (id === 'terminal' ? terminal : null));
    // The interaction ring is centred 10 + 1 m in front of the port, at its radius.
    // Closed, so its last point repeats its first: left out of the mean.
    const ring = (linesOf(set, INTERACTION_COLOUR).find((line) => line.length > 4) ?? []).slice(0, -1);
    const cx = ring.reduce((sum, p) => sum + p[0], 0) / ring.length;
    const cz = ring.reduce((sum, p) => sum + p[2], 0) / ring.length;
    expect(cx).toBeCloseTo(50, 1);
    expect(cz).toBeCloseTo(61, 1);
    expect(Math.hypot(ring[0][0] - 50, ring[0][2] - 61)).toBeCloseTo(1.5, 9);
    expect(linesOf(set, SPAWN_COLOUR).length, "the port's own spawn").toBeGreaterThan(0);
  });

  it('skips a child whose descriptor is not loaded rather than guessing', () => {
    const port = descriptor({ children: [{ asset: 'missing', pos: [0, 0, 10], rot: IDENTITY }] });
    expect(markerLines(port, placement([0, 0, 0], 0), () => null).lines).toHaveLength(0);
  });
});

describe('colliderLines', () => {
  const box = (over: Partial<PlanBox>): PlanBox => ({
    minX: -1,
    minZ: -2,
    maxX: 1,
    maxZ: 2,
    minY: 0,
    maxY: 2,
    planeY: 2,
    slopeX: 0,
    slopeZ: 0,
    radius: 0,
    ...over,
  });

  it('draws a flat box from its underside to its top, at the placement', () => {
    const set = colliderLines(prepareShape([box({})]), 0, 10, 5, 20);
    const heights = new Set(set.lines.flat().map((p) => p[1]));
    expect([...heights].sort()).toEqual([5, 7]);
    const xs = set.lines.flat().map((p) => p[0]);
    expect(Math.min(...xs)).toBe(9);
    expect(Math.max(...xs)).toBe(11);
  });

  it("draws a ramp's top as the sloped plane the collision reads, not its bounding box's roof", () => {
    // Rising 0.4 per metre north over a 4 m run: 0.8 m below the centre at one end, 0.8 m above at the other.
    const ramp = box({ minY: -1.3, maxY: 0.7, planeY: -0.1, slopeZ: 0.4 });
    const top = colliderLines(prepareShape([ramp]), 0, 0, 0, 0).lines[1];
    const heights = top.map((p) => p[1]);
    expect(Math.min(...heights)).toBeCloseTo(-0.9, 9);
    expect(Math.max(...heights)).toBeCloseTo(0.7, 9);
  });

  it('picks the pre-turned boxes for the placement', () => {
    const shape = prepareShape([box({})]);
    const turned = colliderLines(shape, 1, 0, 0, 0).lines.flat();
    // A quarter turn swaps the 2 m and 4 m spans.
    expect(Math.max(...turned.map((p) => p[0])) - Math.min(...turned.map((p) => p[0]))).toBeCloseTo(4, 9);
  });
});
