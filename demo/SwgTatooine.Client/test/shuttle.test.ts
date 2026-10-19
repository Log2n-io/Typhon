import { WorldStore } from '@typhondb/client';
import { describe, expect, it } from 'vitest';
import { nearestShuttleport, SHUTTLEPORT_KIND } from '../src/app/shuttle';
import { Archetype, SWG_SCHEMA } from '../src/data/swg-schema';

/**
 * Finding the port the avatar is standing at, from what the server sent.
 *
 * The prompt that offers a flight appears because of this function, so what it must get right is which structure counts
 * as a port and which city that port serves — the second being the one the wire encodes as `city + 1`.
 */
function setup() {
  const world = new WorldStore(SWG_SCHEMA, { maxNetId: 4096 });
  const store = world.archetypeStore(Archetype.WorldObject);
  world.beginFrame(1);
  const kinds = store.fieldAt(store.fieldIndex('kind'));
  const regions = store.fieldAt(store.fieldIndex('region'));
  const place = (netId: number, x: number, z: number, kind: number, region: number): void => {
    const slot = world.enter(Archetype.WorldObject, netId);
    store.resetMotion(slot, [x, z], [0, 0], 1, 0);
    kinds[slot] = kind;
    regions[slot] = region;
  };

  return { store, place };
}

describe('nearestShuttleport', () => {
  it('finds the port in range and decodes the city from the wire’s city + 1', () => {
    const { store, place } = setup();
    place(1, 10, 0, SHUTTLEPORT_KIND, 4); // city 3's port, 10 m east
    const found = nearestShuttleport(store, 1, 0, 0, 0, 20);

    expect(found).not.toBeNull();
    expect(found?.city).toBe(3);
    expect(found?.distanceM).toBeCloseTo(10, 6);
  });

  it('ignores a building that is not a port, and a port out of range', () => {
    const { store, place } = setup();
    place(1, 2, 0, 0, 4); // a building, right next to the avatar
    place(2, 100, 0, SHUTTLEPORT_KIND, 4); // a port, far away
    expect(nearestShuttleport(store, 1, 0, 0, 0, 20)).toBeNull();
  });

  it('ignores a port in no region, because there is no city to fly to', () => {
    const { store, place } = setup();
    place(1, 5, 0, SHUTTLEPORT_KIND, 0);
    expect(nearestShuttleport(store, 1, 0, 0, 0, 20)).toBeNull();
  });

  it('takes the nearest when two are in range', () => {
    const { store, place } = setup();
    place(1, 15, 0, SHUTTLEPORT_KIND, 1);
    place(2, 0, 4, SHUTTLEPORT_KIND, 7);
    expect(nearestShuttleport(store, 1, 0, 0, 0, 20)?.city).toBe(6);
  });

  it('is null without a store, which is every frame before the first one arrives', () => {
    expect(nearestShuttleport(null, 1, 0, 0, 0, 20)).toBeNull();
  });
});
