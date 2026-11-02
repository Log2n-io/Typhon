import { afterEach, describe, expect, it } from 'vitest';
import type { Descriptor } from '../src/assets/descriptor';
import {
  canUseAt,
  craftingStationAt,
  craftingStationOf,
  craftMenuItems,
  STATION_REACH_M,
} from '../src/app/ticket-terminal';
import { useCraft } from '../src/state/craft-store';
import { useUi } from '../src/state/ui-store';

/**
 * The crafting station (crafting-build.md C2-C3; Loïc, 2026-11-02): the crafting panel opens only within a station's reach — the server's
 * `StationReachM`, not the asset's stand disc — from its context menu or <kbd>C</kbd>, and closes when the body walks away.
 */

/** The published `crafting_station-2` interaction and render box. */
const STATION = {
  interactions: [{ kind: 'crafting', pos: [0, 0, 1.4], rot: [0, 0, 0, 1], forward: '-Z', radius: 1.8 }],
  bounds: {
    render: [
      [-1.38, 0, -0.55],
      [1.35, 1.5, 0.79],
    ],
    collision: null,
  },
} as unknown as Descriptor;

describe('a crafting station', () => {
  afterEach(() => {
    useCraft.getState().reset();
    useUi.getState().closeContextMenu();
  });

  it('is usable within the server’s 6 m of where it stands, from any side, and not past it', () => {
    const use = craftingStationOf(STATION, 100, 0, 200, 1);
    expect(use).not.toBeNull();
    expect(STATION_REACH_M).toBe(6);
    expect(canUseAt(use!, 100, 205.9)).toBe(true);
    expect(canUseAt(use!, 94.1, 200)).toBe(true);
    expect(canUseAt(use!, 100, 206.1)).toBe(false);
    expect(craftingStationOf({ ...STATION, interactions: [] }, 0, 0, 0, 0)).toBeNull();
  });

  it('is found among the parts by kind: a bazaar in reach is no station', () => {
    const use = craftingStationOf(STATION, 0, 0, 0, 0)!;
    expect(craftingStationAt([{ kind: 'craft', use }], 3, 3)).toBe(true);
    expect(craftingStationAt([{ kind: 'bazaar', use }], 3, 3)).toBe(false);
    expect(craftingStationAt([{ kind: 'craft', use }], 6, 6)).toBe(false);
  });

  it('offers the panel from its menu, naming the key', () => {
    const items = craftMenuItems();
    expect(items).toHaveLength(1);
    expect(items[0].action).toEqual({ kind: 'craft' });
    expect(items[0].hint).toContain('C');
  });

  it('gates the panel: away from it neither the key nor the menu opens it, at it both do, and walking away closes it', () => {
    const craft = useCraft.getState;
    craft().toggle();
    useUi.getState().runContextAction({ kind: 'craft' });
    expect(craft().open).toBe(false);

    craft().setAtStation(true);
    useUi.getState().runContextAction({ kind: 'craft' });
    expect(craft().open).toBe(true);
    craft().toggle();
    expect(craft().open).toBe(false);
    craft().toggle();
    expect(craft().open).toBe(true);

    craft().setAtStation(false);
    expect(craft().open).toBe(false);
    craft().toggle();
    expect(craft().open).toBe(false);
  });
});
