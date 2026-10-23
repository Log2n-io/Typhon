import { describe, expect, it } from 'vitest';
import { placement, pointToWorld } from '../src/assets/asset-frame';
import type { Descriptor, NavDesc } from '../src/assets/descriptor';
import { SpeedClass } from '../src/data/source';
import {
  crosses,
  detour,
  DoorWalker,
  footprintOf,
  navNodeAt,
  navRoute,
  pathIn,
  pathOut,
  pointToAsset,
  STALL_MS,
  type Waypoint,
} from '../src/app/door-walk';

const IDENTITY = [0, 0, 0, 1] as const;
const HALF_TURN = [0, -1, 0, 0] as const;

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
      collision: [
        [-5, 0, -5],
        [5, 3, 5],
      ],
    },
    collision: null,
    portals: [],
    spawns: [],
    interactions: [],
    children: [],
    ...overrides,
  };
}

/** A 10 m house with its door on +Z, a path straight in from 6 m out to just past the plane. */
const HOUSE = descriptor({
  portals: [
    {
      id: 'front',
      center: [1.5, 1, 5.5],
      rot: IDENTITY,
      half: [1, 1, 0.4],
      forward: '-Z',
      target: 'room',
      target_spawn: 'entry',
      exit_spawn: 'front',
      approach: [
        [1.5, 0, 11],
        [1.5, 0.5, 7],
        [1.5, 0.7, 5.25],
      ],
    },
  ],
});

describe('crosses and detour', () => {
  const box = { minX: -5, minZ: -5, maxX: 5, maxZ: 5 };

  it('sees a line through a rectangle and not one beside it', () => {
    expect(crosses([-10, 0], [10, 0], box)).toBe(true);
    expect(crosses([-10, 6], [10, 6], box)).toBe(false);
    expect(crosses([-10, 5], [10, 5], box)).toBe(false); // along an edge is not through it
  });

  it('goes round by the short side, and no leg of the way round crosses it', () => {
    const from: Waypoint = [0, -10];
    const to: Waypoint = [1, 10];
    const via = detour(from, to, box);
    expect(via.length).toBeGreaterThan(0);
    const path = [from, ...via, to];
    for (let i = 1; i < path.length; i++) {
      expect(crosses(path[i - 1], path[i], box), `leg ${i}`).toBe(false);
    }

    // Round the +X side: the target is a metre east of the centre line.
    expect(via.every((p) => p[0] > 5)).toBe(true);
  });

  it('adds nothing when the line is clear', () => {
    expect(detour([-10, -10], [-10, 10], box)).toEqual([]);
  });
});

describe('pathIn', () => {
  it("walks the door's published path, mirrored and placed like the building, wherever it stands", () => {
    for (const yaw of [0, Math.PI / 2, Math.PI, -Math.PI / 2]) {
      const pose = placement([100, 2, 200], yaw);
      const start = pointToWorld(pose, [1.5, 0, 11]);
      const route = pathIn(HOUSE, pose, [start[0], start[2] + 0]);
      expect(route).not.toBeNull();
      const expected = (HOUSE.portals[0].approach ?? []).map((p) => pointToWorld(pose, p));
      const path = route!.points.slice(route!.pathStart);
      expect(path).toHaveLength(expected.length);
      path.forEach((p, i) => {
        expect(p[0]).toBeCloseTo(expected[i][0], 9);
        expect(p[1]).toBeCloseTo(expected[i][2], 9);
      });
    }
  });

  it('goes round the building from behind it, and never through it', () => {
    const pose = placement([0, 0, 0], 0);
    const from: Waypoint = [-1.5, -20]; // behind the house: its door faces +Z
    const route = pathIn(HOUSE, pose, from)!;
    expect(route.pathStart).toBeGreaterThan(0);
    const footprint = footprintOf(HOUSE, pose)!;
    const legs = [from, ...route.points.slice(0, route.pathStart + 1)];
    for (let i = 1; i < legs.length; i++) {
      expect(crosses(legs[i - 1], legs[i], footprint), `leg ${i}`).toBe(false);
    }
  });

  it('is null for a building that publishes no path', () => {
    expect(pathIn(descriptor({}), placement([0, 0, 0]), [0, 0])).toBeNull();
  });
});

/** Three nodes in a line, glTF frame; every cell of a 6 x 6 m grid serves its nearest node. */
function nav(): NavDesc {
  const nodes: [number, number, number][] = [
    [1.5, 0, 2],
    [-1.5, 0, 0],
    [1.5, 0, -2],
  ];
  const node: number[] = [];
  for (let row = 0; row < 12; row++) {
    for (let col = 0; col < 12; col++) {
      const x = -3 + (col + 0.5) * 0.5;
      const z = -3 + (row + 0.5) * 0.5;
      let best = 0;
      nodes.forEach((n, i) => {
        if (Math.hypot(n[0] - x, n[2] - z) < Math.hypot(nodes[best][0] - x, nodes[best][2] - z)) {
          best = i;
        }
      });
      node.push(best);
    }
  }

  return {
    nodes,
    edges: [
      [0, 1],
      [1, 2],
    ],
    cells: { origin: [-3, -3], size: 0.5, cols: 12, rows: 12, node },
  };
}

const ROOM = descriptor({
  kind: 'interior',
  portals: [
    {
      id: 'front',
      center: [1.5, 1, 3],
      rot: HALF_TURN,
      half: [1, 1, 0.4],
      forward: '-Z',
      target: 'house',
      target_spawn: 'front',
      exit_spawn: 'entry',
      approach: [
        [1.5, 0, 2],
        [1.5, 0, 3.25],
      ],
    },
  ],
  nav: nav(),
});

describe('pathOut', () => {
  it("walks the room's graph to its way out, then the way out's path", () => {
    const pose = placement([0, 0, 0]);
    // Standing by node 2 (glTF (1.5, -2), world (-1.5, -2)): node 2, node 1, then the path from node 0.
    const route = pathOut(ROOM, pose, [-1.5, -2.2])!;
    expect(route.points).toEqual([
      [-1.5, -2],
      [1.5, 0],
      [-1.5, 2],
      [-1.5, 3.25],
    ]);
    expect(route.pathStart).toBe(2);
  });

  it('finds a cell through the mirror and the nearest served one past the grid', () => {
    const n = nav();
    expect(navNodeAt(n, 1.5, -2)).toBe(2);
    expect(navNodeAt(n, 3.2, 2)).toBe(0);
    expect(navNodeAt(n, 40, 40)).toBe(-1);
    expect(navRoute(n, 2, 0)).toEqual([2, 1, 0]);
  });

  it('turns a world point back into the asset frame it came from', () => {
    const pose = placement([5, 0, -3], Math.PI / 2);
    const p = pointToAsset(pose, pointToWorld(pose, [1, 0, 2]));
    expect(p[0]).toBeCloseTo(1, 9);
    expect(p[2]).toBeCloseTo(2, 9);
  });
});

describe('DoorWalker', () => {
  it('runs to the path, walks the path, and keeps walking past its end until the door fires', () => {
    const walker = new DoorWalker();
    walker.start(
      [
        [0, 10],
        [0, 12],
        [0, 14],
      ],
      1,
      7,
      0,
    );

    let x = 0;
    let z = 0;
    let realm = 7;
    const classes = new Set<number>();
    for (let t = 0; t < 400 && walker.active; t++) {
      const intent = walker.intent(x, z, realm, t * 16);
      if (intent === null) {
        break;
      }

      classes.add(intent.speedClass);
      const step = intent.speedClass === SpeedClass.run ? 0.16 : 0.08;
      x += Math.cos(intent.heading) * step;
      z += Math.sin(intent.heading) * step;

      // Its last point is past a door at z 14.2 the walker knows nothing of: only the realm changing ends the walk.
      if (z > 14.2) {
        realm = 9;
      }
    }

    expect(walker.active).toBe(false);
    expect(z).toBeGreaterThan(14.2);
    expect(Math.abs(x)).toBeLessThan(0.05);
    expect(classes).toEqual(new Set([SpeedClass.run, SpeedClass.walk]));
  });

  it('does not stop on its last point: a prediction there is ahead of a server that has not crossed yet', () => {
    const walker = new DoorWalker();
    walker.start(
      [
        [0, 10],
        [0, 12],
      ],
      0,
      7,
      0,
    );
    expect(walker.intent(0, 10, 7, 0)).not.toBeNull(); // on the first point: on to the last
    const atEnd = walker.intent(0, 11.9, 7, 16);
    expect(atEnd).not.toBeNull();
    expect(atEnd!.heading).toBeCloseTo(Math.PI / 2, 9); // still along the last leg, +Z
  });

  it('ends at the crossing: another realm means the door fired', () => {
    const walker = new DoorWalker();
    walker.start([[0, 10]], 0, 7, 0);
    expect(walker.intent(0, 0, 7, 0)).not.toBeNull();
    expect(walker.intent(0, 0, 9, 16)).toBeNull();
    expect(walker.active).toBe(false);
  });

  it('gives up when the body stops getting closer', () => {
    const walker = new DoorWalker();
    walker.start([[0, 10]], 0, 7, 0);
    expect(walker.intent(0, 0, 7, 0)).not.toBeNull();
    expect(walker.intent(0, 0, 7, STALL_MS - 1)).not.toBeNull();
    expect(walker.intent(0, 0, 7, STALL_MS + 1)).toBeNull();
  });
});
