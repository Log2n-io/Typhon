import { create } from 'zustand';
import type { ArchetypeInfo } from '../data/archetypes';
import type { RealmView } from '../data/realm-view';
import type { SourceStats } from '../data/source';

/** A snapshot the renderer publishes four times a second for the HUD and the inspector. */

/** Where the drawn body's feet are, against where the collision rule says the ground is. */
export interface FeetReport {
  /** The lowest point of the drawn body, metres above sea level. */
  readonly bottomY: number;
  /** The height field under it. */
  readonly terrainY: number;
  /** The terrain raised by any published floor within a step — what it should be standing on. */
  readonly surfaceY: number;
  /** What the server says this body's altitude is. */
  readonly serverY: number;
  /** `bottomY - surfaceY`: zero when the feet are on the ground, positive when the body is in the air. */
  readonly gapM: number;
}

/**
 * What the HAM panel shows. Health is the only pool the game has: no action or mind until something spends them.
 */
export interface PlayerVitals {
  /** Health points, exact: the owner-only `health` (SELF), or `NaN` before it arrives. */
  readonly health: number;
  /** Health over its maximum, 0–1, from the replicated `hp` everyone sees (8 bits). */
  readonly fraction: number;
}

/** The avatar is down: knocked out (1) or dead (2), the server's `Downed`, and the seconds until it is up or cloned. */
export interface DownNotice {
  readonly kind: number;
  readonly seconds: number;
}

export interface LayerStats {
  /** The archetype this row is about, as the source's schema names it. */
  readonly archetype: ArchetypeInfo;
  readonly held: number;
  readonly near: number;
  readonly far: number;
  /**
   * The players held, by who drives them — `null` on every other row: the crowd the server simulates (no name), the bots
   * (their account says so, the replicated `bot`), and the people. Held only: the mesh and dot counts stay the row's.
   */
  readonly drivers: PlayerDrivers | null;
}

/** The players in view by who drives them. */
export interface PlayerDrivers {
  readonly crowd: number;
  readonly bots: number;
  readonly people: number;
}

/**
 * Counts the players of a store by who drives them: no `nameId` is the scripted crowd, `bot` 1 a bot of the fleet, the rest
 * people. Fields the schema does not carry count as absent: no names at all is all crowd.
 */
export function countDrivers(
  live: ArrayLike<number>,
  liveCount: number,
  nameIds: ArrayLike<number> | null,
  bots: ArrayLike<number> | null,
): PlayerDrivers {
  let crowd = 0;
  let botCount = 0;
  let people = 0;
  for (let i = 0; i < liveCount; i++) {
    const slot = live[i];
    if (nameIds === null || !(nameIds[slot] > 0)) {
      crowd++;
    } else if (bots !== null && bots[slot] === 1) {
      botCount++;
    } else {
      people++;
    }
  }

  return { crowd, bots: botCount, people };
}

export interface InspectedField {
  readonly name: string;
  readonly value: string;
}

export interface Inspection {
  /** The archetype's label, resolved from the source's schema: an index would mean nothing to the UI. */
  readonly archetype: string;
  readonly netId: number;
  readonly x: number;
  readonly z: number;
  /** Altitude, metres. Always 0 in a flat realm — a two-axis archetype has no third coordinate to report (CLI3D-04). */
  readonly altitudeM: number;
  /** Speed over the GROUND: a climb does not read as movement across the map. */
  readonly speedMps: number;
  readonly headingDeg: number;
  readonly epoch: number;
  /** False for an archetype without a position: x, z, speed, heading and epoch mean nothing. */
  readonly hasPosition: boolean;
  readonly fields: readonly InspectedField[];
}

/**
 * The engine's own account of this session's replication, from the `DEBUG` block.
 *
 * **Every number here is the server's, not the client's request**, which is the whole reason the row exists: a region
 * the engine clamped reports the shape it actually serves, and the near budget's estimate is the figure that decides
 * whether the radius survives the next tick.
 */
export interface ReplicationStats {
  /** `sphere`, `world` or `region`. */
  readonly shape: string;
  /** Cells delivered, and how many the window covers. */
  readonly deliveredCells: number;
  readonly windowCells: number;
  /** The replication cell side, metres. */
  readonly cellM: number;
  /** A region's near-budget estimate, and the budget; both 0 when the profile has none. */
  readonly held: number;
  readonly nearBudget: number;
  /** A sphere's R′ as the session holds it, and its LOD level; 0 for the other shapes. */
  readonly radiusM: number;
  readonly level: number;
  /** Whether the last frame carrying geometry completed the view. */
  readonly viewComplete: boolean;
}

export interface FrameStats {
  readonly fps: number;
  /** Main-thread time of the client's own per-frame work (evaluate, cull, pack, upload), mean over the last 128 frames. */
  readonly frameJsMs: number;
  /** 95th percentile of the same 128 frames. */
  readonly frameJsP95Ms: number;
  /** Babylon's own frame time (`SceneInstrumentation.frameTimeCounter`: scene evaluation and draw submission), last-second average. */
  readonly frameTotalMs: number;
  readonly drawCalls: number;
  readonly layers: readonly LayerStats[];
  /**
   * The far tier's channels, in the aggregate grid's own order, or empty when the session has no grid — which is what a
   * server started without `--god-region` gives, since the whole-world god profile declares no aggregate.
   */
  readonly heatChannels: readonly ArchetypeInfo[];
  readonly attackLines: number;
  readonly renderTime: number;
  readonly latestTick: number;
  readonly renderDelayMs: number;
  readonly altitude: number;
  /** The speed class Shift+1..4 selected: what the next movement intent will ask for. */
  readonly gait: number;
  /** The boost Shift+1..4 latched, ×1..×4. */
  readonly boost: number;
  /** The controlled avatar's vitals, or `null` without one (god camera, before SELF). */
  readonly vitals: PlayerVitals | null;
  /** How the controlled avatar is down and for how much longer, or `null` while it stands. */
  readonly down: DownNotice | null;
  /** What the server grants for {@link gait}, metres per second, or `NaN` before the owner stats arrive. */
  readonly gaitMps: number;
  /** Ground height under the camera target, metres — 0 while the terrain is still baking. */
  readonly groundM: number;
  /**
   * Where the avatar's lowest vertex is against the surface it should rest on, or `null` when none is drawn.
   *
   * <b>The instrument for "the character is flying".</b> That claim is about the distance between two numbers nobody
   * could read: the altitude the collision rule computed and where the mesh actually ended up. `gapM` is that
   * distance, measured off Babylon's own bounding boxes, so it answers the question wherever the body is standing.
   */
  readonly feet: FeetReport | null;
  /** How long the heightfield bake took, milliseconds; 0 until it lands. */
  readonly terrainBakeMs: number;
  /** Triangles the terrain drew last frame: the quadtree's selected nodes times the node grid. */
  readonly terrainTriangles: number;
  /** Quadtree nodes selected last frame — one instance each, all in one draw call. */
  readonly terrainNodes: number;
  /** The finest node the selection reached, in metres. */
  readonly terrainFinestM: number;
  /** True when the node budget, not the tolerance, decided the detail. */
  readonly terrainCapped: boolean;
  /**
   * The tolerance the terrain governor holds above the Detail slider, rendered pixels, because the frame rate is under 60
   * at the slider's — `null` while it holds the slider's own (`app/terrain-detail.ts`).
   */
  readonly terrainAutoPx: number | null;
  /**
   * The frame rate the governor found the machine capped at — coarser terrain did not raise it: a browser's battery saver,
   * a 30 or 50 Hz display — or `null`. While set, the terrain is held at the slider's own detail.
   */
  readonly terrainCappedFps: number | null;
  readonly nearRadius: number;
  /** The realm the session is in, or `null` in none — what the scene on screen is supposed to be. */
  readonly realm: RealmView | null;
  readonly source: SourceStats;
  /** Whether the source can stop the world: a live session only when the catalog declares the command for it. */
  readonly canPause: boolean;
  /** Whether the source can ask to look at another realm: a live session whose catalog declares `ViewRealm`. */
  readonly canViewRealm: boolean;
  /** Whether the source can ask to ride an entity: a live session whose catalog declares `Spectate`. */
  readonly canSpectate: boolean;
  /**
   * The netId of the entity this session is anchored on, or 0.
   *
   * <b>One number, deliberately.</b> A ride has two ids — the one the viewer clicked and the one the subject holds in
   * the view the server is now sending — and they are different, because anchoring re-sends the view. `ride.ts`
   * reconciles them; everything downstream compares against this. Reported at all because a still frame of a ridden
   * bot is otherwise indistinguishable from a god camera that happens to be low down.
   */
  readonly ridingNetId: number;
  /** What the server's `DEBUG` block says about this session, or `null` when it sends none (CLI3D-03). */
  readonly replication: ReplicationStats | null;
  readonly inspection: Inspection | null;
  /** The held entity count the HUD must agree with (M1-3): the store's, not the renderer's. */
  readonly held: number;
  readonly anomalies: number;
  /** What the local prediction of this session's own avatar is doing, or `null` when nothing is predicted (A14). */
  readonly prediction: PredictionStats | null;
}

/**
 * The prediction's own health, which is the whole reason it is reported.
 *
 * <b>Every way this can go wrong is visible in these three numbers and invisible otherwise.</b> A correction loop that
 * oscillates snaps periodically; a ring too small for the display's refresh rate stops reconciling and climbs `stale`; a
 * missing speed stat climbs `fallbacks`. The first version of this feature shipped with all three counted and none shown,
 * and a review found an instability by simulating the file that the running client would have displayed.
 */
export interface PredictionStats {
  /** How far ahead of the server's own figure the displayed body is, metres — the latency, as a distance. */
  readonly leadM: number;
  /** Times the prediction was jumped because the error passed what lag can explain. Should stay at 0. */
  readonly snaps: number;
  /** Frames that declined to predict for want of a replicated speed. Should stay at 0 after the first `SELF`. */
  readonly fallbacks: number;
  /** Frames whose history could not reach the authority's moment, so nothing was reconciled. Should stop climbing after warm-up. */
  readonly stale: number;
}

interface StatsState {
  readonly stats: FrameStats | null;
  readonly publish: (stats: FrameStats) => void;
}

export const useStats = create<StatsState>()((set) => ({
  stats: null,
  publish: (stats) => {
    set({ stats });
  },
}));
