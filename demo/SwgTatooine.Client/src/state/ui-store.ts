import { create } from 'zustand';

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

/** A set of archetype names held as a map, so a missing name reads as its default rather than as `false`. */
export type NameFlags = Readonly<Record<string, boolean>>;

export interface UiState {
  readonly population: Population;
  readonly seed: number;
  /** Requested radius of the near tier, metres. */
  readonly viewRadius: number;
  /**
   * The largest radius the server will honour, metres, or `null` while it is unknown.
   *
   * 0 is a real value and means "this server's camera has no client-driven region" — not the same as unknown, which is
   * why this is nullable rather than defaulting to a number.
   */
  readonly maxViewRadius: number | null;
  /**
   * Heatmap channels by archetype NAME. Which channels exist is the aggregate grid's to say, and the server's grid holds
   * three (`Creature`, `CityNpc`, `Player`) where the client's old constant assumed four in a different order — so a
   * channel is addressed by name and an unlisted one is off.
   */
  readonly heatmap: NameFlags;
  readonly showGrid: boolean;
  /** Draw the `DEBUG` block the server sends: replication grid, delivered cells, session shape (CLI3D-03). */
  readonly showDebug: boolean;
  /** Terrain geometric error the viewer accepts, in screen pixels. Smaller is finer ground and costs GPU. */
  readonly terrainPixelError: number;
  /** `god` looks down on the world; `eye` stands in it, riding the selection (CLI3D-10). */
  readonly cameraMode: 'god' | 'eye';
  readonly showLabels: boolean;
  readonly showAttacks: boolean;
  /** Per archetype NAME; an archetype the user has not touched is visible. */
  readonly layers: NameFlags;
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
  readonly setMaxViewRadius: (radius: number | null) => void;
  readonly toggleHeatmap: (name: string) => void;
  readonly setShowGrid: (on: boolean) => void;
  readonly setShowDebug: (on: boolean) => void;
  /** Sets the terrain's screen-space error budget, in pixels. */
  readonly setTerrainPixelError: (pixels: number) => void;
  readonly setCameraMode: (mode: 'god' | 'eye') => void;
  readonly setShowLabels: (on: boolean) => void;
  readonly setShowAttacks: (on: boolean) => void;
  readonly toggleLayer: (name: string) => void;
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
  maxViewRadius: null,
  heatmap: { Creature: true },
  showGrid: true,
  showDebug: false,
  terrainPixelError: 2,
  cameraMode: 'god',
  showLabels: true,
  showAttacks: true,
  layers: {},
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
    // Clamped here rather than at the slider, so nothing can ask for a radius the server would silently shrink.
    set((s) => ({ viewRadius: s.maxViewRadius === null ? viewRadius : Math.min(viewRadius, s.maxViewRadius) }));
  },

  setMaxViewRadius: (maxViewRadius) => {
    set((s) => ({
      maxViewRadius,
      viewRadius: maxViewRadius === null || maxViewRadius <= 0 ? s.viewRadius : Math.min(s.viewRadius, maxViewRadius),
    }));
  },
  toggleHeatmap: (name) => {
    set((s) => ({ heatmap: { ...s.heatmap, [name]: !(s.heatmap[name] ?? false) } }));
  },
  setShowGrid: (showGrid) => {
    set({ showGrid });
  },
  setShowDebug: (showDebug) => {
    set({ showDebug });
  },
  setCameraMode: (cameraMode) => {
    // Eye mode rides the selection, so entering it without one would strand the camera. Entering follow too is what
    // makes it read as "I am this creature" rather than "I am standing where it was".
    set((s) =>
      cameraMode === 'eye' && s.selectedNetId === 0
        ? {}
        : { cameraMode, follow: cameraMode === 'eye' ? true : s.follow },
    );
  },
  setShowLabels: (showLabels) => {
    set({ showLabels });
  },
  setShowAttacks: (showAttacks) => {
    set({ showAttacks });
  },
  toggleLayer: (name) => {
    set((s) => ({ layers: { ...s.layers, [name]: !(s.layers[name] ?? true) } }));
  },
  setPaused: (paused) => {
    set({ paused });
  },
  setLatency: (latencyMs, jitterMs) => {
    set({ latencyMs, jitterMs });
  },
  select: (selectedNetId) => {
    // One `set`, so one re-render. Losing the selection must leave eye mode — there is nothing left to ride — and doing
    // that in its own `set` first made every subscriber render twice for one click.
    set((s) => ({
      selectedNetId,
      follow: selectedNetId === 0 ? false : s.follow,
      cameraMode: selectedNetId === 0 ? 'god' : s.cameraMode,
    }));
  },
  setFollow: (follow) => {
    set({ follow });
  },
  setTerrainPixelError: (pixels: number) => {
    set({ terrainPixelError: pixels });
  },
  flyTo: (x, z, distance) => {
    set((s) => ({ cameraRequest: { x, z, distance, seq: (s.cameraRequest?.seq ?? 0) + 1 }, follow: false }));
  },
}));
