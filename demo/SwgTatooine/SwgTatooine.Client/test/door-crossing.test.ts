import { describe, expect, it } from 'vitest';
import { placement } from '../src/assets/asset-frame';
import type { Descriptor } from '../src/assets/descriptor';
import { crossedDoor, doorPlanes, spawnFacingAt, spawnNear } from '../src/app/door-crossing';

const IDENTITY = [0, 0, 0, 1] as const;
const HALF_TURN = [0, -1, 0, 0] as const;

function descriptor(overrides: Partial<Descriptor>): Descriptor {
  return {
    schema: 1,
    id: 'test',
    kind: 'building',
    frame: 'glTF',
    render: { glb: 't.glb', triangles: 0, materials: [] },
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

/** `tat_house_large`'s door and front spawn: the door on +Z crossed towards -Z, the spawn on the porch facing away. */
const HOUSE = descriptor({
  portals: [
    {
      id: 'front',
      center: [1.5, 2.2, 5.45],
      rot: IDENTITY,
      half: [0.95, 1.5, 0.4],
      forward: '-Z',
      target: 'room',
      target_spawn: 'entry',
      exit_spawn: 'front',
    },
  ],
  spawns: [{ id: 'front', pos: [1.5, 0.7, 6.3], rot: HALF_TURN, forward: '-Z' }],
});

/** `tat_house_large_interior`'s way out and entry: the door on +Z crossed towards +Z, the entry facing into the room. */
const ROOM = descriptor({
  kind: 'interior',
  portals: [
    {
      id: 'front',
      center: [1.5, 1.5, 3.5],
      rot: HALF_TURN,
      half: [1, 1.5, 0.4],
      forward: '-Z',
      target: 'house',
      target_spawn: 'front',
      exit_spawn: 'entry',
    },
  ],
  spawns: [{ id: 'entry', pos: [1.5, 0, 2.5], rot: IDENTITY, forward: '-Z' }],
});

describe('doorPlanes and crossedDoor', () => {
  it("place a house's door where the server does, and fire walking in, not walking out", () => {
    for (const yaw of [0, Math.PI / 2, Math.PI, -Math.PI / 2]) {
      const [door] = doorPlanes(HOUSE, placement([100, 0, 200], yaw));
      const outside = [door.x - door.fx, door.z - door.fz] as const;
      const inside = [door.x + door.fx * 0.3, door.z + door.fz * 0.3] as const;
      expect(crossedDoor(door, outside[0], outside[1], inside[0], inside[1]), `in, yaw ${yaw}`).toBe(true);
      expect(crossedDoor(door, inside[0], inside[1], outside[0], outside[1]), `out, yaw ${yaw}`).toBe(false);
    }
  });

  it("mirror the door's x like the walls: the published 1.5 is -1.5 at yaw 0", () => {
    const [door] = doorPlanes(HOUSE, placement([0, 0, 0]));
    expect(door.x).toBeCloseTo(-1.5, 9);
    expect(door.fz).toBeCloseTo(-1, 9);
  });

  it("point a room's way out out of the room", () => {
    const [door] = doorPlanes(ROOM, placement([0, 0, 0]));
    expect(door.fz).toBeCloseTo(1, 9);
  });
});

describe('spawnFacingAt', () => {
  it('faces into the room on its entry: the door is behind', () => {
    const facing = spawnFacingAt(ROOM, placement([0, 0, 0]), -1.5, 2.5);
    expect(facing).not.toBeNull();
    expect(facing![1]).toBeCloseTo(-1, 9); // away from the door, which is at +Z
  });

  it('faces away from the house on its porch, wherever the house is turned', () => {
    for (const yaw of [0, Math.PI / 2, Math.PI, -Math.PI / 2]) {
      const pose = placement([100, 0, 200], yaw);
      const [door] = doorPlanes(HOUSE, pose);
      // The porch spawn, found where the door's plane says the front is: 0.85 m outside it.
      const facing = spawnFacingAt(HOUSE, pose, door.x - door.fx * 0.85, door.z - door.fz * 0.85);
      expect(facing, `yaw ${yaw}`).not.toBeNull();
      expect(facing![0], `yaw ${yaw}`).toBeCloseTo(-door.fx, 9);
      expect(facing![1], `yaw ${yaw}`).toBeCloseTo(-door.fz, 9);
    }
  });

  it('finds the spawn a body ran past before the client first saw it', () => {
    // Measured live at 9.7 m/s: first seen 1.0 m past the room's entry, where a 0.6 m radius found nothing — 1.5 s of
    // black, then the door beside the body. A run of 10 m/s for ~0.3 s is 3 m.
    const facing = spawnFacingAt(ROOM, placement([0, 0, 0]), -1.5 - 3, 2.5);
    expect(facing).not.toBeNull();
    expect(facing![1]).toBeCloseTo(-1, 9);
  });

  it('gives the nearest spawn and how far it is, for choosing between structures', () => {
    const near = spawnNear(ROOM, placement([0, 0, 0]), -2.5, 2.5);
    expect(near?.distance).toBeCloseTo(1, 6);
  });

  it('is null away from every spawn', () => {
    expect(spawnFacingAt(ROOM, placement([0, 0, 0]), 4, -3)).toBeNull();
  });
});
