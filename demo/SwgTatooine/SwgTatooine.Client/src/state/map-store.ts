import { create } from 'zustand';

/** Where the player stands on the planet, and which way the camera looks: yaw 0 faces +z (north on the map), growing towards +x. */
export interface MapPlayer {
  readonly x: number;
  readonly z: number;
  readonly yaw: number;
}

/**
 * The planet map (Loïc, 2026-11-02): whether it is open, and the player's marker on it.
 *
 * The marker is published by the frame loop (`ClientApp.updateMapPlayer`) only while the map is open, a few times a second: the map is
 * the only reader, and a store write per frame for a closed window would re-render nothing but still cost the loop. `null` when the
 * session has no avatar on the planet — a god camera, or a player inside a building or a dungeon.
 */
export interface MapState {
  readonly open: boolean;
  readonly player: MapPlayer | null;
  readonly toggle: () => void;
  readonly close: () => void;
  readonly setPlayer: (player: MapPlayer | null) => void;
}

/** How far the player moves, metres, or turns, radians, before its marker is published again: a standing player redraws nothing. */
export const MAP_MOVE_M = 0.5;
export const MAP_TURN_RAD = 0.02;

/** Whether two markers are the same as the map draws them: both absent, or within {@link MAP_MOVE_M} and {@link MAP_TURN_RAD}. */
export function samePlace(a: MapPlayer | null, b: MapPlayer | null): boolean {
  if (a === null || b === null) {
    return a === b;
  }

  return Math.hypot(a.x - b.x, a.z - b.z) < MAP_MOVE_M && Math.abs(a.yaw - b.yaw) < MAP_TURN_RAD;
}

export const useMap = create<MapState>((set, get) => ({
  open: false,
  player: null,
  toggle: () => {
    set((s) => ({ open: !s.open }));
  },
  close: () => {
    set({ open: false });
  },
  setPlayer: (player) => {
    if (!samePlace(get().player, player)) {
      set({ player });
    }
  },
}));
