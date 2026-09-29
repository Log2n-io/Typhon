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

import type { RealmDirectory } from '../data/server-config';
import { askedFor, IDLE, type TransitionState } from '../app/realm-transition';

/** A pending ask to look at another realm. The sequence makes a repeat of the same realm a new ask. */
export interface RealmRequest {
  readonly realmId: number;
  readonly seq: number;
}

/** A pending ask to ride an entity, or to stop riding. Sequenced for {@link RealmRequest}'s reason: the server may refuse. */
export interface SpectateRequest {
  /** The subject, or 0 to stop. */
  readonly netId: number;
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
  /**
   * A pending ask to look at another realm, or `null`.
   *
   * Carries a sequence for the same reason {@link CameraRequest} does, and one that bites harder here: the server may
   * REFUSE an ask, so picking the same realm twice has to send twice. Without the sequence the second pick would be
   * indistinguishable from no pick at all, and a viewer whose first attempt was rate-limited could never retry.
   */
  readonly realmRequest: RealmRequest | null;
  /** A pending ask to ride an entity, or `null`. */
  readonly spectateRequest: SpectateRequest | null;
  /** The realms this server says it has, or `null` from the mock and from a server that publishes none. */
  readonly realms: RealmDirectory | null;
  /**
   * The realm crossing now on screen.
   *
   * Held in the store rather than in `ClientApp` because the thing that draws it is React: the overlay has to cover the
   * DOM label layer as well as the canvas, which a scene post-process cannot do — it would leave a room's arrival
   * showing the previous planet's town names over it.
   */
  readonly transition: TransitionState;

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
  /** Asks the server to put this session in a realm. An ask: the server may refuse it. */
  readonly viewRealm: (realmId: number) => void;
  /** Asks to ride an entity, or to stop with 0. The selection is KEPT — see the action. */
  readonly spectate: (netId: number) => void;
  /** Puts the selection and the camera on the entity the server says this session is riding, in ONE update. */
  readonly rideSubject: (netId: number) => void;
  readonly setRealms: (realms: RealmDirectory | null) => void;
  readonly setTransition: (transition: TransitionState) => void;
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
  realmRequest: null,
  spectateRequest: null,
  realms: null,
  transition: IDLE,

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
  setRealms: (realms) => {
    set({ realms });
  },
  setTransition: (transition) => {
    set({ transition });
  },
  rideSubject: (netId) => {
    // One `set`, because the two halves are one fact. Done as `select` then `setCameraMode` it was two store updates
    // and two renders, and the first of them had `cameraMode: 'god'` still in place — a visible flip out of the eye
    // and back into it on every crossing, sixty times a second apart but on screen.
    set({ selectedNetId: netId, follow: false, cameraMode: 'eye' });
  },
  spectate: (netId) => {
    // The selection is NOT dropped here, and that is the difference from `viewRealm`. A crossing invalidates the
    // selection because the viewer is going somewhere else; a ride is the viewer saying "this one". The RESET that
    // follows drops it anyway — netIds are re-allocated for the new view — and `ClientApp` then re-selects the subject
    // from the `SELF` block the server fills for the entity it anchored the session on, which is the only source that
    // survives the reset.
    //
    // No fade either. There is no crossing to hide unless the subject is in another realm, and when it is the arrival
    // raises the server-initiated branch on its own.
    set((s) => ({
      spectateRequest: { netId, seq: (s.spectateRequest?.seq ?? 0) + 1 },
      cameraMode: netId === 0 ? 'god' : 'eye',
    }));
  },
  viewRealm: (realmId) => {
    // The selection is dropped here as well as on arrival: the entity it names belongs to the realm being left, and
    // between the ask and the answer the inspector would otherwise keep showing it as though it were still relevant.
    //
    // The fade starts on the ASK, not on the arrival, and that is the whole reason a client-initiated crossing looks
    // different from a door: this is the only moment the client knows a crossing is coming, so it is the only one where
    // there is still a world on screen to fade out of. The kind is taken as `travel` here because the destination's is
    // not known until it arrives; `arrived` re-reads it and a channel change simply ramps back in faster.
    set((s) => ({
      realmRequest: { realmId, seq: (s.realmRequest?.seq ?? 0) + 1 },
      selectedNetId: 0,
      follow: false,
      transition: askedFor(s.transition, 'travel', performance.now()),
    }));
  },
}));
