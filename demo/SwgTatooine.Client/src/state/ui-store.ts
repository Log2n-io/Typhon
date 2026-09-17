import { create } from 'zustand';
import { ARCHETYPE_COUNT } from '../data/swg-schema';

/**
 * UI state only (`05-client.md` C4): toggles, the selection, what the user asked for. World data never enters here; the
 * renderer reads this store with `getState()` each frame and never re-renders React.
 */

export type Population = 1 | 4 | 16;

export interface CameraRequest {
  readonly x: number;
  readonly z: number;
  readonly distance: number;
  readonly seq: number;
}

export interface UiState {
  readonly population: Population;
  readonly seed: number;
  /** Requested radius of the near tier, metres. */
  readonly viewRadius: number;
  /** Heatmap channels: lairs, creatures, city NPCs, players (the grid's archetype order). */
  readonly heatmap: readonly boolean[];
  readonly showGrid: boolean;
  readonly showLabels: boolean;
  readonly showAttacks: boolean;
  /** Per archetype. */
  readonly layers: readonly boolean[];
  readonly paused: boolean;
  readonly latencyMs: number;
  readonly jitterMs: number;
  /** netId of the selected entity, 0 for none. */
  readonly selectedNetId: number;
  readonly follow: boolean;
  readonly cameraRequest: CameraRequest | null;

  readonly setPopulation: (population: Population) => void;
  readonly setSeed: (seed: number) => void;
  readonly setViewRadius: (radius: number) => void;
  readonly toggleHeatmap: (channel: number) => void;
  readonly setShowGrid: (on: boolean) => void;
  readonly setShowLabels: (on: boolean) => void;
  readonly setShowAttacks: (on: boolean) => void;
  readonly toggleLayer: (archetype: number) => void;
  readonly setPaused: (paused: boolean) => void;
  readonly setLatency: (latencyMs: number, jitterMs: number) => void;
  readonly select: (netId: number) => void;
  readonly setFollow: (follow: boolean) => void;
  readonly flyTo: (x: number, z: number, distance: number) => void;
}

export const useUi = create<UiState>()((set) => ({
  population: 1,
  seed: 20260916,
  viewRadius: 1500,
  heatmap: [false, true, false, false],
  showGrid: true,
  showLabels: true,
  showAttacks: true,
  layers: Array.from({ length: ARCHETYPE_COUNT }, () => true),
  paused: false,
  latencyMs: 40,
  jitterMs: 25,
  selectedNetId: 0,
  follow: false,
  cameraRequest: null,

  setPopulation: (population) => {
    set({ population, selectedNetId: 0, follow: false });
  },
  setSeed: (seed) => {
    set({ seed, selectedNetId: 0, follow: false });
  },
  setViewRadius: (viewRadius) => {
    set({ viewRadius });
  },
  toggleHeatmap: (channel) => {
    set((s) => ({ heatmap: s.heatmap.map((on, i) => (i === channel ? !on : on)) }));
  },
  setShowGrid: (showGrid) => {
    set({ showGrid });
  },
  setShowLabels: (showLabels) => {
    set({ showLabels });
  },
  setShowAttacks: (showAttacks) => {
    set({ showAttacks });
  },
  toggleLayer: (archetype) => {
    set((s) => ({ layers: s.layers.map((on, i) => (i === archetype ? !on : on)) }));
  },
  setPaused: (paused) => {
    set({ paused });
  },
  setLatency: (latencyMs, jitterMs) => {
    set({ latencyMs, jitterMs });
  },
  select: (selectedNetId) => {
    set((s) => ({ selectedNetId, follow: selectedNetId === 0 ? false : s.follow }));
  },
  setFollow: (follow) => {
    set({ follow });
  },
  flyTo: (x, z, distance) => {
    set((s) => ({ cameraRequest: { x, z, distance, seq: (s.cameraRequest?.seq ?? 0) + 1 }, follow: false }));
  },
}));
