import { create } from 'zustand';

/** One row of the navigation pane. */
export interface NavRow {
  /** The `StructureKind` it finds; also what its arrow is found by. */
  readonly kind: number;
  readonly label: string;
  /** Metres, rounded; `Infinity` when none is known. */
  readonly distanceM: number;
  /** Where it is (a click walks there), planet metres. */
  readonly x: number;
  readonly z: number;
}

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
import { NOT_TRAVELLING, type TravelState } from '../app/travel';

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

/**
 * A pending walk through a door: into the building `netId`, out of the room the body is in (0), or stop walking (-1).
 * Sequenced so the same ask twice is two asks.
 */
export interface DoorWalkRequest {
  readonly netId: number;
  readonly seq: number;
}

/** A pending ask to be flown to a city. */
export interface BoardRequest {
  /** The destination city's index in the client's own map data. */
  readonly city: number;
  readonly seq: number;
}

/**
 * A boarding refusal the source reported, held for one frame.
 *
 * <b>A sequence rather than a flag, so two refusals in a row are two.</b> A person who clicks a destination the server
 * will not accept tends to click it again; without the counter the second refusal would be indistinguishable from the
 * first still being on screen, and the panel would look stuck rather than answering.
 */
export interface ShuttleRefusal {
  /** The `ACKS` reason, or `travel.REASON_NOT_SENT` when the ask never left the client. */
  readonly reason: number;
  readonly seq: number;
}

/** The shuttleport the avatar is standing at, as the prompt shows it. */
export interface ShuttleportPrompt {
  /** The city the port serves. */
  readonly city: number;
  /** How far the avatar is from it, in metres, rounded — the prompt shows it and a jitter of centimetres is noise. */
  readonly distanceM: number;
  /** Whether the avatar stands at the port's ticket terminal, where the destination menu is opened (T). */
  readonly atTerminal: boolean;
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
   * The radius a PLAYER session asks to be served over, metres, or `null` until the server states its range.
   *
   * Separate from {@link viewRadius}, which is a god camera's region: the two have different mechanisms, different
   * ceilings and are never both in play for one session, so one number would have to mean whichever the session happens
   * to be — and the first panel to read it would get the other one's bound.
   */
  readonly playerRadius: number | null;
  /**
   * The range {@link playerRadius} may be set over, metres, or `null` while the server has not said.
   *
   * `def` is what the server serves before the slider is touched, which is a third number and not either end: the floor
   * is the narrowest a session may ask to be, and starting the slider there showed a radius the session did not have.
   */
  readonly playerRadiusRange: { readonly min: number; readonly max: number; readonly def: number } | null;
  /**
   * Heatmap channels by archetype NAME. Which channels exist is the aggregate grid's to say, and the server's grid holds
   * three (`Creature`, `CityNpc`, `Player`) where the client's old constant assumed four in a different order — so a
   * channel is addressed by name and an unlisted one is off.
   */
  readonly heatmap: NameFlags;
  readonly showGrid: boolean;
  /** Draw the `DEBUG` block the server sends: replication grid, delivered cells, session shape (CLI3D-03). */
  readonly showDebug: boolean;
  /**
   * Terrain geometric error the viewer accepts, in screen pixels. Smaller is finer ground and costs GPU.
   *
   * <b>Eight, because the quadtree's cost in triangles is roughly the inverse square of this.</b> Two pixels asks for
   * ground accurate to a quarter of the error eight allows, over the whole visible planet, and measured in game that is
   * 15 million triangles — a figure that has nothing to do with how the terrain looks at a metre of eye height and
   * everything to do with how far the horizon is. The slider still reaches 0.25 for anyone who wants to see what the
   * bake can do.
   */
  readonly terrainPixelError: number;
  /**
   * Whether the sun casts: buildings shadow each other and the ground, and the avatar shadows the ground.
   *
   * A toggle rather than a constant because it is the one scene setting with a GPU cost worth A/B-ing — a 2 048 map
   * re-rendered every frame over every structure the session holds — and because an impostor cannot cast, so what it
   * buys depends on how close the camera is to something real.
   */
  readonly shadows: boolean;
  /** `god` looks down on the world; `eye` stands in it, riding the selection (CLI3D-10). */
  readonly cameraMode: 'god' | 'eye';
  /**
   * Whether this session drives an avatar of its own (A10).
   *
   * <b>Eye mode needs something to ride, and since A10 that is no longer only the selection.</b> A god camera rides what
   * it has selected; a PLAYER rides its own avatar and typically has no selection at all. Without this flag the camera
   * actions below could only ask "is something selected", and so refused a player eye mode outright.
   */
  readonly playing: boolean;
  /**
   * Also draw the avatar where the SERVER says it is, as a translucent ghost beside the predicted body (A14, prediction.md § 5).
   *
   * <b>A diagnostic, and the answer to a fair objection.</b> Prediction makes the displayed position a fiction, in a demo
   * whose whole point is that the engine is the authority — so the fiction has to be inspectable. The gap between the two
   * bodies is the lead, which makes the latency something the demo SHOWS rather than only measures.
   */
  readonly showAuthoritative: boolean;
  readonly showLabels: boolean;
  readonly showAttacks: boolean;
  /**
   * Debug: draw the placed structures' portals (blue, with an arrow along the crossing), spawns (green), interactions
   * (magenta) and bot approach paths (yellow), and the player's collision body (white), from the same data the game uses.
   */
  readonly showGizmos: boolean;
  /** Debug: draw the colliders the server published and the prediction pushes against (orange; walls' tops redder). */
  readonly showColliders: boolean;
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
  /** A pending ask to be flown somewhere, or `null`. */
  readonly boardRequest: BoardRequest | null;
  /** A pending walk through a door, or `null`. */
  readonly doorWalkRequest: DoorWalkRequest | null;
  /** A pending walk to a point around the buildings (a navigation row clicked), or `null`. */
  readonly walkToRequest: { readonly x: number; readonly z: number; readonly seq: number } | null;
  readonly walkTo: (x: number, z: number) => void;
  /**
   * The navigation pane's rows: the nearest shuttleport, bazaar and mission terminal (`nav-finder.ts`), or empty when
   * there is nothing to navigate (no avatar, not on a planet). The arrows are turned by the frame loop, not from here.
   */
  readonly nav: readonly NavRow[];
  /** How long the scan that produced {@link nav} took, milliseconds — the browser's clock is coarser than 0.1 ms. */
  readonly navScanMs: number;
  /** How many held structures that scan looked at. */
  readonly navScanned: number;
  readonly setNav: (nav: readonly NavRow[], scanMs: number, scanned: number) => void;
  /**
   * The shuttleport the avatar is standing at, or `null` when it is not at one.
   *
   * <b>Found in the replicated view rather than in the client's map data</b>, because the ports are not in it: which
   * building of a city is its port is a draw the world generator made, so a client knows where one is only by having
   * been sent it. `app/shuttle.ts` does the looking, and the bot brain answers the same question the same way.
   */
  readonly shuttleport: ShuttleportPrompt | null;

  /**
   * How the journey is going, as the panel reports it.
   *
   * Written by the app from {@link nextTravel} once a frame rather than by the actions below, because every input it
   * reads except the ask itself comes from the server.
   */
  readonly travel: TravelState;

  /** The last boarding refusal, or null; drained by the app on the frame it feeds the machine. */
  readonly shuttleRefusal: ShuttleRefusal | null;
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
  /** Asks for a player radius, clamped to the server's range. Ignored until the range is known. */
  readonly setPlayerRadius: (radius: number) => void;
  /** Adopts the server's range, and starts at its own radius — which is what the session is already being served. */
  readonly setPlayerRadiusRange: (range: { min: number; max: number; def?: number } | null) => void;
  readonly toggleHeatmap: (name: string) => void;
  readonly setShowGrid: (on: boolean) => void;
  readonly setShowDebug: (on: boolean) => void;
  /** Sets the terrain's screen-space error budget, in pixels. */
  readonly setTerrainPixelError: (pixels: number) => void;
  readonly setShadows: (shadows: boolean) => void;
  readonly setCameraMode: (mode: 'god' | 'eye') => void;
  /** Tells the store this session has an avatar to ride; see {@link UiState.playing}. */
  readonly setPlaying: (playing: boolean) => void;
  /** Asks the server to fly the avatar to a city; refused unless it is standing at a port. */
  readonly boardShuttle: (city: number) => void;
  /** Walks the body into building `netId`, out of its room (0), or stops (-1). */
  readonly walkDoor: (netId: number) => void;
  /** Publishes the port the avatar is standing at, or `null`. Called by the frame loop on change only. */
  readonly setShuttleport: (shuttleport: ShuttleportPrompt | null) => void;
  /** Whether the ticket terminal's destination menu is open; movement keys are ignored while it is. */
  readonly ticketMenu: boolean;
  readonly setTicketMenu: (open: boolean) => void;

  /** Publishes this frame's travel state. */
  readonly setTravel: (travel: TravelState) => void;

  /** Records a refusal the source reported, for the app to drain on its next frame. */
  readonly shuttleRefused: (reason: number) => void;

  /** Clears a drained refusal, so one reason is fed to the machine exactly once. */
  readonly clearShuttleRefusal: () => void;
  readonly setShowAuthoritative: (showAuthoritative: boolean) => void;
  readonly setShowLabels: (on: boolean) => void;
  readonly setShowGizmos: (on: boolean) => void;
  readonly setShowColliders: (on: boolean) => void;
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

export const useUi = create<UiState>()((set, get) => ({
  population: 1,
  seed: 20260916,
  viewRadius: 1500,
  maxViewRadius: null,
  playerRadius: null,
  playerRadiusRange: null,
  heatmap: { Creature: true },
  showGrid: true,
  showDebug: false,
  terrainPixelError: 8,
  shadows: true,
  cameraMode: 'god',
  playing: false,
  showAuthoritative: false,
  showLabels: true,
  showAttacks: true,
  showGizmos: false,
  showColliders: false,
  layers: {},
  paused: false,
  latencyMs: 40,
  jitterMs: 25,
  selectedNetId: 0,
  follow: false,
  cameraRequest: null,
  realmRequest: null,
  spectateRequest: null,
  boardRequest: null,
  doorWalkRequest: null,
  walkToRequest: null,
  walkTo: (x, z) => {
    set((s) => ({ walkToRequest: { x, z, seq: (s.walkToRequest?.seq ?? 0) + 1 } }));
  },
  nav: [],
  navScanMs: 0,
  navScanned: 0,
  setNav: (nav, navScanMs, navScanned) => {
    set({ nav, navScanMs, navScanned });
  },
  shuttleport: null,
  travel: NOT_TRAVELLING,
  shuttleRefusal: null,
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

  setPlayerRadius: (radius) => {
    // Clamped here rather than at the slider, for the reason setViewRadius is: the server REFUSES a radius outside its
    // range instead of clamping it, so an unclamped ask is a slider that moves and a session that is not widened.
    set((s) => {
      const range = s.playerRadiusRange;
      if (range === null || !Number.isFinite(radius)) {
        return {};
      }

      return { playerRadius: Math.min(Math.max(Math.round(radius), range.min), range.max) };
    });
  },

  setPlayerRadiusRange: (range) => {
    set((s) => {
      if (range === null) {
        return { playerRadiusRange: null, playerRadius: null };
      }

      // The default is the server's when it states one and its floor otherwise, which is what every server published
      // before the figure existed.
      const def = Math.min(Math.max(range.def ?? range.min, range.min), range.max);
      return {
        playerRadiusRange: { min: range.min, max: range.max, def },
        // Starts at what the session is ALREADY SERVED — so adopting the range changes nothing until the person moves
        // the slider. An existing value is kept, clamped, so a re-fetch does not reset it.
        playerRadius: Math.min(Math.max(s.playerRadius ?? def, range.min), range.max),
      };
    });
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
    // Eye mode needs a subject, so entering it without one would strand the camera. A selection is one subject; a
    // player's own avatar is the other, and that second case is what A10 added.
    //
    // Refusing on the selection ALONE is what left a logged-in player in the god camera: a player has an avatar and no
    // selection, so the one request that mattered was dropped silently. Entering follow too is what makes it read as
    // "I am this creature" rather than "I am standing where it was".
    set((s) =>
      cameraMode === 'eye' && s.selectedNetId === 0 && !s.playing
        ? {}
        : { cameraMode, follow: cameraMode === 'eye' ? true : s.follow },
    );
  },
  setPlaying: (playing) => {
    set({ playing });
  },
  setShowAuthoritative: (showAuthoritative) => {
    set({ showAuthoritative });
  },
  setShowLabels: (showLabels) => {
    set({ showLabels });
  },
  setShowGizmos: (showGizmos) => {
    set({ showGizmos });
  },
  setShowColliders: (showColliders) => {
    set({ showColliders });
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
    //
    // UNLESS this session is playing, in which case the eye camera is on its own avatar and the selection was never what
    // it rode. A9 makes clicking empty ground a legitimate way to clear the target, and without this a player would be
    // thrown out of third person for doing it.
    set((s) => ({
      selectedNetId,
      follow: selectedNetId === 0 ? false : s.follow,
      cameraMode: selectedNetId === 0 && !s.playing ? 'god' : s.cameraMode,
    }));
  },
  setFollow: (follow) => {
    set({ follow });
  },
  setShadows: (shadows) => {
    set({ shadows });
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
  boardShuttle: (city) => {
    // A sequence for the reason every other request carries one: the server may refuse, so asking for the same city
    // twice has to send twice, and without it the second ask would be indistinguishable from no ask at all.
    set((s) => ({ boardRequest: { city, seq: (s.boardRequest?.seq ?? 0) + 1 } }));
  },
  walkDoor: (netId) => {
    set((s) => ({ doorWalkRequest: { netId, seq: (s.doorWalkRequest?.seq ?? 0) + 1 } }));
  },
  ticketMenu: false,
  setTicketMenu: (ticketMenu) => {
    set({ ticketMenu });
  },
  setShuttleport: (shuttleport) => {
    set({ shuttleport });
  },
  setTravel: (travel) => {
    // Compared by identity before publishing: the machine returns its own previous state when nothing moved, and a
    // store write every frame would re-render the panel sixty times a second for an unchanged queue.
    if (get().travel !== travel) {
      set({ travel });
    }
  },
  shuttleRefused: (reason) => {
    set((s) => ({ shuttleRefusal: { reason, seq: (s.shuttleRefusal?.seq ?? 0) + 1 } }));
  },
  clearShuttleRefusal: () => {
    if (get().shuttleRefusal !== null) {
      set({ shuttleRefusal: null });
    }
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
