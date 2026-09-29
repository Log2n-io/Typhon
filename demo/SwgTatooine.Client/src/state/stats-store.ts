import { create } from 'zustand';
import type { ArchetypeInfo } from '../data/archetypes';
import type { RealmView } from '../data/realm-view';
import type { SourceStats } from '../data/source';

/** A snapshot the renderer publishes four times a second for the HUD and the inspector. */

export interface LayerStats {
  /** The archetype this row is about, as the source's schema names it. */
  readonly archetype: ArchetypeInfo;
  readonly held: number;
  readonly near: number;
  readonly far: number;
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
  /** Ground height under the camera target, metres — 0 while the terrain is still baking. */
  readonly groundM: number;
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
  readonly nearRadius: number;
  /** The realm the session is in, or `null` in none — what the scene on screen is supposed to be. */
  readonly realm: RealmView | null;
  readonly source: SourceStats;
  /** Whether the source can stop the world: a live session only when the catalog declares the command for it. */
  readonly canPause: boolean;
  /** Whether the source can ask to look at another realm: a live session whose catalog declares `ViewRealm`. */
  readonly canViewRealm: boolean;
  /** What the server's `DEBUG` block says about this session, or `null` when it sends none (CLI3D-03). */
  readonly replication: ReplicationStats | null;
  readonly inspection: Inspection | null;
  /** The held entity count the HUD must agree with (M1-3): the store's, not the renderer's. */
  readonly held: number;
  readonly anomalies: number;
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
