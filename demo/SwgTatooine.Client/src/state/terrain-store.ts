import { create } from 'zustand';

import type { TerrainPhase } from '../terrain/terrain-provider';
import type { TerrainBaked } from '../terrain/terrain.worker';

/**
 * The planet, and how far along its load is.
 *
 * <b>Its own store because its lifetime is the page's, not the session's.</b> The load starts at module scope, before
 * anyone has logged in and before `ClientApp` exists, and it survives a logout — the planet does not change when the
 * account does. Putting it in `ui-store` would tie a page-lifetime fact to a store the renderer re-reads every frame, and
 * putting it in component state would restart it whenever the viewport remounted.
 *
 * <b>The field itself lives here rather than being awaited at the point of use</b>, because the one consumer is a React
 * effect and an effect cannot await. Holding the settled value turns "has the planet arrived" into an ordinary render.
 */
export interface TerrainState {
  readonly phase: TerrainPhase;
  /** The planet, or `null` until it has loaded. */
  readonly baked: TerrainBaked | null;
  /** Whether it came from the local database rather than from the workers. */
  readonly fromCache: boolean;
  /** What the load took, milliseconds: a cache read, or the bake. */
  readonly loadMs: number;
  readonly setPhase: (phase: TerrainPhase) => void;
  readonly setLoaded: (baked: TerrainBaked, fromCache: boolean, loadMs: number) => void;
}

export const useTerrain = create<TerrainState>((set) => ({
  phase: 'idle',
  baked: null,
  fromCache: false,
  loadMs: 0,
  setPhase: (phase) => {
    // `ready` is published by setLoaded together with the field, so that no render ever sees "ready with nothing".
    set(phase === 'ready' ? {} : { phase });
  },
  setLoaded: (baked, fromCache, loadMs) => {
    set({ phase: 'ready', baked, fromCache, loadMs });
  },
}));
