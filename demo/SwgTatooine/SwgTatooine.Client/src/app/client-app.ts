import { FreeCamera } from '@babylonjs/core/Cameras/freeCamera';
import { Engine } from '@babylonjs/core/Engines/engine';
import { SceneInstrumentation } from '@babylonjs/core/Instrumentation/sceneInstrumentation';
import { Color4 } from '@babylonjs/core/Maths/math.color';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { Scene } from '@babylonjs/core/scene';
import {
  AggregateGrid,
  archetypeOf,
  type GridSchema,
  Clock,
  epochAt,
  evaluateSlot,
  headingOf,
  MAX_MOTION_STRIDE,
  NOT_FOUND,
  slotOf,
  WorldStore,
} from '@typhondb/client';
import { configureDecoders } from '../assets/decoders';
import { CameraInput, isTyping } from '../camera/camera-input';
import { EyeCamera } from '../camera/eye-camera';
import { MapCamera } from '../camera/map-camera';
import { mountAssets, type AssetMount } from './asset-mount';
import { describeCatalogue, mountCreatureCrowd, mountCrowd, type CrowdMount } from './crowd-mount';
import { CrowdRouter } from './crowd-router';
import type { AssetLibrary } from '../assets/asset-library';
import type { Descriptor } from '../assets/descriptor';
import { SWOOP_ASSET } from './character-mount';
import { CrowdSwoops } from './crowd-swoops';
import { StaticAssets, type StructureSample } from './static-assets';
import { DoorWalker, pathIn, pathOut } from './door-walk';
import { findPath, wallGrid } from './city-path';
import { beginArrival, carryThrough, stepArrival, type DoorArrival, type DoorCarry } from './door-arrival';
import { crossedDoor, doorPlanes, spawnNear, type DoorPlane, type SpawnNear } from './door-crossing';
import {
  groundOnShapes,
  prepareShape,
  pushOut,
  type AssetShape,
  type PlacedShape,
  type Position,
} from './structure-collision';
import { meanAndP95, overviewPolicy, regionDue } from './frame-policy';
import { headingFromVelocity } from '../math/vec';
import { resolveArchetypes, type ArchetypeInfo, type ArchetypeView } from '../data/archetypes';
import { altitudeField, altitudeOf, Placement } from '../data/placement';
import { replicationStatsOf } from '../data/replication-stats';
import type { AssetSet } from '../data/server-config';
import { SpeedClass, type DataSource, type EventSink } from '../data/source';
import { describeFields } from '../data/swg-format';
import { AGG_GRID, FASTEST_TICK_PERIOD_MS, StructureKind, SWG_SCHEMA, TICK_PERIOD_MS } from '../data/swg-schema';
import { CITIES, PLANET_HALF_EXTENT_M } from '../data/world-data';
import { AttackLines } from '../render/attack-lines';
import { GameplayGizmos, type GizmoPlacement } from '../render/gameplay-gizmos';
import { placement } from '../assets/asset-frame';
import { assetForStructure, lairQuarter, structureYaw } from '../assets/structure-assets';
import {
  EntityLayer,
  type FrameView,
  type PickHit,
  type PickRay,
  type PredictedOverride,
} from '../render/entity-layer';
import { NONE_HIDDEN, type CrowdBody } from '../render/layer-packer';
import { enemySkins } from '../character/outfits';
import { FLAT_GROUND, type GroundSampler } from '../terrain/ground-sampler';
import { GroundView, SKY } from '../render/ground';
import { SceneLighting } from '../render/scene-lighting';
import { PlanetProfile } from '../render/planet-profile';
import { InteriorProfile } from '../render/interior-profile';
import { SpaceProfile } from '../render/space-profile';
import type { SceneProfile } from '../render/scene-profile';
import { Heightfield } from '../terrain/heightfield';
import type { TerrainBaked } from '../terrain/terrain.worker';
import { Labels } from '../render/labels';
import { SHUTTLEPORT_STYLE, styleFor } from '../render/styles';
import { RenderOrigin, extractFrustumPlanes, invertMatrix4, rayGroundT, unprojectRay } from '../render/view-math';
import { useChat } from '../state/chat-store';
import { crossingKindOf, isIndoors, sameRealm, type RealmView, type SceneKind } from '../data/realm-view';
import { advance, arrived, askedFor, blackout, opacityAt } from './realm-transition';
import {
  flightAt,
  shuttleMoment,
  type FlightPose,
  type ShuttleMoment,
  type ShuttleSchedule,
  type Writable,
} from './shuttle-flight';
import { ShuttleView } from './shuttle-view';
import { ticketTerminalOf } from './ticket-terminal';
import { resetForRealm, resetForSession, type RealmSwitchTargets } from './realm-switch';
import { nextRide, NOT_RIDING, rideOwnsTheView, rideSubjectOf, type RideState } from './ride';
import { boostFor, DEFAULT_BOOST } from './gait';
import { driveInputOf, SwoopDrive } from './swoop-drive';
import { SwoopHover } from './swoop-hover';
import {
  gaitChanged,
  boostOf,
  throttleByte,
  throttleOf,
  gaitOf,
  intentChanged,
  movementIntent,
  REFRESH_MS,
  stopAfter,
  type MoveIntent,
} from './player-intent';
import { Predictor, speedForClass, type GrantedSpeeds } from './predictor';
import { DEFAULT_THIRD_PERSON_M, MAX_EYE_DISTANCE_M } from '../camera/eye-camera';
import { bodyOpacityAt, marchEye } from '../camera/eye-clearance';
import { useStats, type FeetReport, type Inspection } from '../state/stats-store';
import { useUi, type NavRow, type ShuttleportPrompt, type UiState } from '../state/ui-store';
import { NAV_KINDS, navLabel, nearestFacilities, relativeBearing, type NavTarget } from './nav-finder';
import { nearestShuttleport } from './shuttle';
import { nextTravel } from './travel';

/**
 * How far from the camera a structure is drawn as its published asset, in metres.
 *
 * Past this it is the renderer's own shape — the box every structure was before the asset pipeline. The figure used to
 * be 220 m, about a city block, because a placement cost its FULL geometry however small it was on screen; a town's
 * worth of them at full detail was not affordable and the box was the honest fallback.
 *
 * <b>Levels of detail are what make this figure a different question.</b> A `tat_house_large` past 166 m is its 721
 * triangle LOD2, a thirty-ninth of what it was, so the range is now set by how far a session HOLDS structures rather
 * than by what they cost. 1 280 m is the demo's own `playerMaxRadiusM`: past it the server is not sending them, so
 * nothing is drawn as a box that could have been drawn as a building.
 */
const STATIC_ASSET_RANGE_M = 1280;

const STATS_INTERVAL_MS = 250;

/** How long the client draws its own mount ask before the server's replicated `mounted` decides: a round trip and a few ticks. */
const MOUNT_ANSWER_MS = 1500;

/** How often the navigation pane rescans for the nearest facilities: five times a second is a walk of a metre or two. */
const NAV_INTERVAL_MS = 200;
const REGION_INTERVAL_MS = 200;
const PICK_RADIUS_PX = 8;
const FRAME_SAMPLES = 128;
const ERROR_LOG_INTERVAL_MS = 5000;

/**
 * How much of a tick a gait change needs in front of it to name that tick, on top of the round trip (A14b).
 *
 * The socket write and the server's own drain are what this floor covers: measured on loopback at 10 Hz, a transition
 * past about 0.96 of a tick still missed the boundary at an RTT of half a millisecond. Inside the margin the client names
 * the next boundary instead and waits for it, which costs this many milliseconds of input lag on that fraction of presses
 * and is the cheaper side of the trade against being one whole step wrong.
 */
const SEND_MARGIN_MS = 5;
/**
 * Largest device pixel ratio rendered: a 1080p laptop at 150 % would otherwise shade the full-screen ground at 2880 × 1620.
 * Babylon applies its own `limitDeviceRatio` only at construction, so the client sets the scaling itself on every resize.
 */
const MAX_DEVICE_RATIO = 1.5;

/**
 * How far the pick's ground march looks, in metres.
 *
 * A diagonal of the planet plus its relief. Beyond it the march gives up and reports no ground, which leaves the pick
 * exactly as permissive as it was before the march existed — the safe direction to fail in.
 */
const PICK_MAX_DISTANCE_M = 32000;

/**
 * Stands in for an aggregate grid the session does not have. A live server declares one only under `--god-region`; the
 * ground shader needs a grid's extent whatever happens, and one empty cell shows nothing rather than crashing the frame.
 */
const NO_GRID: GridSchema = { index: 0, origin: [0, 0], cell: 1, dims: [1, 1], archetypes: [] };

/** Whether two resolved schemas name the same archetypes, in the same order. */
function sameArchetypes(a: ArchetypeView, b: ArchetypeView): boolean {
  return a.count === b.count && a.infos.every((info, index) => info.name === b.infos[index].name);
}

/**
 * The edge of an interior realm, metres — `WorldBuilder.InteriorEdgeM`.
 *
 * The room mesh is built at this size once and scaled per realm, so a server whose interiors are a different size still
 * draws correctly; this only decides what the geometry is authored at.
 */
const INTERIOR_EDGE_M = 64;

/**
 * The planet the client opens on, before any `REALM` block has arrived.
 *
 * Not a placeholder: a live session's first frame carries its realm, but the mock never sends one and the first second
 * of any session is drawn before the socket has answered. This is the world those frames are of.
 */
const PLANET_AT_START: RealmView = {
  scene: 'planet',
  realmId: 0,
  generation: 0,
  key: '0:0',
  appTag: 0,
  palette: 0,
  placeSet: 0,
  slot: 0,
  deep: false,
  cellM: 64,
  minX: -PLANET_HALF_EXTENT_M,
  maxX: PLANET_HALF_EXTENT_M,
  minY: 0,
  maxY: 0,
  minZ: -PLANET_HALF_EXTENT_M,
  maxZ: PLANET_HALF_EXTENT_M,
  centreX: 0,
  centreZ: 0,
  halfX: PLANET_HALF_EXTENT_M,
  halfZ: PLANET_HALF_EXTENT_M,
};

/** {@link LayerStats} as this app holds it: the readonly face is what the store publishes. */
interface MutableLayerStats {
  archetype: ArchetypeInfo;
  held: number;
  near: number;
  far: number;
}

/** What a data source fills: the SDK world the renderer reads. */
export interface SourceContext {
  readonly world: WorldStore;
  readonly grid: AggregateGrid;
  readonly clock: Clock;
  readonly events: EventSink;
}

/** Builds the data source for the current UI settings; called again when the population or the seed changes. */
export type SourceFactory = (context: SourceContext, ui: UiState) => DataSource;

class MutableFrameView implements FrameView {
  renderTick = 0;
  renderFrac = 0;
  originX = 0;
  originZ = 0;
  eyeX = 0;
  eyeY = 0;
  eyeZ = 0;
  planes = new Float64Array(24);
  pixelsPerMetre = 1;
  viewportWidth = 1;
  viewportHeight = 1;
  selectedNetId = 0;
  ground: GroundSampler = FLAT_GROUND;
  predicted: PredictedOverride | null = null;
  hidden: ReadonlySet<number> = NONE_HIDDEN;
  crowd: CrowdBody[] | null = null;
  crowdDraws = false;
  crowdCount = 0;
  perSecond = 1;
}

/** Reused each frame so a predicted body costs no allocation; the packer only reads it. */
class MutablePredictedOverride implements PredictedOverride {
  netId = 0;
  x = 0;
  z = 0;
}

/**
 * The client, outside React: owns the Babylon engine, the SDK world (store, grid, clock), the data source, the layers and
 * the camera, and runs the frame. React mounts it on a canvas and talks to it only through the UI store.
 *
 * One frame: advance the clock → apply held keys, follow the selection → move the camera → pick the render origin →
 * update the view matrices → evaluate, cull and pack every layer → attack lines, ground, labels → Babylon draws.
 */
/**
 * What the render clock is told, and the one value here that is not a default.
 *
 * <b>`minDelayMs` is the floor on the interpolation buffer, and the SDK's 150 ms is a floor for the internet, not for
 * this.</b> The clock already computes `period + p95(jitter)`, which is the honest answer — one tick, plus however
 * unevenly frames actually arrive — and then clamps it up to 150. On a server on the same machine the measured answer is
 * about one tick and the clamp was the whole delay: 150 ms of latency added to every key press and every release, on top
 * of the round trip, for jitter that was not there.
 *
 * The floor is the FASTEST period this client supports, not the one it happens to have been compiled with, because the
 * clock's own formula already produces the right answer: `period + p95(jitter)`, where the period is the server's real one
 * once `Clock.onPeriodChange` has been seeded from the catalog. A floor set from a guessed rate is the binding constraint
 * whenever the guess is too slow — which is what kept a 25 Hz server on a 100 ms buffer it did not need.
 *
 * `initialDelayMs` stays at two of the DEFAULT period: it applies only until the first frames arrive, and being generous
 * there costs one settling second and avoids a visible snap on a connection that turns out to be jittery.
 *
 * Passed by the APP rather than changed in the SDK: a default that suits one deployment is not a default.
 */
const CLOCK_OPTIONS = {
  tickPeriodMs: TICK_PERIOD_MS,
  minDelayMs: FASTEST_TICK_PERIOD_MS,
  initialDelayMs: 2 * TICK_PERIOD_MS,
};

/** How close to a shuttleport the prompt appears, in metres, before the server has published its own boarding radius (`demo.json` shuttle.boardM). */
const SHUTTLE_PROMPT_M = 30;

/** At most one swoop intent per this many milliseconds while it curves; see `sendMovementIntent`. */
const DRIVE_SEND_MS = 50;

/** No hands on the swoop: what it gets while the camera is elsewhere or a menu holds the keys. */
const PARKED_HANDS = { throttle: false, brake: false, steer: 0 } as const;

/** How far a click-to-walk is searched round the buildings before it goes straight, metres; see `walkTo`. */
const WALK_SEARCH_M = 300;

/** How far away a port's shuttle is drawn flying in and out, metres: the arrival starts 190 m out and 95 m up. */
const SHUTTLE_VIEW_M = 1200;

/** The published shuttleport, whose ticket terminal child is where a ticket is bought. */
const SHUTTLEPORT_ASSET = 'tat_shuttleport';

/** The published ticket terminal. */
const TICKET_TERMINAL_ASSET = 'ticket_terminal';

/** Frames between port searches; see ClientApp.updateShuttlePrompt. */
const SHUTTLE_SEARCH_FRAMES = 8;

/**
 * The archetype that holds every structure, by the name the catalog gives it.
 *
 * Two things ask for it - the shuttleport prompt's field scan and the dome layer - and both must mean the same one.
 */
const STRUCTURE_ARCHETYPE = 'WorldObject';

/**
 * The archetypes whose bodies are drawn as rigged characters rather than as boxes.
 *
 * <b>`Creature` is one of them since the creature pack was published.</b> A creature replicates its template and skin;
 * the router (`crowd-router.ts`) sends a camp's ninjas and soldiers to the characters' crowd in their outfits and every
 * beast to its own species' crowd. Before, a creature was a box: the catalogue held nineteen humanoids and no animals.
 */
const CHARACTER_ARCHETYPES: readonly string[] = ['CityNpc', 'Player', 'Creature'];

/** The most of one creature species drawn at once. A lair holds four to eight; a view holds a few dozen lairs. */
const SPECIES_CAPACITY = 512;

/**
 * The most bodies the crowd will draw at once, per outfit.
 *
 * Buffers are sized once from this, per outfit per detail level, so it is paid in memory whether or not the bodies
 * arrive: nineteen outfits at 2 048 is about 16 MB of Float32Array. Bodies past it in a frame are simply not drawn by
 * the crowd — they keep their sprite — which is the right failure for a figure that only a pathological view reaches.
 */
const CROWD_CAPACITY = 2048;

export class ClientApp {
  private readonly canvas: HTMLCanvasElement;
  private readonly createSource: SourceFactory;
  private readonly engine: Engine;
  private readonly scene: Scene;
  private readonly camera: FreeCamera;
  private readonly instrumentation: SceneInstrumentation;
  private readonly mapCamera = new MapCamera();
  private readonly eyeCamera = new EyeCamera();
  private readonly input: CameraInput;
  private readonly origin = new RenderOrigin();
  private readonly view = new MutableFrameView();

  /** A14: the locally predicted avatar, and the scratch record the packer reads it through. */
  private readonly predictor = new Predictor();
  private readonly predictedOverride = new MutablePredictedOverride();
  private readonly groundView = new GroundView();
  /** One per archetype of the CURRENT world's schema, rebuilt when a source brings a different one. */
  private layers: EntityLayer[];
  /**
   * The backdrop for each kind of realm, built lazily and kept.
   *
   * The planet's is built at start-up because that is the world this client opens on; the others appear the first time
   * a session enters one and then stay, hidden. See {@link SceneProfile} for why none of them is ever disposed on a
   * switch — the planet's terrain alone is 134 MB and a 13 s bake.
   */
  private readonly profiles = new Map<SceneKind, SceneProfile>();
  private readonly planetProfile: PlanetProfile;
  private profile: SceneProfile;
  /** The planet's heightfield: the one source of ground height for everything, filled before the first frame. */
  private readonly field: Heightfield;
  /** Retained across clicks: the inverse view-projection, the ray through the cursor, and the winning hit. */
  private readonly pickInverse = new Float64Array(16);
  private readonly pickRay: PickRay = { ox: 0, oy: 0, oz: 0, dx: 0, dy: 0, dz: 1 };
  private readonly pickBest: PickHit = { archetype: -1, netId: 0, pixels: Infinity, depth: Infinity, inside: false };
  /** How long the bake took, reported in the HUD's session block. On a cache hit this is the ORIGINAL bake's cost. */
  private terrainBakeMs = 0;
  private readonly attacks: AttackLines;
  private readonly labels: Labels;
  private readonly resizeObserver: ResizeObserver;

  private world: WorldStore;
  private grid: AggregateGrid;
  private clock: Clock;
  private source: DataSource | null = null;
  /** Which archetype is which in {@link world}'s schema: names, labels and the index lookup (`data/archetypes.ts`). */
  private archetypes: ArchetypeView;
  /** The far tier's channels, in the grid's own order. */
  private heatChannels: readonly ArchetypeInfo[] = [];

  /**
   * The HUD's per-archetype rows, one object each for the life of a schema.
   *
   * Rebuilt only in {@link adopt}, where the archetypes themselves change; `publishStats` rewrites the numbers into it.
   */
  private layerStats: MutableLayerStats[] = [];
  /** {@link heatChannels} as the ground shader wants it, refilled in place each frame rather than rebuilt. */
  private heatMask: boolean[] = [];

  /** Canvas size in CSS pixels, kept by the resize observer: reading layout every frame would be a forced reflow. */
  private readonly cssSize = { width: 1, height: 1 };
  /** Rendered pixels per CSS pixel, as {@link resize} last set it on the engine. */
  private renderScale = 1;
  /** {@link pickRay} shifted back into PLANET space, which is the frame the heightfield is sampled in. */
  private readonly pickPlanetRay: PickRay = { ox: 0, oy: 0, oz: 0, dx: 0, dy: 0, dz: 1 };
  /** The canvas or the device pixel ratio changed: resized at the top of the next frame, never between a draw and its paint. */
  private sizeDirty = true;
  private ratioQuery: MediaQueryList | null = null;
  private readonly onRatioChange = (): void => {
    this.sizeDirty = true;
    this.watchDeviceRatio();
  };
  private lastFrameMs = performance.now();
  private lastStatsMs = 0;
  private lastRegionMs = Number.NEGATIVE_INFINITY;
  private lastErrorLogMs = Number.NEGATIVE_INFINITY;
  private suppressedErrors = 0;
  private regionX = Number.NaN;
  private regionZ = Number.NaN;
  private regionRadius = Number.NaN;
  private readonly frameJs = new Float64Array(FRAME_SAMPLES);
  private frameJsNext = 0;
  private frameJsCount = 0;
  private readonly scratch = new Float64Array(MAX_MOTION_STRIDE);
  /** The one reader that knows where altitude and velocity sit for a 2- or 3-axis store (CLI3D-04). */
  private readonly placement = new Placement();
  private readonly sortScratch = new Float64Array(FRAME_SAMPLES);
  private readonly frameJsSummary = new Float64Array(2);
  /** The selection's evaluated planet position this frame, valid when {@link locateSelected} returned true. */
  private selectedX = 0;
  private selectedZ = 0;
  /** The selection's altitude and facing, for the eye camera (CLI3D-10). */
  private selectedY = 0;
  private selectedHeading = 0;

  /**
   * The EYE SUBJECT's evaluated position, which since A10 is not the selection's.
   *
   * <b>A slot of its own, because a player's camera rides their avatar while the selection is whatever they clicked.</b>
   * Both used to be evaluated into `selectedX/Y/Z/Heading`, the eye subject second — so for a playing session the
   * selection marker and its label were drawn at the player's own body, with the player's own label, whatever they had
   * clicked. The god camera never saw it, because there the two subjects are the same entity.
   */
  private eyeX = 0;
  private eyeZ = 0;
  private eyeY = 0;
  private eyeHeading = 0;
  /** Whose eyes the eye camera is in; 0 for nobody. A change is a cut, not a glide. */
  private eyeSubject = 0;

  /**
   * The boost Shift+1..4 last selected, ×1..×4, which every movement intent is sent at — walk, run or swoop.
   *
   * <b>Latched here rather than derived from the held keys, because it is a MODE and not a chord.</b> `movementIntent` is
   * pure in the keys currently down — which is what makes it testable — and a mode is by definition not in them. It is
   * also why this is a key DOWN listener and not a per-frame poll of `heldKeys`: a tap shorter than a frame is a tap the
   * person made, and a poll at 60 Hz would silently lose it.
   *
   * Per app, so it survives a logout and a realm crossing and is reset by a reload, which is the lifetime of everything
   * else about how this session is being played.
   */
  private boost: number = DEFAULT_BOOST;

  /** The gait shortcut's listener, which is this class's own and not the camera's. */
  private readonly gaitKeys = new AbortController();

  /** The movement intent last sent, so an unchanged one is not re-sent every frame (A9). */
  private lastIntent: MoveIntent | null = null;

  /** When it was sent, so a held key is refreshed before the server's destination is reached. */
  private lastIntentMs = 0;

  /**
   * Whether the camera has been put behind the avatar for this session yet (A10).
   *
   * <b>Once per session, not once per frame.</b> The default has to be third person — A10 says so, and first person on a
   * body you have just created tells you nothing about it — but it must be a STARTING point rather than a value the app
   * keeps restoring, or the wheel and the camera-mode button would both snap back on the next frame.
   */
  private playerCameraReady = false;

  /**
   * The realm the scene is currently set up for: what {@link adoptSourceRealm} compares against each frame.
   *
   * Held as the view rather than the frame so the comparison is on `key` — `realmId:generation` — and a realm id
   * reused by a later realm is not mistaken for the one it replaced.
   */
  private realm: RealmView | null = null;
  /** The source's `resetCount` as of the last frame: what {@link adoptSourceReset} compares against. */
  private resetCount = 0;
  /** Whether this session is anchored on an entity, and on which one; see {@link nextRide}. */
  private ride: RideState = NOT_RIDING;
  /** When the last release was asked for, so a subject that stays gone is retried at the command rate, not the frame rate. */
  private lastReleaseMs = Number.NEGATIVE_INFINITY;
  /** The altitude of whichever camera drew the last frame, for the HUD. */
  private activeAltitude = 0;

  /** The altitude the predicted body is standing at, carried frame to frame; see {@link groundUnder}. */
  private predictedFeet = Number.NaN;

  /** The realm {@link predictedFeet} was last worked out in; a change means the body just went through a door. */
  private predictedRealm = -1;

  /** Whether the feet are still the server's, since the body arrived in this realm; see `advancePrediction`. */
  private feetFromServer = true;

  /** Where the body stood when it arrived in this realm; moving off it hands the feet back to the prediction. */
  private arrivedX = 0;
  private arrivedZ = 0;

  private selectionText = '';
  /** The netId and archetype {@link selectionText} was built for: netIds are reused, by another archetype too. */
  private selectionTextNetId = 0;
  private selectionTextArchetype = -1;
  /**
   * The published assets, once mounted, or `null` on a run whose server announced none.
   *
   * Held so that `dispose` releases the containers and the environment texture: a reload that leaves them behind leaks
   * the whole GLB set, which is the largest single allocation this client makes.
   */
  private assets: AssetMount | null = null;

  /**
   * The scene's sun and its environment probe, for every lit surface: the terrain, and the published assets once they
   * mount.
   *
   * <b>Created here rather than by {@link mountAssets}, which is where it used to live.</b> That was right while the
   * buildings were the only lit thing in the scene and wrong the moment the terrain became a `PBRMaterial`, because
   * mounting assets is allowed to fail and be swallowed — a 404 on the catalogue, a decoder that will not load — and
   * none of those is a reason for the desert to go black. See {@link SceneLighting}.
   */
  private readonly lighting: SceneLighting;

  /**
   * Every replicated body drawn as a character, or `null` before the catalogue lands and on a run that publishes none.
   *
   * Held rather than reached through the asset mount because it outlives a failed mount the way the lighting does: a
   * crowd that cannot be built is a run with boxes, not a run with nothing.
   */
  private crowd: CrowdMount | null = null;

  /** The swoops under the crowd's riders; see `crowd-swoops.ts`. */
  private crowdSwoops: CrowdSwoops | null = null;

  /** Reused each frame: the bodies the character layers diverted, handed to the crowd and then cleared. */
  private readonly crowdBodies: CrowdBody[] = [];

  /** Each creature species' own crowd, in the router's layer order after the characters. */
  private creatureCrowds: CrowdMount[] = [];

  /** Which crowd draws each diverted body, and in what; `null` until the crowds are mounted. */
  private crowdRouter: CrowdRouter | null = null;

  /** The lairs in range, drawn as their published props: a nest, a hideout, a camp. */
  private lairAssets: StaticAssets | null = null;

  /** Reused each frame: the lairs in range. */
  private readonly lairSamples: StructureSample[] = [];
  /** Set by {@link dispose}, read by {@link gone}: what tells an in-flight asset load that there is nothing to mount on. */
  private disposed = false;
  private readonly unsubscribe: () => void;

  /**
   * @param canvas The canvas the engine renders into.
   * @param overlay The DOM layer the labels are drawn on.
   * @param createSource Builds the session this app drives.
   * @param terrain The planet, **already baked**. See {@link terrain/terrain-provider} for why it is a parameter.
   */
  constructor(canvas: HTMLCanvasElement, overlay: HTMLElement, createSource: SourceFactory, terrain: TerrainBaked) {
    this.canvas = canvas;
    this.createSource = createSource;
    this.engine = new Engine(canvas, true, {
      stencil: false,
      preserveDrawingBuffer: false,
      powerPreference: 'default',
    });
    this.scene = new Scene(this.engine);
    // The camera input owns the canvas: Babylon's own pointer handling would only pick meshes we never let it pick.
    this.scene.detachControl();
    this.scene.clearColor = new Color4(SKY.r, SKY.g, SKY.b, 1);
    this.scene.skipPointerMovePicking = true;
    this.scene.skipFrustumClipping = true;
    this.camera = new FreeCamera('camera', Vector3.Zero(), this.scene);
    this.camera.fov = this.mapCamera.fov;
    this.camera.inputs.clear();
    this.instrumentation = new SceneInstrumentation(this.scene);
    this.instrumentation.captureFrameTime = true;
    // Before the profiles, so no frame is ever drawn by a material that compiled against a scene with no sun and no
    // environment. `configureDecoders` is pure configuration and idempotent; `mountAssets` calls it again for the
    // decoders themselves, and this needs it only for the vendored `.env` path.
    this.lighting = new SceneLighting(this.scene, configureDecoders());

    // The ground is REAL before anything reads it, and that is a correctness property rather than a nicety.
    //
    // It used to start flat and gain relief about a second later, when the worker's bake landed. Everything that samples
    // the ground in between was answered with zero while the server's altitudes were the true ones — and the eye camera's
    // clearance probe, which walks the pull-back ray and stops before the ground, read a spawn 20 m below sea level as
    // entirely underground and collapsed third person to first. The player spent the first two seconds inside their own
    // avatar, under a plane that was not there. Filling the field here means no frame, no camera and no altitude query
    // ever sees the placeholder.
    this.field = new Heightfield();
    this.view.ground = this.field;
    this.eyeCamera.ground = this.field;
    this.adoptTerrain(terrain);
    this.planetProfile = new PlanetProfile(this.scene, this.field, PLANET_HALF_EXTENT_M, this.lighting);
    this.profiles.set('planet', this.planetProfile);
    this.profile = this.planetProfile;
    this.planetProfile.enter(PLANET_AT_START);
    // The per-node error the bake measured: what the pixel tolerance is spent against. Measured off the main thread
    // because it is a pass over 4.19 M posts per level and the frame budget is 2 ms.
    this.planetProfile.refreshTerrain(terrain.nodeError, terrain.nodeMinY, terrain.nodeMaxY);
    this.attacks = new AttackLines(this.scene);
    this.labels = new Labels(overlay);

    this.world = new WorldStore(SWG_SCHEMA);
    this.grid = new AggregateGrid(AGG_GRID);
    this.clock = new Clock(CLOCK_OPTIONS);
    this.archetypes = resolveArchetypes(SWG_SCHEMA);
    this.layers = this.buildLayers();
    this.bindLayers();

    const home = CITIES[0];
    this.mapCamera.jumpTo(home.x, home.z, 1800);

    this.input = new CameraInput(
      canvas,
      this.mapCamera,
      this.cssSize,
      {
        onClick: (x, y) => {
          const netId = this.pickAt(x, y);
          useUi.getState().select(netId);

          // A9: clicking something is also how a player attacks it. The selection and the target move together on
          // purpose — the inspector shows what you clicked and the avatar shoots at it, which is one gesture and one
          // mental model. The server refuses a netId this session was never shown and one whose entity is dead, so a
          // click on empty ground (0) is a legitimate clear rather than something to filter here.
          const live = this.source;
          if (live !== null && live.canPlay) {
            live.setTarget(netId);
          }
        },
        onManualMove: () => {
          if (useUi.getState().follow) {
            useUi.getState().setFollow(false);
          }
        },
      },
      this.eyeCamera,
      // Eye mode IS the condition — no second opinion about what makes it legal. `setCameraMode` already refuses eye
      // without a subject, and a player's subject is its avatar rather than a selection, so re-testing the selection here
      // routed every wheel and drag of a playing session to the MAP camera: the look controls silently drove a camera
      // that was not on screen, which reads as a dead mouse wheel.
      () => useUi.getState().cameraMode === 'eye',
    );

    // The gait shortcuts. On `window` like the camera's keys, with the same typing guard — Shift+2 in the chat box is a
    // quotation mark, not a boost — and on this class's own AbortController so disposing the app takes it with it.
    window.addEventListener(
      'keydown',
      (e) => {
        if (isTyping(e)) {
          return;
        }

        // The ticket terminal: T opens the destination menu where the avatar stands at one. Not E, the testbench's habit:
        // Q, E, R and F turn and pitch this client's camera.
        if (e.code === 'KeyT' && !e.repeat && useUi.getState().shuttleport?.atTerminal === true) {
          e.preventDefault();
          useUi.getState().setTicketMenu(true);
          return;
        }

        // The swoop: the top-row 1 on its own (Shift+1 is the run gait, and the pad's 1 is not this key).
        if (e.code === 'Digit1' && !e.shiftKey && !e.ctrlKey && !e.altKey && !e.metaKey) {
          e.preventDefault();
          if (!e.repeat) {
            this.toggleMount(e.timeStamp);
          }

          return;
        }

        const boost = boostFor(e.code, e.shiftKey);
        if (boost !== null) {
          // The browser's own Shift+number does nothing on a canvas, but a page that has focus in the sidebar would
          // otherwise type into it, and a repeat of a held digit would re-latch sixty times a second for no effect.
          e.preventDefault();
          this.boost = boost;
        }

        this.flushGaitNow(e.timeStamp);
      },
      { signal: this.gaitKeys.signal },
    );

    // The other half of A14b's send: letting go of the last direction key is a gait change too, and the stop it owes has to
    // leave on the same terms as the start.
    window.addEventListener(
      'keyup',
      (e) => {
        this.flushGaitNow(e.timeStamp);
      },
      { signal: this.gaitKeys.signal },
    );

    // Observer callbacks run after the frame's draw and before its paint: resizing the canvas there would paint it blank.
    this.resizeObserver = new ResizeObserver(() => {
      this.sizeDirty = true;
    });

    this.unsubscribe = useUi.subscribe((state, previous) => {
      this.onUiChange(state, previous);
    });
  }

  /**
   * The app, on `window.__typhon`, for a console or an automated browser to look at what the renderer is actually
   * holding. A demo whose purpose is to be inspected; the first live session needed exactly this and had to grow it.
   */
  private expose(): void {
    (window as unknown as { __typhon?: ClientApp }).__typhon = this;
  }

  start(): void {
    this.expose();
    this.resizeObserver.observe(this.canvas);
    this.watchDeviceRatio();
    this.applyUi(useUi.getState());
    this.startSource(useUi.getState());
    this.engine.runRenderLoop(() => {
      // Babylon schedules the next frame only after this one returns: an exception must not escape, or rendering stops.
      try {
        if (this.sizeDirty) {
          this.resize();
        }

        this.frame();
        this.scene.render();
      } catch (error) {
        this.reportFrameError(error);
      }
    });
  }

  /**
   * Places the server's published assets, once, when it announced a set.
   *
   * <b>Opt-in, and silent when there is nothing to mount.</b> The asset set lives outside this repository and a server
   * started without `--assets` publishes none, so every path here has to be one a run with no assets never takes: this
   * client draws exactly what it drew before, and nothing in the frame loop asks whether assets exist.
   *
   * Failures are reported and swallowed. A missing decoder, a 404 on a descriptor or a catalogue schema this build does
   * not read are all deployment faults, and none of them is a reason for the session — the simulation, the crowd, the
   * terrain — to stop being watchable.
   *
   * @param set The asset set from `/typhon/demo.json`.
   * @returns When the arrangement is placed, or when it has failed and said so.
   */
  async mountAssets(set: AssetSet): Promise<void> {
    if (this.assets !== null || this.gone()) {
      return;
    }

    try {
      // Anchored on the city the camera starts over, so what is placed is under the viewer rather than somewhere they
      // have to be told to fly to.
      const mount = await mountAssets(this.scene, set, this.origin, this.lighting);
      if (this.gone()) {
        // The session was torn down while the catalogue was in flight; its scene took the instances with it.
        mount.dispose();
        return;
      }

      this.assets = mount;
      this.assetSet = set;
      // After the mount, because it reads the same library — and deliberately not inside it: a crowd that will not
      // build must not cost the street its buildings, and a street that will not load must not cost the crowd.
      try {
        // Read once, for every crowd: the characters and each creature species plan their atlases from these.
        const described = await describeCatalogue(mount.library);
        this.crowd = await mountCrowd(
          this.scene,
          mount.library,
          this.lighting,
          CROWD_CAPACITY,
          described,
          enemySkins(set.creatures),
        );
        await this.mountCreatureCrowds(mount.library, set, described);
        // Everyone else's riders on their swoops, the seat the avatar's rider uses once the swoop's descriptor says where it is.
        this.crowdSwoops = new CrowdSwoops(this.scene, mount.library, mount.lighting);
        void mount.library
          .describe(SWOOP_ASSET)
          .then((swoop) => {
            const seat = swoop.seats?.[0];
            if (seat !== undefined && this.crowd !== null) {
              this.crowd.layer.seatY = seat.pos[1];
            }
          })
          .catch(() => undefined);
      } catch (error) {
        console.error('crowd: not mounted', error);
      }

      this.shapeCache = [];
      this.staticAssets = new StaticAssets(
        mount.library,
        mount.root,
        (asset) => assetForStructure(set, asset),
        undefined,
        mount.lighting,
      );
      // A lair's prop is its template's, by the server's table: the nest, the hideout or the camp.
      this.lairAssets = new StaticAssets(
        mount.library,
        mount.root,
        (template) => set.creatures[template]?.lair || null,
        undefined,
        mount.lighting,
      );
      this.shuttleView = new ShuttleView(mount.library, mount.root, mount.lighting);
      // The descriptors the ticket terminal is read from, asked for now so the first port walked up to has them.
      void mount.library.describe(SHUTTLEPORT_ASSET).catch(() => undefined);
      void mount.library.describe(TICKET_TERMINAL_ASSET).catch(() => undefined);
    } catch (error) {
      console.error('assets: not mounted', error);
    }
  }

  /**
   * Whether this app is over: torn down by {@link dispose}, or holding a scene something else disposed.
   *
   * <b>A method rather than a field, deliberately.</b> TypeScript carries a property's narrowing across an `await`, so a
   * second read of `this.scene.isDisposed` after an early return on the same expression is "always false" to the compiler
   * and to the linter — the type system proving away the exact race the second check exists for. A call is not narrowed.
   */
  private gone(): boolean {
    return this.disposed || this.scene.isDisposed;
  }

  /** What the renderer is drawing from, for {@link expose}: the store, the grid and the resolved archetypes. */
  get debug(): {
    world: WorldStore;
    grid: AggregateGrid;
    archetypes: ArchetypeView;
    layers: readonly EntityLayer[];
    view: FrameView;
    /** The UI store, so a console or a browser test can fly the camera and pick an entity as a person would. */
    ui: typeof useUi;
    /** The first few live entities of one archetype, evaluated at this frame's render time. */
    positions: (
      archetype: number,
      count?: number,
    ) => { netId: number; x: number; y: number; z: number; vx: number; vz: number }[];
    /** What a click at this canvas pixel would select, without going through the pointer handling. */
    pick: (x: number, y: number) => number;
    /** Whether the source can stop the server's simulation. */
    canPause: () => boolean;
    /** Where an entity the renderer PACKED this frame is on screen, in canvas CSS pixels, or null when it packed none. */
    screenOf: (netId: number) => { x: number; y: number; depth: number; band: 'near' | 'far' } | null;
  } {
    return {
      world: this.world,
      grid: this.grid,
      archetypes: this.archetypes,
      layers: this.layers,
      view: this.view,
      ui: useUi,
      positions: (archetype, count = 5) => {
        const store = this.world.archetypeStore(archetype);
        const out: { netId: number; x: number; y: number; z: number; vx: number; vz: number }[] = [];
        for (let i = 0; i < Math.min(count, store.liveCount); i++) {
          const slot = store.live[i];
          evaluateSlot(store, slot, this.clock.renderTick, this.clock.renderFrac, this.scratch, 0);
          const at = this.placement.read(store.dims, this.scratch, 0);
          out.push({ netId: store.netIds[slot], x: at.x, y: at.y, z: at.z, vx: at.vx, vz: at.vz });
        }

        return out;
      },
      pick: (x, y) => this.pickAt(x, y),
      canPause: () => this.source?.canPause ?? false,
      screenOf: (netId) => {
        const matrix = this.scene.getTransformMatrix().m;
        for (const layer of this.layers) {
          const found = layer.screenOf(matrix, this.view, netId);
          if (found !== null) {
            return found;
          }
        }

        return null;
      },
    };
  }

  dispose(): void {
    // Cleared first: a disposed app left on `window` keeps the whole Babylon scene reachable, and a console inspecting
    // it would be reading a dead one.
    const host = window as unknown as { __typhon?: ClientApp };
    if (host.__typhon === this) {
      delete host.__typhon;
    }

    this.disposed = true;
    this.shuttleView?.dispose();
    this.engine.stopRenderLoop();
    this.resizeObserver.disconnect();
    this.ratioQuery?.removeEventListener('change', this.onRatioChange);
    this.ratioQuery = null;
    this.unsubscribe();
    this.gaitKeys.abort();
    this.input.dispose();
    this.source?.dispose();
    this.source = null;
    this.labels.dispose();
    this.attacks.dispose();
    for (const layer of this.layers) {
      layer.dispose();
    }

    for (const profile of this.profiles.values()) {
      profile.dispose();
    }

    this.staticAssets?.dispose();
    this.staticAssets = null;
    this.lairAssets?.dispose();
    this.lairAssets = null;
    this.crowdSwoops?.dispose();
    this.crowdSwoops = null;
    this.crowd?.dispose();
    this.crowd = null;
    for (const species of this.creatureCrowds) {
      species.dispose();
    }

    this.creatureCrowds = [];
    this.crowdRouter = null;
    this.gizmos?.dispose();
    this.gizmos = null;
    this.assets?.dispose();
    this.assets = null;
    // After the mount, which holds instances of meshes this lit.
    this.lighting.dispose();
    this.scene.dispose();
    this.engine.dispose();
  }

  /**
   * Adopts the baked heightfield.
   *
   * Copied into the field's own array rather than swapped in, because the GPU texture is created over that array and a
   * swap would leave it pointing at the flat one. 16.8 MB is a few milliseconds, once.
   *
   * Called from the constructor, before the profile exists — the quadtree arrays are handed to `PlanetProfile` separately,
   * right after it is built, because they are its data and not the field's.
   */
  private adoptTerrain(baked: TerrainBaked): void {
    const grid = this.field.grid;
    if (baked.posts !== grid.posts || baked.spacingM !== grid.spacingM || baked.originM !== grid.originM) {
      // Both sides read the same constants, so this cannot happen without a bug; it is here because silently rendering a
      // field at the wrong scale would look like bad terrain rather than like a defect.
      console.warn('terrain bake geometry does not match the field; keeping it flat');
      return;
    }

    grid.height.set(baked.height);
    this.field.measure();
    this.terrainBakeMs = baked.bakeMs;
  }

  private resize(): void {
    this.sizeDirty = false;
    this.cssSize.width = Math.max(1, this.canvas.clientWidth);
    this.cssSize.height = Math.max(1, this.canvas.clientHeight);
    this.renderScale = Math.min(MAX_DEVICE_RATIO, window.devicePixelRatio || 1);
    // Resizes the canvas too.
    this.engine.setHardwareScalingLevel(1 / this.renderScale);
  }

  /** A media query that matches only the current device pixel ratio: it fires on browser zoom and on a monitor change. */
  private watchDeviceRatio(): void {
    this.ratioQuery?.removeEventListener('change', this.onRatioChange);
    this.ratioQuery = window.matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`);
    this.ratioQuery.addEventListener('change', this.onRatioChange);
  }

  private startSource(state: UiState): void {
    this.source?.dispose();

    // What the last session was told is not this one's to show: a reconnect or a source change starts the room empty.
    useChat.getState().clear();
    this.clock = new Clock(CLOCK_OPTIONS);

    // The world the context offers is the one a source that does not own its own fills — the mock. A live connection can
    // only size its store from the server's catalog, which arrives with the WELCOME, so it hands one back later instead
    // and `adoptSourceWorld` picks it up.
    this.adopt(new WorldStore(SWG_SCHEMA), new AggregateGrid(AGG_GRID));
    this.source = this.createSource(
      { world: this.world, grid: this.grid, clock: this.clock, events: this.attacks },
      state,
    );
    this.source.start();
    this.source.setPaused(state.paused);
    this.regionRadius = Number.NaN;
    this.lastRegionMs = Number.NEGATIVE_INFINITY;
  }

  /**
   * Takes the world a source built for itself, when it has one this app is not already drawing.
   *
   * Called every frame because there is no other moment that is right: a live session's store appears on its `WELCOME`,
   * and a reconnect to a server whose catalog moved replaces it. Steady state is one reference comparison.
   */
  private adoptSourceWorld(): void {
    const world = this.source?.world;
    if (world === null || world === undefined || world === this.world) {
      return;
    }

    this.adopt(world, this.source?.grid ?? new AggregateGrid(NO_GRID));
  }

  /**
   * Notices that the session has crossed into another realm, the same way {@link adoptSourceWorld} notices a new store.
   *
   * <b>A per-frame compare rather than a callback</b>, for the reason `adoptSourceWorld` gives: the SDK's
   * `onRealmChanged` fires inside `applier.apply()`, and a scene swap belongs to the frame that draws it, not to the
   * decode that caused it. Steady state is one reference compare against a string.
   */
  /**
   * Notices that the store was emptied under the app without the realm moving.
   *
   * <b>The door {@link adoptSourceRealm} does not cover.</b> A `RESET` invalidates every netId, and only some resets
   * carry a realm change: a profile switch — which is what spectating an entity is — empties the store in the realm
   * the session is already in, so `onRealmChanged` never fires and nothing would drop the selection. netIds are dense
   * and reused, so the held one then resolves to a stranger.
   *
   * Runs BEFORE {@link adoptSourceRealm}, so a crossing is cleared once here and then again by the realm path, which
   * adds the camera. The clearing is idempotent by construction — a selection already 0, a chat log already empty, a
   * region already NaN — so the double call costs nothing, and the alternative, a flag saying which of the two has
   * already run, is a second source of truth for "has this frame been reset".
   */
  private adoptSourceReset(): void {
    const count = this.source?.resetCount ?? 0;
    if (count === this.resetCount) {
      return;
    }

    this.resetCount = count;
    resetForSession(this.resetTargets());
    // A new session asked for nothing: neither a mount nor an arrival's turn carries over from the one before.
    this.mountAskedAtMs = Number.NEGATIVE_INFINITY;
    this.faceOutOfGate = false;
  }

  /**
   * Keeps the selection on the entity the SESSION is riding, across the resets that riding causes.
   *
   * <b>A ride cannot survive by netId.</b> Anchoring the session re-sends the whole view and netIds are allocated
   * densely per view, so the subject almost never keeps the id that was clicked — and {@link adoptSourceReset} has just
   * dropped the selection for exactly that reason. The server names the anchored entity in `SELF`, which is what
   * `Control` is for, so this re-selects by being told rather than by guessing.
   *
   * <b>And it is how a lost subject ends the ride.</b> A subject that dies leaves the session anchored on nothing —
   * `BoundLost`, 12-realms § 1.3: the engine keeps the session's realm and last point until the app re-anchors it — and
   * `SELF` goes to 0. Releasing here rather than waiting for the viewer is what stops the camera riding a corpse, and
   * the release is a real command, so the session gets its own camera back in the realm it was left in.
   */
  private adoptSubject(now: number): void {
    const source = this.source;
    if (source === null) {
      return;
    }

    const ui = useUi.getState();
    const step = nextRide(this.ride, source.spectatingNetId, source.selfNetId, ui.selectedNetId, source.resetCount);
    this.ride = step.state;
    if (step.action.kind === 'adopt') {
      // A DELIBERATE change of subject, so the aim goes with it: the viewer asked to ride something else, and keeping
      // the angle they were watching the previous body from would point the camera at nothing in particular.
      this.eyeSubject = 0;
      this.eyeRebased = false;
      // The camera as well as the selection, because the reset dropped both: `select(0)` returns the camera to god
      // mode, so re-selecting alone would leave the viewer looking at their subject from above after every door.
      // Riding IS riding — the viewer can still leave eye mode by hand, and the next crossing puts them back in it.
      ui.rideSubject(step.action.netId);
      return;
    }

    if (step.action.kind !== 'release') {
      return;
    }

    // A subject that died or left leaves the session anchored on nothing (`BoundLost`, 12-realms § 1.3: the engine
    // keeps the session's realm and last point until the app re-anchors it). RETRIED rather than asked once, because
    // the ask is rate-limited and a refused one would otherwise strand the ride with no control on screen to try
    // again from — the selection is already 0, so the inspector is not even open. At the command's rate, not the
    // frame's.
    if (now - this.lastReleaseMs < REGION_INTERVAL_MS) {
      return;
    }

    this.lastReleaseMs = now;
    ui.spectate(0);
  }

  private adoptSourceRealm(): void {
    const next = this.source?.realm ?? null;
    if (sameRealm(next, this.realm)) {
      return;
    }

    const previous = this.realm;
    this.realm = next;
    this.onRealmSwitched(previous, next);
  }

  /**
   * Drops everything that belonged to the realm just left.
   *
   * The policy is in {@link resetForRealm}, which can be asserted without a canvas; this supplies the three things
   * that live in here and nowhere a test can reach.
   */
  private onRealmSwitched(previous: RealmView | null, next: RealmView | null): void {
    // The scene first, so that everything the reset then reads — the ground sampler the camera rides, the bounds it
    // clamps to — is the new realm's rather than the old one's.
    if (next !== null) {
      this.showScene(next);
    }

    // Whether THIS crossing is the one the viewer asked for, from the outstanding request's realm id — not from
    // "a ramp is running", which was wrong in three ways: a door landing inside a previous crossing's fade-in was
    // captioned *Travelling…*, a late arrival after the safety valve had already returned to idle was classed as a
    // door, and an ask for one realm interrupted by a door credited the door with the ask.
    const ui = useUi.getState();
    const asked = next !== null && ui.realmRequest?.realmId === next.realmId;
    const kind = crossingKindOf(previous, next, asked);
    const now = performance.now();

    // <b>Through a door, the scene stays black until the camera is behind the body in the new realm.</b> The eye camera
    // used to treat the switch as a renumbering and GLIDE from the old realm's coordinates to the new ones — ten frames
    // of a room seen from above — and kept the yaw it had outside, so the room came up turned: the door to one side
    // instead of behind. `placeArrival` snaps it, facing the way the spawn the body landed on faces, and only then
    // fades in.
    const door =
      kind === 'transition' &&
      ((previous !== null && isIndoors(previous.scene)) || (next !== null && isIndoors(next.scene)));
    if (door && this.source?.canPlay === true) {
      ui.setTransition(blackout(ui.transition, now));
      // Only a building's door publishes the spawns a facing is read from; a dungeon's has none to wait for.
      this.arrival = beginArrival(
        kind,
        now,
        previous?.scene === 'interior' || next?.scene === 'interior',
        this.doorCarry(now),
      );
    } else {
      ui.setTransition(arrived(ui.transition, kind, now));
      this.arrival = null;
    }

    this.hasLastStep = false;
    this.doorPlaneCache.clear();
    resetForRealm(next, this.resetTargets());
  }

  /**
   * A door crossing whose arrival is not yet placed: the camera has to be snapped behind the body, facing the way the
   * spawn it landed on faces, before the fade comes back. `null` when there is none. See `door-arrival.ts`.
   */
  private arrival: DoorArrival | null = null;

  /** The spawn facing at a point, bound once for {@link stepArrival}. */
  private readonly facingAt = (x: number, z: number): readonly [number, number] | null => this.spawnFacing(x, z);

  /**
   * Places the camera on an arrival through a door, and gives the screen back.
   *
   * @returns Whether it was placed this frame.
   *
   * @remarks
   * <b>The facing is the spawn's, read from the descriptor of what the body is standing in</b> — a room's `entry` faces
   * into the room with its door behind, a house's `front` faces away from the house — because the server has no heading
   * to send. The structures and their descriptors can arrive a frame or more after the realm does, so the screen stays
   * black until the spawn is found, for at most `ARRIVAL_WAIT_MS` (`door-arrival.ts`); past that the camera is placed with the yaw it
   * has, which is what every crossing did before.
   */
  private placeArrival(camX: number, camY: number, camZ: number, nowMs: number): boolean {
    const arrival = this.arrival;
    if (arrival === null) {
      return false;
    }

    const ui = useUi.getState();
    const step = stepArrival(arrival, ui.transition, camX, camZ, nowMs, this.facingAt);
    if (step.action === 'abandon') {
      this.arrival = null;
      return false;
    }

    if (step.action === 'wait') {
      return false;
    }

    const facing = step.facing;
    let yaw = this.eyeCamera.yaw;
    if (facing !== null) {
      // The renderer's yaw (0 along +Z), and the server's heading (from +X towards +Z) for the avatar. Carried through the
      // door when it is known which one was crossed, so the camera keeps its angle to the body; put behind the body otherwise.
      const spawnYaw = Math.atan2(facing[0], facing[1]);
      const carried = arrival.carry === null ? null : carryThrough(arrival.carry, spawnYaw);
      yaw = carried === null ? spawnYaw : carried.yaw;
      this.intentHeading = carried === null ? Math.atan2(facing[1], facing[0]) : carried.heading;
      this.haveIntentHeading = true;
      this.avatarHeading = this.intentHeading;
    }

    this.eyeCamera.jumpTo(camX, camY, camZ, yaw);
    ui.setTransition(arrived(ui.transition, arrival.kind, nowMs));
    this.arrival = null;
    return true;
  }

  /**
   * The facing of the published spawn nearest a body that has just come through a door, among the structures in reach, or
   * `null`. The NEAREST across structures, not the first: the search radius is wide enough to reach a running body
   * (`ARRIVAL_RADIUS_M`), so a neighbour's spawn may be in it too.
   */
  private spawnFacing(x: number, z: number): readonly [number, number] | null {
    const set = this.assetSet;
    const library = this.assets?.library ?? null;
    if (set === null || library === null) {
      return null;
    }

    let best: SpawnNear | null = null;
    for (let i = 0; i < this.structureCount; i++) {
      const s = this.structureSamples[i];
      const id = assetForStructure(set, s.asset);
      const descriptor = id === null ? null : library.described(id);
      if (descriptor === null) {
        // Asked for now rather than waited for with the geometry: a room entered for the first time has not been
        // placed yet, and its descriptor is a few KB the arrival needs before the screen comes back.
        if (id !== null) {
          void library.describe(id).catch(() => undefined);
        }

        continue;
      }

      if (descriptor.spawns.length === 0) {
        continue;
      }

      const near = spawnNear(descriptor, placement([s.x, s.y, s.z], structureYaw(s.yaw)), x, z);
      if (near !== null && (best === null || near.distance < best.distance)) {
        best = near;
      }
    }

    return best === null ? null : best.facing;
  }

  /** How many of {@link placedShapes} the camera's march reads this frame. */
  private cameraShapes = 0;

  /** The eye camera's wall probe, bound once: a march over the structures gathered this frame. */
  private readonly eyeClearance = (
    fx: number,
    fy: number,
    fz: number,
    dx: number,
    dy: number,
    dz: number,
    max: number,
  ): number => marchEye(this.placedShapes, this.cameraShapes, fx, fy, fz, dx, dy, dz, max);

  /** The curtain's element, found once it is mounted; see {@link paintFade}. */
  private fadeElement: HTMLElement | null = null;

  /** The opacity last written, so a frame that changes nothing writes nothing. */
  private fadeOpacity = -1;

  /**
   * Sets the realm curtain's opacity for THIS frame, from the transition state.
   *
   * In the frame loop rather than in React, so the curtain and the scene it hides are painted together: a door arrival
   * goes black on the frame the realm switches, not a frame or two later over a camera still in the old realm.
   */
  private paintFade(nowMs: number): void {
    if (this.fadeElement === null || !this.fadeElement.isConnected) {
      this.fadeElement = document.querySelector<HTMLElement>('.realm-fade');
      this.fadeOpacity = -1;
    }

    const opacity = opacityAt(useUi.getState().transition, nowMs);
    if (this.fadeElement !== null && opacity !== this.fadeOpacity) {
      this.fadeElement.style.opacity = String(opacity);
      this.fadeOpacity = opacity;
    }
  }

  /** The direction of the door the predicted body last stepped through, as a renderer yaw, and when; see {@link doorCarry}. */
  private crossedDoorYaw = 0;
  private crossedAtMs = Number.NEGATIVE_INFINITY;

  /**
   * What to carry through the door that just switched the realm: the door's crossing direction, the camera's yaw and the
   * body's heading — or `null` when which door was crossed is not known.
   *
   * The door is the one the prediction saw the body step through, when that was moments ago; otherwise the nearest of the
   * doors the watch had in hand to where the body last stood (a crossing the prediction missed: a body the server moved).
   */
  private doorCarry(nowMs: number): DoorCarry | null {
    const heading = this.haveIntentHeading ? this.intentHeading : this.avatarHeading;
    if (nowMs - this.crossedAtMs < 3000) {
      return { doorYaw: this.crossedDoorYaw, cameraYaw: this.eyeCamera.yaw, heading };
    }

    if (!this.hasLastStep) {
      return null;
    }

    let best: DoorPlane | null = null;
    let bestD = 3;
    for (const entry of this.doorPlaneCache.values()) {
      for (const plane of entry.planes) {
        const d = Math.hypot(plane.x - this.lastStepX, plane.z - this.lastStepZ);
        if (d < bestD) {
          best = plane;
          bestD = d;
        }
      }
    }

    return best === null ? null : { doorYaw: Math.atan2(best.fx, best.fz), cameraYaw: this.eyeCamera.yaw, heading };
  }

  /** The nearest shuttleport, bazaar and mission terminal, rescanned every {@link NAV_INTERVAL_MS}; see `nav-finder.ts`. */
  private readonly navTargets: NavTarget[] = [];
  private navAtMs = Number.NEGATIVE_INFINITY;
  private navShown = false;
  private navKey = '';
  /** Where the body stood at the last scan, which the arrows point from. */
  private navFromX = 0;
  private navFromZ = 0;
  /** The pane's arrows by kind, found once they are mounted, and the turn last written to each. */
  private readonly navArrows = new Map<number, HTMLElement>();
  private readonly navTurns = new Map<number, number>();

  /**
   * Rescans for the nearest of each facility kind and publishes the pane's rows when they changed. Only for a playing
   * avatar on a planet: a room or a spectator has nothing to navigate to, and the pane goes.
   */
  private updateNav(nowMs: number): void {
    if (nowMs - this.navAtMs < NAV_INTERVAL_MS) {
      return;
    }

    this.navAtMs = nowMs;
    const source = this.source;
    const ui = useUi.getState();
    if (source === null || !source.canPlay || source.selfNetId === 0 || this.profile.kind !== 'planet') {
      if (this.navShown) {
        this.navShown = false;
        this.navKey = '';
        ui.setNav([], 0, 0);
      }

      return;
    }

    this.navFromX = this.predictor.active ? this.predictor.x : this.eyeX;
    this.navFromZ = this.predictor.active ? this.predictor.z : this.eyeZ;
    const started = performance.now();
    nearestFacilities(this.structureSamples, this.structureCount, this.navFromX, this.navFromZ, this.navTargets);
    const scanMs = performance.now() - started;

    // Republished only when what it shows changed: a metre of distance or a different target. Five store writes a second
    // would re-render the pane five times a second for nothing.
    let key = '';
    for (const t of this.navTargets) {
      key += `${t.kind}:${Number.isFinite(t.distance) ? Math.round(t.distance) : -1}:${Math.round(t.x)},${Math.round(t.z)};`;
    }

    if (key !== this.navKey) {
      this.navKey = key;
      this.navShown = true;
      const rows: NavRow[] = this.navTargets.map((t) => {
        // A click walks to where the thing is used from: a shuttleport's ticket terminal, not its pad.
        const at = t.kind === StructureKind.Shuttleport ? this.ticketStandPoint(t.x, t.z) : null;
        return {
          kind: t.kind,
          label: navLabel(t),
          distanceM: Number.isFinite(t.distance) ? Math.round(t.distance) : Infinity,
          x: at?.x ?? t.x,
          z: at?.z ?? t.z,
        };
      });
      ui.setNav(rows, scanMs, this.structureCount);
    }
  }

  /** Turns the pane's arrows to the camera, every frame: up is straight ahead (see `relativeBearing`). */
  private paintNav(yaw: number): void {
    if (!this.navShown) {
      return;
    }

    for (let k = 0; k < NAV_KINDS.length && k < this.navTargets.length; k++) {
      const target = this.navTargets[k];
      let arrow = this.navArrows.get(target.kind) ?? null;
      if (arrow === null || !arrow.isConnected) {
        arrow = document.querySelector<HTMLElement>(`.nav-arrow[data-nav-kind="${target.kind}"]`);
        if (arrow === null) {
          continue;
        }

        this.navArrows.set(target.kind, arrow);
        this.navTurns.delete(target.kind);
      }

      const turn = Number.isFinite(target.distance)
        ? relativeBearing(this.navFromX, this.navFromZ, target.x, target.z, yaw)
        : 0;
      const last = this.navTurns.get(target.kind);
      if (last === undefined || Math.abs(last - turn) > 0.005) {
        arrow.style.transform = `rotate(${turn.toFixed(3)}rad)`;
        this.navTurns.set(target.kind, turn);
      }
    }
  }

  /** The predicted body's position on the previous frame, in its realm, for {@link watchDoors}; not set after a switch. */
  private lastStepX = 0;
  private lastStepZ = 0;
  private hasLastStep = false;

  /**
   * Each nearby structure's door planes, by netId, with the pose they were built for: a structure does not move, and
   * building them per frame was a placement, a plane array and its vectors per structure per moving frame.
   */
  private readonly doorPlaneCache = new Map<
    number,
    { asset: number; x: number; y: number; z: number; yaw: number; planes: readonly DoorPlane[] }
  >();

  /**
   * Starts the fade the moment the predicted body steps through a door, before the server's answer arrives.
   *
   * @remarks
   * The same directed plane test the server applies, against the doors of the structures in reach (`door-crossing.ts`).
   * A prediction can be wrong — a body the server stopped in the jamb — and then the hold's safety valve gives the screen
   * back; the cost of a wrong guess is a short blink, the cost of no guess is the cut the testbench does not have.
   */
  private watchDoors(nowMs: number): void {
    if (!this.predictor.active) {
      this.hasLastStep = false;
      return;
    }

    const x = this.predictor.x;
    const z = this.predictor.z;
    const had = this.hasLastStep;
    const fromX = this.lastStepX;
    const fromZ = this.lastStepZ;
    this.lastStepX = x;
    this.lastStepZ = z;
    this.hasLastStep = true;
    const ui = useUi.getState();
    if (!had || ui.transition.phase !== 'idle' || (fromX === x && fromZ === z)) {
      return;
    }

    const set = this.assetSet;
    const library = this.assets?.library ?? null;
    if (set === null || library === null) {
      return;
    }

    for (let i = 0; i < this.structureCount; i++) {
      const s = this.structureSamples[i];
      if (Math.abs(s.x - x) > 25 || Math.abs(s.z - z) > 25) {
        continue;
      }

      const id = assetForStructure(set, s.asset);
      const descriptor = id === null ? null : library.described(id);
      if (descriptor === null || descriptor.portals.length === 0) {
        continue;
      }

      let cached = this.doorPlaneCache.get(s.netId);
      if (
        cached?.asset !== s.asset ||
        cached.x !== s.x ||
        cached.y !== s.y ||
        cached.z !== s.z ||
        cached.yaw !== s.yaw
      ) {
        const planes = doorPlanes(descriptor, placement([s.x, s.y, s.z], structureYaw(s.yaw)));
        cached = { asset: s.asset, x: s.x, y: s.y, z: s.z, yaw: s.yaw, planes };
        this.doorPlaneCache.set(s.netId, cached);
      }

      const planes = cached.planes;
      for (let d = 0; d < planes.length; d++) {
        if (crossedDoor(planes[d], fromX, fromZ, x, z)) {
          this.crossedDoorYaw = Math.atan2(planes[d].fx, planes[d].fz);
          this.crossedAtMs = nowMs;
          ui.setTransition(askedFor(ui.transition, 'transition', nowMs));
          return;
        }
      }
    }
  }

  /**
   * The three things a reset has to reach that live in here and nowhere a test can get at.
   *
   * One object for both callers, so the reset path cannot drift between "the store was emptied" and "the store was
   * emptied and we moved".
   */
  private resetTargets(): RealmSwitchTargets {
    return {
      setCameraBounds: (bounds) => {
        this.mapCamera.setBounds(bounds);
      },
      jumpCamera: (x, z, distanceM) => {
        this.mapCamera.jumpTo(x, z, distanceM);
      },
      forgetSelectionCaches: () => {
        // NOT `eyeSubject = 0`, which is what this used to do and what made every reset re-aim the camera. A reset
        // reallocates netIds; it does not move the viewer's head. See `eyeRebased`.
        this.eyeRebased = true;
        this.selectionTextNetId = 0;
      },
      forgetEyeAim: () => {
        this.eyeSubject = 0;
      },
      forgetRegion: () => {
        this.regionX = Number.NaN;
        this.regionZ = Number.NaN;
        this.regionRadius = Number.NaN;
        this.lastRegionMs = Number.NEGATIVE_INFINITY;
      },
      forgetViewRadius: () => {
        this.sentViewRadius = null;
      },
    };
  }

  /**
   * Puts the backdrop for a realm on screen, building it the first time one of its kind is entered.
   *
   * <b>Built lazily and then kept.</b> A session that never leaves the planet never pays for a room, and one that walks
   * in and out of twenty buildings builds the room once. Nothing is disposed on a switch: see {@link SceneProfile}.
   *
   * A realm kind this build has no scene for keeps the one on screen rather than showing nothing. It cannot happen
   * against this server — every kind it declares has a profile — but a newer server naming a fifth kind should cost the
   * viewer a backdrop that is merely wrong, not a black screen over a live world.
   */
  private showScene(next: RealmView): void {
    const profile = this.profileFor(next.scene);
    if (profile === null) {
      return;
    }

    if (profile !== this.profile) {
      this.profile.leave();
      this.profile = profile;
    }

    profile.enter(next);
    this.scene.clearColor = profile.sky.toColor4(1);
    this.lighting.setIndoors(isIndoors(profile.kind));

    // The cameras read the ground through the profile, so both have to be re-pointed: the map camera rides it and the
    // eye camera marches it for terrain clearance. A stale sampler is the quiet version of this bug — the picture
    // changes and the camera keeps standing on the old world's hills.
    this.view.ground = profile.ground;
    this.eyeCamera.ground = profile.ground;
  }

  /**
   * Sends the movement intent the held keys mean, when it has changed or is due a refresh (A9).
   *
   * @param source The live session.
   * @param nowMs The frame's clock.
   * @param steering Whether the keys are the avatar's this frame — true in eye mode, false in map mode where they pan the camera.
   *
   * @remarks
   * <b>Two reasons to send, and both are needed.</b> A CHANGE — a new heading, a new speed class, the stop that ends a run —
   * must go at once or the avatar does the wrong thing for as long as the key is held. A REFRESH must go because the server
   * turns a heading into a destination one second of travel away: an intent that stopped being re-sent would simply be
   * reached, and the avatar would halt while the key was still down.
   *
   * <b>The heading is the camera's yaw AFTER this frame's update.</b> Taken before it, every intent would be a frame behind
   * the view the person is steering by — which at sixty frames a second is invisible standing still and clearly wrong while
   * turning.
   *
   * <b>`steering: false` is not the same as not calling this.</b> It reads as "the keys are not mine this frame", which still owes
   * the avatar a stop if it was moving — so switching to the map camera mid-run halts the character instead of leaving it walking
   * off under a camera that is no longer behind it.
   */
  /**
   * Starts, steps or stops the avatar's prediction (A14).
   *
   * @param netId The avatar to predict, or 0 for "nothing to predict" — a god camera, a spectator, or a session whose intents
   * the server refuses.
   * @param nowMs This frame's timestamp.
   * @param dt Seconds since the previous frame.
   *
   * @remarks
   * <b>Reconciles against {@link eyeX}/{@link eyeZ}, which {@link locateEyeSubject} has just written for this very netId.</b>
   * Calling {@link evaluate} again would interpolate the same entity a second time per frame, and would overwrite the shared
   * `eval*` slots that {@link locateSelected} and {@link locateEyeSubject} both read — harmless today only because both copy
   * their values out first, which is the kind of ordering that holds until someone moves a line.
   *
   * The caller passes 0 for anything that is not a player steering its own body, and only passes a netId once
   * {@link locateEyeSubject} has succeeded for it — so there is always an authority here to reconcile against.
   */
  private advancePrediction(netId: number, nowMs: number, dt: number): void {
    if (netId === 0) {
      this.predictor.stop();
      this.predictedSubject = 0;
      this.predictedFeet = Number.NaN;
      return;
    }

    if (this.predictedSubject !== netId) {
      this.predictedSubject = netId;
      this.predictor.start(nowMs, this.eyeX, this.eyeZ);
      // A different body, so the altitude carried from the last one says nothing about this one: a session that was
      // spectating someone on a roof must not read that roof as the step its own feet are leaving.
      this.predictedFeet = Number.NaN;
      return;
    }

    this.predictor.step(nowMs, dt, this.eyeX, this.eyeZ, this.clock.renderDelayMs);

    // <b>And then out of the walls, the way the server will.</b> Prediction integrates the intent; without this it
    // walks through a building and reconciliation pulls back against a server that keeps pushing out, which settles
    // into a standing error rather than a transient — `predictor.ts` sizes it at 0.8 m here and 2.1 m at the delay
    // ceiling. That is the difference between standing in a 1.2 m doorway and standing in its jamb.
    const half = this.bodyHalfM;
    const at = this.predictedAt;
    at.x = this.predictor.x;
    at.z = this.predictor.z;
    // <b>A body that has just come through a door stands where the SERVER put it</b> — on the landing at the top of a
    // house's ramp, 0.7 m above the terrain — and the structures that say so arrive a frame or more after the realm does.
    // Taking the terrain in the meantime made the landing a wall the moment it arrived (more than a step above the feet),
    // and the prediction pushed the body a metre off it. So until the structures are in, the feet are the server's.
    //
    // <b>Until it MOVES, not for one frame</b>: the shapes in reach on the first frames after a switch can be the old
    // realm's or a partial set, and one frame's server altitude sampled against them came back as the terrain, which then
    // stood as the base for good. While the body stands where it landed, the server's altitude is the truth.
    const realmId = this.realm?.realmId ?? -1;
    if (this.predictedRealm !== realmId) {
      this.predictedRealm = realmId;
      this.feetFromServer = true;
      this.arrivedX = at.x;
      this.arrivedZ = at.z;
    }

    const serverFeet = this.feetFromServer && Number.isFinite(this.evalY) ? this.evalY : Number.NaN;
    const count = half > 0 ? this.gatherStructureShapes() : 0;

    // Handed back once the body has moved off its landing AND the shapes around it are in: a body still walking when it
    // came through has moved before the porch it stands on has arrived, and the terrain taken in that gap made the porch a
    // wall the moment it did — the body went through it, onto the ground.
    if (this.feetFromServer && count > 0 && Math.hypot(at.x - this.arrivedX, at.z - this.arrivedZ) > 0.05) {
      this.feetFromServer = false;
    }
    if (count === 0) {
      // Nothing published within reach, so the planet's own relief is the whole answer — and saying so is what stops a
      // body that has just walked off a deck from keeping that deck's altitude for the rest of the session.
      const terrain = this.profile.ground.heightAt(at.x, at.z);
      this.predictedFeet = Number.isFinite(serverFeet) ? Math.max(serverFeet, terrain) : terrain;
      return;
    }

    // <b>One base for both samples of the frame.</b> The floor test is relative to where the feet WERE, so sampling the
    // ground twice against a moving base would let one frame climb two steps: the body would mount a 0.8 m ledge at
    // 60 Hz that it refuses at 30. The previous frame's altitude is the base, and both samples share it.
    const base = Number.isFinite(serverFeet) ? serverFeet : this.predictedFeet;
    // The feet, so the predictor makes the same floor-or-wall call the server makes: a deck the body is standing
    // on is not a wall, and a box more than a step above it is.
    const beforeX = at.x;
    const beforeZ = at.z;
    if (pushOut(this.placedShapes, count, half, this.worldHalfM, at, this.groundUnder(at, count, half, base))) {
      this.predictor.placeAt(at.x, at.z);
      // A swoop driven into a wall loses the speed it was driving into it, so it slides along and stops head-on rather
      // than grinding there at full throttle (SwoopDrive.collide).
      if (this.driving) {
        this.drive.collide(this.predictor.x - beforeX, this.predictor.z - beforeZ);
      }

      // What the predictor actually took: `placeAt` refuses a non-finite push, so its own position is the authority on
      // where the body ended up and `at` is only a request.
      at.x = this.predictor.x;
      at.z = this.predictor.z;
    }

    // <b>Where it ENDED UP, which is the altitude the frame draws.</b> Sampling only before the push-out leaves the
    // avatar standing at the height of the spot it was ejected from, and that spot is inside a wall.
    this.predictedFeet = this.groundUnder(at, count, half, base);
  }

  /**
   * The structures near the predicted body, with the shapes to push out of.
   *
   * <b>Pooled, because this runs every frame.</b> The entries are reused and a count returned rather than a fresh
   * array of fresh literals per structure per frame — the same reason the packer evaluates into typed arrays.
   *
   * The geometry is the server's, published once per session and pre-turned per asset, so a frame does no parsing, no
   * trigonometry and no allocation.
   *
   * @returns How many entries of {@link placedShapes} are live.
   */
  /**
   * Where a body standing here would have its feet, so the push-out can tell a floor from a wall.
   *
   * <b>The terrain raised by whatever published surface is within a step of `feet`</b>, which is the client's half of
   * the server's `GroundOnShapes`. A surface more than a step above the feet is not climbed, so the answer depends on
   * where the feet were — which is why the base is a parameter and not read from the field: one frame must sample twice,
   * before and after the push-out, and both samples have to be relative to the same previous altitude.
   *
   * @param at Where to stand.
   * @param count How many entries of {@link placedShapes} are live.
   * @param half The body's half-extent.
   * @param feet The altitude the feet were at, or `NaN` on the first frame — then the terrain stands for it.
   */
  private groundUnder(at: Position, count: number, half: number, feet: number): number {
    const terrain = this.profile.ground.heightAt(at.x, at.z);
    return groundOnShapes(this.placedShapes, count, half, at, terrain, Number.isFinite(feet) ? feet : terrain);
  }

  /**
   * The altitude the locally predicted body stands at, or `NaN` when nothing is predicted.
   *
   * Written by {@link advancePrediction} once the push-out has settled, and read by the frame that draws the avatar.
   */
  get predictedGroundY(): number {
    return this.predictedFeet;
  }

  /**
   * Puts the sun's shadow slab over the camera's own patch of ground.
   *
   * <b>Centred on what the camera is looking at, on the ground, in RENDER space.</b> The slab is 128 m across against
   * a 16 km planet, so where it sits is the whole question: on the eye it would cover the sky behind a first-person
   * camera and half of what is in front; on the planet coordinate it would be five kilometres from the scene, because
   * everything the client draws is relative to an origin snapped near the camera.
   *
   * Nothing is handed to the ground any more: the terrain is a Babylon material now and Babylon shadows it.
   *
   * @param wanted Whether the viewer wants shadows at all.
   * @param targetX What the camera orbits, planet east.
   * @param targetZ The same, north.
   * @param originX The render origin this frame, planet east.
   * @param originZ The same, north.
   */
  private frameSun(wanted: boolean, targetX: number, targetZ: number, originX: number, originZ: number): void {
    this.lighting.setShadows(wanted);
    this.lighting.frameShadows(targetX - originX, this.profile.ground.heightAt(targetX, targetZ), targetZ - originZ);
  }

  /**
   * Where the avatar's lowest vertex actually is, against what the collision rule says is under it.
   *
   * <b>An instrument, and the reason it exists is that this session could not tell.</b> "The character is flying" was
   * reported five times and argued about from screenshots; the drawn altitude, the height field, the published floor
   * and the mesh's own extent are four different numbers and any one of them can be the wrong one. This puts the gap
   * between the bottom of the body as Babylon actually has it and the surface it should be resting on into the HUD,
   * so the answer is readable wherever the body happens to be standing.
   *
   * @returns The report, or `null` when no avatar is drawn.
   */
  private feetReport(): FeetReport | null {
    const character = this.assets?.character ?? null;
    if (character === null || character.subjectNetId === 0) {
      return null;
    }

    // A rider's body is lifted onto the seat; the feet the collision rule stands on are the swoop's, under it.
    const bottomY = character.rig.lowestY() - character.riderLift;
    if (!Number.isFinite(bottomY)) {
      return null;
    }

    const predicted = this.view.predicted;
    const x = predicted === null ? this.evalX : predicted.x;
    const z = predicted === null ? this.evalZ : predicted.z;
    const terrainY = this.profile.ground.heightAt(x, z);

    // What the collision rule says is under it: the terrain, raised by any published floor within a step.
    let surfaceY = terrainY;
    const half = this.bodyHalfM;
    if (half > 0) {
      const count = this.gatherStructureShapes();
      if (count > 0) {
        this.predictedAt.x = x;
        this.predictedAt.z = z;
        surfaceY = this.groundUnder(this.predictedAt, count, half, this.predictedFeet);
      }
    }

    return { bottomY, terrainY, surfaceY, serverY: this.evalY, gapM: bottomY - surfaceY };
  }

  /**
   * Fills {@link placedShapes} from this frame's structures, once per refresh of them: the prediction, the camera and the
   * HUD all ask, and the answer only changes when {@link updateStaticAssets} takes a new set.
   */
  private gatherStructureShapes(): number {
    if (this.shapesEpoch === this.structureEpoch && this.shapesSet === this.assetSet) {
      return this.shapesCount;
    }

    this.shapesEpoch = this.structureEpoch;
    this.shapesSet = this.assetSet;
    this.shapesCount = this.gatherStructureShapesNow();
    return this.shapesCount;
  }

  private shapesEpoch = -1;
  private shapesSet: AssetSet | null = null;
  private shapesCount = 0;

  private gatherStructureShapesNow(): number {
    const set = this.assetSet;
    if (set === null) {
      return 0;
    }

    const pool = this.placedShapes;
    let count = 0;
    for (let i = 0; i < this.structureCount; i++) {
      const sample = this.structureSamples[i];
      const shape = this.shapeFor(set, sample.asset);
      if (shape === null) {
        continue;
      }

      if (count === pool.length) {
        pool.push({ shape, x: sample.x, z: sample.z, quarters: sample.yaw, y: sample.y });
      } else {
        const entry = pool[count];
        entry.shape = shape;
        entry.x = sample.x;
        entry.z = sample.z;
        entry.quarters = sample.yaw;
        entry.y = sample.y;
      }

      count++;
    }

    return count;
  }

  /** One asset's geometry, pre-turned on first use; `null` for an asset that collides as its footprint. */
  private shapeFor(set: AssetSet, assetIndex: number): AssetShape | null {
    if (assetIndex < 0 || assetIndex >= set.collision.length) {
      return null;
    }

    let shape = this.shapeCache[assetIndex];
    if (shape === undefined) {
      const boxes = set.collision[assetIndex];
      // An asset the server collides as its footprint publishes no boxes, and the client must do the same rather than
      // invent geometry the authority does not have.
      shape = boxes.length === 0 ? null : prepareShape(boxes);
      this.shapeCache[assetIndex] = shape;
    }

    return shape;
  }

  /**
   * The prepared geometry by asset index.
   *
   * Three states, and the type has to carry all three: a shape, `null` for an asset the server collides as its
   * footprint, and `undefined` for one not yet prepared. `noUncheckedIndexedAccess` is off in this project, so the
   * sentinel has to be in the element type or the compiler reads the miss as unreachable.
   */
  private shapeCache: (AssetShape | null | undefined)[] = [];

  /** Reused each frame: the structures the predicted body may be inside, pooled. */
  private readonly placedShapes: PlacedShape[] = [];

  /** Reused: the predicted position being pushed out. */
  private readonly predictedAt = { x: 0, z: 0 };

  /** Half a body's width, from the server; 0 before it has said, which predicts no collision rather than guessing. */
  private bodyHalfM = 0;

  /**
   * Tells this app how wide a body is.
   *
   * @param halfM The server's own half-extent, or 0 for a server that does not publish one.
   */
  setBodyHalf(halfM: number, worldHalfM: number): void {
    this.bodyHalfM = Number.isFinite(halfM) && halfM > 0 ? halfM : 0;
    this.worldHalfM = Number.isFinite(worldHalfM) && worldHalfM > 0 ? worldHalfM : 0;
  }

  /** Half the world's edge, from the server: the push-out clamps to it exactly where the server's does. */
  private worldHalfM = 0;

  /** The announced asset set, for resolving a structure's replicated asset index. */
  private assetSet: AssetSet | null = null;

  /** The netId {@link predictor} is anchored on, so a change of avatar re-anchors instead of integrating from the old one. */
  private predictedSubject = 0;

  /**
   * The keys' intent: on foot, a direction in the camera's frame; on the swoop, wherever the drive is carrying the bike
   * ({@link driveIntent}).
   */
  private keyIntent(): MoveIntent | null {
    // The destination menu holds the keys, on every path that reads them — the frame loop's and the keydown flush's: an arrow key in it is not a
    // step away from the terminal, and a flush that skipped this sent one (review).
    if (useUi.getState().ticketMenu) {
      return null;
    }

    if (this.driving) {
      return this.driveIntent();
    }

    return movementIntent(this.input.heldKeys, this.eyeCamera.yaw, this.boost);
  }

  /**
   * The swoop's motion as an intent: where the bike is GOING (not where it points — a slide is the difference) at the share
   * of the granted swoop speed it is doing, on the wire's 255ths so the predictor integrates exactly what the server will.
   * `null` once it is parked, which owes the server one stop and then silence, as letting go of the keys does on foot.
   */
  private driveIntent(): MoveIntent | null {
    const source = this.source;
    const max = source === null ? Number.NaN : speedForClass(SpeedClass.swoop, this.grantedSpeeds(source), this.boost);
    const speed = this.drive.speed;
    if (!(max > 0) || !(speed > 0)) {
      return null;
    }

    const throttle = Math.max(1, Math.min(255, Math.round((speed / max) * 255))) / 255;
    return { heading: this.drive.heading, speedClass: SpeedClass.swoop, boost: this.boost, throttle };
  }

  /**
   * Advances the swoop under the rider's hands, once a frame before the intent is read: mounted, it is put under the body
   * facing where the body faced; dismounted, it stops counting. The camera turns with it, keeping whatever angle the viewer
   * had it at, so a chase view stays a chase view and a look over the shoulder stays one.
   */
  private stepDrive(dtS: number, steering: boolean): void {
    const riding = this.ridingNow(performance.now());
    if (!riding) {
      this.driving = false;
      return;
    }

    const source = this.source;
    const max = source === null ? Number.NaN : speedForClass(SpeedClass.swoop, this.grantedSpeeds(source), this.boost);
    if (!this.driving) {
      this.driving = true;
      this.hover.y = Number.NaN;
      // Facing into the screen when the eye camera is on it — W then goes where the viewer looks, as it did on foot — and
      // where the body faced otherwise. The body turns onto it at its own rate (`character-mount.ts`). The camera's forward
      // is (sin yaw, cos yaw) in the ground plane (`movementIntent`), hence the arguments' order.
      const y = this.eyeCamera.yaw;
      this.drive.reset(
        steering
          ? Math.atan2(Math.cos(y), Math.sin(y))
          : this.haveIntentHeading
            ? this.intentHeading
            : this.avatarHeading,
      );
      this.driveYaw = this.drive.yaw;
    }

    const input = steering && !useUi.getState().ticketMenu ? driveInputOf(this.input.heldKeys) : PARKED_HANDS;
    if (max > 0) {
      this.drive.step(dtS, input, max);
    }

    // The renderer's yaw runs the other way round from a heading (`movementIntent`'s basis), hence the minus.
    const turned = Math.atan2(Math.sin(this.drive.yaw - this.driveYaw), Math.cos(this.drive.yaw - this.driveYaw));
    this.driveYaw = this.drive.yaw;
    if (steering && turned !== 0) {
      this.eyeCamera.rotateBy(-turned, 0);
    }
  }

  /** The swoop's handling while this session rides it; see `swoop-drive.ts`. */
  private readonly drive = new SwoopDrive();

  /** How high it floats; see `swoop-hover.ts`. Stepped after collision, on the ground the body ended up over. */
  private readonly hover = new SwoopHover();

  /** The terrain's height, for the hover's footprint: a field so the frame does not allocate a closure. */
  private readonly terrainAt = (x: number, z: number): number => this.profile.ground.heightAt(x, z);

  /** The hover's height when it is the body's this frame, or `NaN` on foot and before it has a ground to float over. */
  private get hoverY(): number {
    return this.driving && this.predictor.active ? this.hover.y : Number.NaN;
  }

  /** Whether {@link drive} is the body's motion: mounted, as far as {@link ridingNow} knows. */
  private driving = false;

  /** The drive's yaw last frame, for turning the camera by what the bike turned. */
  private driveYaw = 0;

  /** What the 1 key last asked for, and when: the client draws it at once and the server's `mounted` decides once it has answered. */
  private mountAsked = false;
  private mountAskedAtMs = Number.NEGATIVE_INFINITY;

  /**
   * Whether this session's avatar is on its swoop: what the 1 key asked for, for long enough to be answered, and the
   * server's replicated `mounted` after that — so a refused ask (indoors, queued for a shuttle) puts the rider back on foot.
   */
  private ridingNow(nowMs: number): boolean {
    const server = this.selfField('mounted') === 1;
    // The ask stands until the server has answered — which ends the window early once it agrees — or the window runs out.
    if (nowMs - this.mountAskedAtMs < MOUNT_ANSWER_MS && server !== this.mountAsked) {
      return this.mountAsked;
    }

    this.mountAskedAtMs = Number.NEGATIVE_INFINITY;
    return server;
  }

  /** The 1 key: onto the swoop, or off it. Not in a room — the swoop does not fit through the door — and only for a playing session. */
  private toggleMount(atMs: number): void {
    const source = this.source;
    if (source === null || !source.canPlay || source.selfNetId === 0 || isIndoors(this.profile.kind)) {
      return;
    }

    // Nor with a ticket: the server refuses a mount while its passenger waits for the shuttle, and drawing one for the answer's round trip ran the
    // prediction tens of metres ahead at the swoop's speed before it snapped back (review).
    const travel = useUi.getState().travel.phase;
    if (travel === 'asking' || travel === 'queued' || travel === 'boarding') {
      return;
    }

    const now = performance.now();
    const next = !this.ridingNow(now);
    this.mountAsked = next;
    this.mountAskedAtMs = now;
    source.setMount(next);

    // The held keys now mean a different speed: said at once, as a gait change is.
    this.flushGaitNow(atMs);
  }

  /** The speeds the server granted this session, for the predictor and the HUD. */
  private grantedSpeeds(source: DataSource): GrantedSpeeds {
    return {
      walkMps: source.selfNumber('walkMps'),
      runMps: source.selfNumber('runMps'),
      swoopMps: source.selfNumber('swoopMps'),
    };
  }

  private sendMovementIntent(source: DataSource, nowMs: number, steering: boolean): void {
    // The destination menu holds the keys: an arrow key in it is not a step away from the terminal.
    const keyed = steering ? this.keyIntent() : null;

    // A walk to or through a door (`walkIn` / `walkOut`) supplies the intent the keys would, so the prediction, the refresh
    // window and the stop at the end are the keyboard's own. Any key takes the body back.
    if (keyed !== null) {
      this.doorWalk.cancel();
    }

    const intent = keyed ?? (this.driving ? null : this.walkIntent(nowMs));

    // A14b: when the gait changed, the instant it changed at — and `NaN` otherwise, which is every other frame. See
    // `gaitChanged` for why a heading change is not one of these.
    //
    // Against the PREDICTOR's own record and not against `lastIntent`, because by the time this runs the key handler has
    // already sent the change and moved `lastIntent` on. Comparing with that would hide every transition from the
    // predictor, which would then integrate whole frames while the server integrated from the key — the error the split
    // exists to remove, reintroduced at the frame scale.
    // The facing of one's OWN body comes from the intent, not from the replicated velocity, and the difference only
    // shows at a stop. A corrected body is walked backwards by the replicated motion for a tick or two, so a facing
    // taken from that vector turns the character round — and then LATCHES, because the moment the velocity reaches
    // exactly zero `headingOf` keeps its last value, which is the one the correction produced. The intent has no such
    // state: a release says "stop", never "go the other way", so the heading it last asked for is the one to keep.
    // Only for a body this session steers; everything else has nothing but its velocity to be read from.
    if (intent !== null && intent.speedClass !== SpeedClass.stop) {
      this.intentHeading = intent.heading;
      this.haveIntentHeading = true;
    }

    const changedAtMs =
      this.predictedClass === gaitOf(intent) && this.predictedBoost === boostOf(intent)
        ? Number.NaN
        : this.takeDeclaredChange(nowMs);
    this.predictedClass = gaitOf(intent);
    this.predictedBoost = boostOf(intent);

    // A14: the predictor is driven by the CURRENT intent, whether or not this frame sends one. The send is coalesced and rate
    // limited (REFRESH_MS), so a predictor fed only on sends would stand still for up to a third of a second at a time.
    if (this.predictor.active) {
      if (intent === null || intent.speedClass === SpeedClass.stop) {
        this.predictor.clearIntent(changedAtMs);
      } else {
        this.predictor.setIntent(
          intent.heading,
          speedForClass(intent.speedClass, this.grantedSpeeds(source), boostOf(intent)) * throttleOf(intent),
          changedAtMs,
        );
      }
    }

    if (intent === null) {
      // The keys are up. If the avatar was moving it must be TOLD to stop — the server is steering towards a destination a second
      // of travel ahead, so silence lets the character walk on for about five metres after the key came up.
      // Cleared either way, so a standing session sends nothing at all and the next press reads as a change rather than as a
      // repeat the refresh window would have swallowed for up to a third of a second.
      this.emitIntent(source, null, this.changePhase(source, changedAtMs), nowMs);
      return;
    }

    if (!intentChanged(intent, this.lastIntent) && nowMs - this.lastIntentMs < REFRESH_MS) {
      return;
    }

    // A swoop in a curve changes heading and throttle every frame; at most one send per DRIVE_SEND_MS keeps it under the
    // command's 30/s rate, and the predictor is driven by the current intent every frame regardless (above).
    if (this.driving && gaitOf(intent) === gaitOf(this.lastIntent) && nowMs - this.lastIntentMs < DRIVE_SEND_MS) {
      return;
    }

    this.emitIntent(source, intent, this.changePhase(source, changedAtMs), nowMs);
  }

  /**
   * Sends a gait change the instant the key moved, rather than on the frame that notices it.
   *
   * @param atMs The key event's own timestamp.
   *
   * @remarks
   * <b>A frame of delay is a tick boundary one time in five, and that costs a whole tick of travel (A14b).</b> The phase
   * tells the server which part of the tick the intent changed in, and the server splits THAT tick — so the command has to
   * reach it before the tick ends. Sending from the frame loop meant a key in the last frame of a tick arrived after the
   * boundary, the server split the following tick instead, and the gesture came out one step long or one step short:
   * measured on loopback at 10 Hz, every transition past phase 0.8 was wrong by 0.498 m and nothing below it was wrong at
   * all. Sending here cuts the window from a frame to the socket write.
   *
   * It also takes half a frame off the start latency, which A14 measures, and that is a side effect rather than the point.
   *
   * <b>The held set is already up to date.</b> `CameraInput` registers its own `keydown` on `window` in its constructor,
   * which runs before this listener is added, and listeners on one target fire in registration order.
   *
   * <b>Gated more loosely than the frame loop.</b> That one also requires the avatar to have been LOCATED this frame,
   * which is a frame-time fact this cannot see. The cost of the difference is an intent sent in the gap between a session
   * gaining `selfNetId` and its avatar appearing in the view, which the server validates and refuses like any other.
   */
  private flushGaitNow(atMs: number): void {
    const source = this.source;
    if (source === null || !source.canPlay || source.selfNetId === 0 || useUi.getState().cameraMode !== 'eye') {
      return;
    }

    const intent = this.keyIntent();
    if (!gaitChanged(intent, this.lastIntent)) {
      return;
    }

    // Declared once, here, and read by the frame loop for the predictor: both sides have to integrate from the SAME
    // instant, and this is the only place that knows which one the server was told about.
    const declared = this.declareChange(source, atMs);
    this.declaredChangeAtMs = declared.atMs;
    this.emitIntent(source, intent, declared.phase, atMs);
  }

  /**
   * The instant the key handler told the server about, or `nowMs` for a change that came from somewhere else.
   *
   * @param nowMs This frame's timestamp.
   * @returns The instant to integrate the change from.
   *
   * @remarks
   * Consumed, because a declaration belongs to exactly one transition. The fallback covers a gait change with no key event
   * behind it — the camera leaving eye mode, a session losing `canPlay` — where no phase was sent either.
   */
  private takeDeclaredChange(nowMs: number): number {
    const declared = this.declaredChangeAtMs;
    this.declaredChangeAtMs = Number.NaN;
    return Number.isFinite(declared) ? declared : nowMs;
  }

  /** What the key handler told the server, for the frame loop to drive the predictor with. */
  private declaredChangeAtMs = Number.NaN;

  /**
   * The instant this client will honour for a transition, and the phase that tells the server about it.
   *
   * @param source The live session, for its RTT.
   * @param atMs When the key actually moved.
   * @returns The instant to integrate from, and the wire phase in 255ths.
   *
   * @remarks
   * <b>A phase names a tick, and a command that misses that tick costs a whole step.</b> The server splits the tick the
   * phase belongs to; if the command arrives after that tick has ended it splits the next one instead, having already paid
   * a full step of the old velocity — so the gesture comes out one step long or one step short. Measured on loopback at
   * 10 Hz: with the send on the next frame every transition past phase 0.8 was wrong by 0.498 m; sending from the key
   * handler moved that edge to 0.99, which is still one gesture in fourteen.
   *
   * <b>So the client declares a boundary it can still reach.</b> Inside the margin it gives up on the current tick, names
   * the next boundary instead — phase 0, the whole of the next tick to the new velocity — and waits for it before
   * integrating. The cost is the margin's worth of input lag on the small fraction of presses that land in it: 5 ms on
   * loopback, which is a third of a frame and below what anyone can feel. The alternative is being exactly one step wrong,
   * visibly, instead of a few milliseconds late, invisibly.
   *
   * <b>The margin is the round trip and not half of it.</b> What has to fit is the uplink plus however long the server
   * takes to drain, and the floor is what loopback still needs: the measured dead zone there was about 4 ms with an RTT of
   * half a millisecond, so the write and the drain are the term that does not scale with distance.
   */
  private declareChange(source: DataSource, atMs: number): { atMs: number; phase: number } {
    const phase = this.clock.subTickPhase(atMs, source.rttMs / 2);
    if (!Number.isFinite(phase)) {
      // No grid yet: the server treats 0 as "the whole tick is mine", which is what it did before phases existed.
      return { atMs, phase: 0 };
    }

    const toBoundaryMs = (1 - phase) * this.clock.tickPeriodMs;
    if (toBoundaryMs < SEND_MARGIN_MS + source.rttMs) {
      return { atMs: atMs + toBoundaryMs, phase: 0 };
    }

    return { atMs, phase: Math.round(phase * 255) };
  }

  /**
   * Sends an intent — or the stop that ends one — and records it as what was last sent.
   *
   * @param source The live session.
   * @param intent What to say, or `null` for "the keys are up", which owes a stop and then nothing.
   * @param changePhase The sub-tick phase, in 255ths.
   * @param atMs When this counts as having been sent, for the refresh window.
   *
   * @remarks
   * One rule in one place, because there are now two callers — the key handler and the frame loop — and the half that is
   * easy to get wrong is the `null` branch: the server aims at a destination a second of travel away, so a client that
   * merely stopped talking would watch its character walk on for about five metres.
   */
  private emitIntent(source: DataSource, intent: MoveIntent | null, changePhase: number, atMs: number): void {
    if (intent === null) {
      const stop = stopAfter(this.lastIntent);
      if (stop !== null) {
        source.moveDir(stop.heading, stop.speedClass, changePhase, boostOf(stop), 0);
        this.lastIntentMs = atMs;
      }

      this.lastIntent = null;
      return;
    }

    source.moveDir(intent.heading, intent.speedClass, changePhase, boostOf(intent), throttleByte(intent));
    this.lastIntent = intent;
    this.lastIntentMs = atMs;
  }

  /** The gait the PREDICTOR was last driven with, which is how a transition is still visible after the send took it. */
  private predictedClass: number = SpeedClass.stop;

  /** The boost the predictor was last driven with, for the same reason as {@link predictedClass}. */
  private predictedBoost = 1;

  /**
   * The wire's sub-tick phase for an intent that changed at `atMs`, in 255ths — `MoveDir.changePhase`.
   *
   * @param source The live session, for its RTT.
   * @param atMs When the intent changed, or `NaN` for an intent that did not.
   * @returns The phase, or 0 when there is nothing to measure against — which is what the server treats as "the whole
   *   tick is mine", the behaviour it had before the phase existed.
   *
   * @remarks
   * Half the RTT, because the clock's tick grid is anchored on the ARRIVAL of each tick's frame and therefore sits one
   * downlink behind the server's own boundaries; uncorrected, every phase would be short by `delay / period` — nothing on
   * loopback and a third of a step at 30 ms on a 100 ms tick. Half the round trip is the usual estimate of a one-way
   * delay and the residual is the asymmetry between the two directions, which is a few milliseconds.
   */
  private changePhase(source: DataSource, atMs: number): number {
    return Number.isFinite(atMs) ? this.declareChange(source, atMs).phase : 0;
  }

  /**
   * Runs the realm fade's clock, and writes it back only when it moved.
   *
   * <b>Only on a change.</b> The store is a zustand store React subscribes to, so publishing an identical object every
   * frame would re-render the whole UI sixty times a second to show the same thing — which is the mistake the toolbar's
   * narrow selectors were written to avoid.
   */
  private advanceTransition(nowMs: number): void {
    const ui = useUi.getState();
    const next = advance(ui.transition, nowMs);
    if (next !== ui.transition) {
      ui.setTransition(next);
    }
  }

  /** The profile for a scene kind, built on first use, or `null` for a kind this build cannot draw. */
  private profileFor(kind: SceneKind): SceneProfile | null {
    const existing = this.profiles.get(kind);
    if (existing !== undefined) {
      return existing;
    }

    if (kind === 'space') {
      const space = new SpaceProfile(this.scene);
      this.profiles.set(kind, space);
      return space;
    }

    if (kind !== 'interior' && kind !== 'dungeon') {
      return null;
    }

    // A dungeon is served as an interior is — one cell, the same size, the same flat floor — so it is the same indoor
    // scene. A building's room is a structure the server sends and the structure layer draws; a dungeon has none, so its
    // profile draws the generic box instead.
    const room = new InteriorProfile(this.scene, INTERIOR_EDGE_M, kind);
    this.profiles.set(kind, room);
    return room;
  }

  /** Binds this app to a world and its grid: archetype identity, one layer per archetype, the attack lines, the HUD rows. */
  private adopt(world: WorldStore, grid: AggregateGrid): void {
    const previous = this.archetypes;
    this.world = world;
    this.grid = grid;
    this.selectionTextNetId = 0;
    this.archetypes = resolveArchetypes(world.schema);

    // The selection goes with the store it was made in. netIds are dense and reused, so the one held across a reconnect
    // very often resolves in the NEW store — to a different entity, which the inspector would then show under the old
    // number and `follow` would fly to. A store swap is exactly the case 05-client.md § 6 wrote the selection rules for.
    useUi.getState().select(0);

    // Everything resolved against the OLD catalog goes with it. The archetype index is the dangerous one: the catalog
    // numbers archetypes in its own order and `sameArchetypes` exists precisely because that order can differ, so a
    // cached index would read another archetype's `kind` and `region` after a swap. The prompt itself is dropped
    // because it names a port from a world this session may no longer be in.
    this.shuttleArchetype = -1;
    this.shuttleFrames = 0;
    useUi.getState().setShuttleport(null);

    // The Babylon layers are rebuilt only when the archetypes themselves changed. A seed or population change also
    // reaches here with the same schema, and disposing meshes, materials and shaders to build identical ones on a button
    // press is churn for nothing.
    if (!sameArchetypes(previous, this.archetypes)) {
      for (const layer of this.layers) {
        layer.dispose();
      }

      this.layers = this.buildLayers();
    }

    this.bindLayers();
    // From the ARCHETYPES and not from the layers: there is one more layer than there are archetypes (the shuttleport's
    // dome), and these rows are indexed by archetype everywhere they are read.
    this.layerStats = this.archetypes.infos.map((info) => ({
      archetype: info,
      held: 0,
      near: 0,
      far: 0,
    }));
    this.heatChannels = grid.schema.archetypes.map((index) => ({
      name: this.archetypes.name(index),
      label: this.archetypes.label(index),
    }));
    this.heatMask = this.heatChannels.map(() => false);
    this.applyUi(useUi.getState());
  }

  /**
   * One layer per archetype of the current schema, each with the style its NAME selects, plus one for the shuttleport.
   *
   * <b>The port layer is APPENDED, so the first `infos.length` entries stay 1:1 with the archetypes</b> - which is what
   * `layerStats` and `publishStats` index by. Everything that merely iterates the list (update, pick, bind, dispose,
   * `screenOf`) picks the extra layer up for free, the two places that count rows do not see it, and `publishStats`
   * folds its instances into its archetype's own row.
   */
  private buildLayers(): EntityLayer[] {
    const layers = this.archetypes.infos.map(
      (info, index) =>
        new EntityLayer(
          index,
          styleFor(info.name),
          this.scene,
          -1,
          // A port is drawn by the layer below instead, as a dome. Omitted here so it is not drawn twice, one mesh
          // inside the other.
          info.name === STRUCTURE_ARCHETYPE ? StructureKind.Shuttleport : -1,
        ),
    );
    for (const [index, info] of this.archetypes.infos.entries()) {
      // The near band of a people layer becomes the crowd; its far band stays the sprite it already was.
      layers[index].drawsCharacters = CHARACTER_ARCHETYPES.includes(info.name);
    }

    const structures = this.archetypes.infos.findIndex((info) => info.name === STRUCTURE_ARCHETYPE);
    if (structures >= 0) {
      layers.push(new EntityLayer(structures, styleFor(SHUTTLEPORT_STYLE), this.scene, StructureKind.Shuttleport, -1));
    }

    return layers;
  }

  private bindLayers(): void {
    for (const layer of this.layers) {
      layer.bind(this.world.archetypeStore(layer.archetype));
    }

    this.attacks.bind(this.world);
  }

  private onUiChange(state: UiState, previous: UiState): void {
    if (state.population !== previous.population || state.seed !== previous.seed) {
      this.startSource(state);
    }

    if (state.latencyMs !== previous.latencyMs || state.jitterMs !== previous.jitterMs) {
      this.source?.latency?.setLatency(state.latencyMs, state.jitterMs);
    }

    if (state.paused !== previous.paused) {
      this.source?.setPaused(state.paused);
    }

    if (state.cameraRequest !== null && state.cameraRequest !== previous.cameraRequest) {
      this.mapCamera.glideTo(state.cameraRequest.x, state.cameraRequest.z, state.cameraRequest.distance);
    }

    // An ask, and nothing here assumes it was granted: the scene changes when a REALM block says it did, which
    // `adoptSourceRealm` notices. A refusal arrives in ACKS and leaves everything as it was.
    if (state.realmRequest !== null && state.realmRequest !== previous.realmRequest) {
      this.source?.viewRealm(state.realmRequest.realmId);
    }

    // The same contract as the realm ask: the server anchors the session or refuses, and nothing here pretends either
    // happened. What follows is a RESET, which `adoptSourceReset` handles, and then `adoptSubject` re-finds the subject
    // by the netId the server names in SELF.
    if (state.spectateRequest !== null && state.spectateRequest !== previous.spectateRequest) {
      this.source?.spectate(state.spectateRequest.netId);
    }

    // The same contract again: an ask the server grants on its port's next landing, refuses with an ACKS reason, or
    // rate-limits. Nothing here moves the avatar — the flight arrives as a position the server sent.
    if (state.boardRequest !== null && state.boardRequest !== previous.boardRequest) {
      this.source?.boardShuttle(state.boardRequest.city);
    }

    if (state.walkToRequest !== null && state.walkToRequest !== previous.walkToRequest) {
      this.walkTo(state.walkToRequest.x, state.walkToRequest.z);
    }

    // Walking is this client steering its own body, so nothing is asked of the server but the moves themselves.
    if (state.doorWalkRequest !== null && state.doorWalkRequest !== previous.doorWalkRequest) {
      const ask = state.doorWalkRequest.netId;
      if (ask > 0) {
        this.walkIn(ask);
      } else if (ask === 0) {
        this.walkOut();
      } else {
        this.doorWalk.cancel();
      }
    }

    this.applyUi(state);
  }

  /** The walk to or through a door under way, if any; see {@link walkIn}. */
  private readonly doorWalk = new DoorWalker();

  /** This frame's intent from a walk under way, or `null`. */
  private walkIntent(nowMs: number): MoveIntent | null {
    if (!this.doorWalk.active) {
      return null;
    }

    const x = this.predictor.active ? this.predictor.x : this.eyeX;
    const z = this.predictor.active ? this.predictor.z : this.eyeZ;
    return this.doorWalk.intent(x, z, this.realm?.realmId ?? -1, nowMs);
  }

  /**
   * Walks the body to a point of the city it is in, around the buildings (`city-path.ts`): a run along a path found on
   * the walls' grid, stopping on the point. Any movement key takes the body back, as for a door.
   *
   * <b>Searched only near the body.</b> A target past {@link WALK_SEARCH_M} — a port the pane only knows as a city centre,
   * kilometres off — is searched to that distance along the way and walked straight from there: the grid is the
   * rectangle round both ends, and at 3 km it was ten million cells and a hundred megabytes in one click (review), for
   * ground with nothing on it — only held structures are walls at all.
   *
   * @returns Why it did not start, or `null` when it did.
   */
  walkTo(x: number, z: number): string | null {
    if (!this.predictor.active) {
      return 'this session is not steering a body';
    }

    const fromX = this.predictor.x;
    const fromZ = this.predictor.z;
    const far = Math.hypot(x - fromX, z - fromZ);
    const reach = Math.min(1, WALK_SEARCH_M / Math.max(far, 1e-6));
    const toX = fromX + (x - fromX) * reach;
    const toZ = fromZ + (z - fromZ) * reach;
    const margin = 60;
    const count = this.gatherStructureShapes();
    const started = performance.now();
    const grid = wallGrid(
      this.placedShapes,
      count,
      Math.min(fromX, toX) - margin,
      Math.min(fromZ, toZ) - margin,
      Math.max(fromX, toX) + margin,
      Math.max(fromZ, toZ) + margin,
      this.bodyHalfM > 0 ? this.bodyHalfM : 0.3,
    );
    const path = findPath(grid, fromX, fromZ, toX, toZ);
    this.lastPathMs = performance.now() - started;
    if (path === null) {
      return 'no way round the buildings';
    }

    if (reach < 1) {
      path.push([x, z]);
    }

    this.doorWalk.start(path, path.length, this.realm?.realmId ?? -1, performance.now(), true);
    return null;
  }

  /** How long the last {@link walkTo} took to find its path, milliseconds. */
  lastPathMs = 0;

  /**
   * Walks this session's body into a building: round it if needed, to where its door's published path starts, along the
   * path and through the door.
   *
   * @param netId The building, as replicated.
   * @returns Why it did not start, or `null` when it did.
   */
  walkIn(netId: number): string | null {
    const sample = this.structureSamples.slice(0, this.structureCount).find((s) => s.netId === netId) ?? null;
    if (sample === null) {
      return 'that building is not in range';
    }

    return this.startWalk(sample, pathIn);
  }

  /** Walks this session's body out of the room it is in, through the room's navigation to its way out. */
  walkOut(): string | null {
    const room =
      this.structureSamples.slice(0, this.structureCount).find((s) => s.kind === StructureKind.Interior) ?? null;
    if (room === null) {
      return 'not in a room';
    }

    return this.startWalk(room, pathOut);
  }

  private startWalk(sample: StructureSample, plan: typeof pathIn): string | null {
    const set = this.assetSet;
    const id = set === null ? null : assetForStructure(set, sample.asset);
    const descriptor = id === null ? null : (this.assets?.library.described(id) ?? null);
    if (descriptor === null) {
      return 'its asset is not loaded';
    }

    if (!this.predictor.active) {
      return 'this session is not steering a body';
    }

    const pose = placement([sample.x, sample.y, sample.z], structureYaw(sample.yaw));
    const route = plan(descriptor, pose, [this.predictor.x, this.predictor.z]);
    if (route === null) {
      return 'it publishes no door path';
    }

    this.doorWalk.start(route.points, route.pathStart, this.realm?.realmId ?? -1, performance.now());
    return null;
  }

  private applyUi(state: UiState): void {
    // By NAME, and visible unless the user turned it off: a schema may hold an archetype the UI has never named. Keyed
    // on the layer's own archetype rather than its position, because the shuttleport's layer shares one with the
    // structures and follows that toggle.
    for (const layer of this.layers) {
      layer.visible = state.layers[this.archetypes.name(layer.archetype)] ?? true;
    }
    for (let i = 0; i < this.heatChannels.length; i++) {
      this.heatMask[i] = state.heatmap[this.heatChannels[i].name] ?? false;
    }

    this.attacks.visible = state.showAttacks;
  }

  private frame(): void {
    const now = performance.now();
    const dt = Math.min(0.1, (now - this.lastFrameMs) / 1000);
    this.lastFrameMs = now;
    this.adoptSourceWorld();
    this.adoptSourceReset();
    this.adoptSourceRealm();
    this.adoptSubject(now);
    this.syncOverview();
    this.advanceTransition(now);
    this.clock.update(now);
    // Held keys may stop following: read the UI state after them.
    this.input.update(dt);
    let ui = useUi.getState();

    let hasSelection = false;
    if (ui.selectedNetId !== 0) {
      if (this.world.locate(ui.selectedNetId) === NOT_FOUND) {
        // The entity left the view. Its netId will be reused for another entity, so the selection cannot wait for it.
        ui.select(0);
        ui = useUi.getState();
      } else {
        hasSelection = this.locateSelected(ui.selectedNetId);
      }
    }

    if (hasSelection && ui.follow) {
      this.mapCamera.follow(this.selectedX, this.selectedZ);
    }

    // A10: a PLAYING session's eye camera rides the avatar the server gave it — `selfNetId`, from the `SELF` block, which
    // is what `Control` fills — and not the selection. Clicking something must not move the camera off your own body; the
    // selection stays what the inspector shows and what `SetTarget` names. A god camera keeps riding its selection, which
    // is what CLI3D-10 built this for.
    const live = this.source;
    const playing = live !== null && live.canPlay && live.selfNetId !== 0;
    const eyeNetId = playing ? live.selfNetId : ui.selectedNetId;

    // Into the eye subject's OWN slot when it is not the selection. Sharing one slot drew the selection marker and its
    // label on the player's own avatar, because the eye subject was evaluated second and overwrote it.
    const haveEyeSubject = playing ? this.locateEyeSubject(eyeNetId) : hasSelection;

    // The store's camera actions need to know this session has an avatar to ride: eye mode is legal without a selection
    // for a player and not for a god camera, and `select(0)` must not drop a player out of third person. Published on
    // change only — this runs every frame and the store is one React subscribes to.
    if (ui.playing !== playing) {
      ui.setPlaying(playing);
      ui = useUi.getState();
    }

    // The shuttleport prompt. Published on change only, like `playing` above, and only for a session that has a body to
    // fly: a god camera is not standing anywhere.
    //
    // `haveEyeSubject` and not `playing` alone, because the search reads `eyeX`/`eyeZ` and those are only written when
    // the eye subject was located this frame. `playing` stays true across a despawn, a realm crossing and a reconnect
    // before the SELF entity arrives — and the prompt then measured from wherever the avatar last stood, offering a
    // boarding the server answers with ShuttleNotAtPort. The camera latch below already gated on both.
    this.updateShuttlePrompt(playing && haveEyeSubject, ui);
    this.updateTravel(ui);
    this.driveShuttle(now, ui);

    // A10: a player arrives in third person, behind their own avatar. Done here rather than in the store's initial state
    // because it depends on the session being a player's, which is only known once the catalog has arrived — and done
    // ONCE, so the camera-mode button and the wheel still belong to the viewer afterwards.
    //
    // <b>The latch closes only if the mode actually took.</b> It used to be set before the request and the request was
    // refused — `setCameraMode('eye')` dropped it because the session had no selection — so the one chance to enter third
    // person was spent on a call that did nothing, and a logged-in player sat in the god camera for the whole session.
    // Reading the mode back costs one store read on the frames before it succeeds and none after.
    if (playing && haveEyeSubject && !this.playerCameraReady) {
      this.eyeCamera.setDistance(DEFAULT_THIRD_PERSON_M);
      if (ui.cameraMode !== 'eye') {
        ui.setCameraMode('eye');
        ui = useUi.getState();
      }

      this.playerCameraReady = ui.cameraMode === 'eye';
    }

    // Eye mode rides that subject; without one there is nothing to ride, so it falls back to the map rather than
    // stranding the viewer at the origin (CLI3D-10).
    const inEye = ui.cameraMode === 'eye' && haveEyeSubject;

    // Bolted to your own avatar, eased when watching somebody else's — see EyeCamera.rigid.
    // A9: the held keys, in the camera's frame, as a movement intent.
    //
    // <b>Before the camera's own update, and A14 is why.</b> The three are circular: the heading is the eye camera's yaw, the
    // camera's position follows the predicted body, and the prediction integrates the intent. Something has to be a frame
    // stale, and the yaw is by far the cheapest: `update` EASES it toward its goal, so reading it here costs one ease step —
    // a fraction of a degree — where ordering the prediction after the camera instead costs a whole frame of movement latency
    // on every key press, and ordering the camera after the prediction without this costs a constant 8 cm of camera lag at a
    // run. Measured latency is 9.4 ms p50 with this order.
    //
    // Gated on IN EYE and not merely on playing, because the heading is the eye camera's yaw. A10 leaves the camera-mode button to
    // the viewer, and in map mode that yaw is whatever it was when the viewer left eye mode — nothing updates it — while W/A/S/D
    // belong to the map camera's pan. Sending then walked the avatar along a stale facing the person could not see, in response to
    // keys they were using for something else.
    if (playing) {
      this.stepDrive(dt, inEye);
      this.sendMovementIntent(live, now, inEye);
    }

    // A14: the predicted avatar, stepped with the intent just computed. Started on the authority the first frame the session
    // has one, stopped for anything that is not a player steering its own body — a god camera, a spectator, or a session whose
    // intents the server refuses.
    this.advancePrediction(playing && haveEyeSubject ? eyeNetId : 0, now, dt);
    if (this.driving && this.predictor.active) {
      this.hover.step(
        dt,
        this.terrainAt,
        this.predictedFeet,
        this.predictor.x,
        this.predictor.z,
        this.drive.yaw,
        this.drive.speed,
      );
    }
    if (playing) {
      this.watchDoors(now);
    }

    this.eyeCamera.rigid = playing;
    if (inEye) {
      // A god camera rides its selection, so the two slots hold the same subject and either would do; a player's camera
      // must read the eye slot, which is the only one holding the avatar.
      // The PREDICTED position when there is one: the camera and the body must agree, or the view slides off a character
      // standing where the server last confirmed it.
      const predicting = playing && this.predictor.active;
      const camX = predicting ? this.predictor.x : playing ? this.eyeX : this.selectedX;
      // On the swoop, the height it floats at: the server stands the body on every bump of the ground, and a camera bolted
      // to that shook with it (swoop-hover.ts).
      const hoverY = this.hoverY;
      const camY = playing ? (Number.isFinite(hoverY) ? hoverY : this.eyeY) : this.selectedY;
      const camZ = predicting ? this.predictor.z : playing ? this.eyeZ : this.selectedZ;
      if (this.faceOutOfGate) {
        // Out of the gate is the port's +Z, which is the renderer's yaw 0: the shuttleport is never turned.
        this.faceOutOfGate = false;
        this.eyeCamera.jumpTo(camX, camY, camZ, 0);
      }

      if (playing && this.arrival !== null) {
        // Snapped, every frame until it is placed: under the curtain, but the curtain is a DOM element that reaches full
        // black a frame after the store asks it to, so a glide from the old realm's coordinates would be on screen.
        if (!this.placeArrival(camX, camY, camZ, now)) {
          this.eyeCamera.jumpTo(camX, camY, camZ, this.eyeCamera.yaw);
        }

        this.eyeSubject = eyeNetId;
      } else if (this.eyeSubject !== eyeNetId) {
        this.eyeSubject = eyeNetId;
        if (this.eyeRebased) {
          // The SAME body under a new id: a `RESET` empties the store and netIds are allocated densely per view, so an
          // id held across one almost never survives. Re-aiming here is what made the camera snap to the avatar's own
          // facing every twenty seconds or so, discarding wherever the viewer had turned to look — for a frame in which
          // nothing about the subject changed except its number.
          this.eyeCamera.follow(camX, camY, camZ);
        } else {
          this.eyeCamera.jumpTo(camX, camY, camZ, playing ? this.eyeHeading : this.selectedHeading);
        }
      } else {
        this.eyeCamera.follow(camX, camY, camZ);
      }

      // Consumed by the first eye frame after the reset, whichever branch took it: a rebase describes one renumbering.
      this.eyeRebased = false;

      // The testbench's camera (`orbit-camera.ts`): short of the first wall behind the body, and in a room capped at
      // 1.9 m over the feet — under the 2 m the colliders stop at, so the walls still stop it — and at 7 m back.
      const indoors = isIndoors(this.profile.kind);
      this.eyeCamera.maxEyeHeightM = indoors ? 1.9 : Infinity;
      this.eyeCamera.maxDistanceM = indoors ? 7 : MAX_EYE_DISTANCE_M;
      this.cameraShapes = this.gatherStructureShapes();
      this.eyeCamera.clearance = this.cameraShapes > 0 ? this.eyeClearance : null;
      this.eyeCamera.update(dt);

      // A wall that pulled the eye in close fades the body rather than putting the view inside a head.
      this.assets?.character?.setOpacity(playing ? bodyOpacityAt(this.eyeCamera.effectiveDistance) : 1);
    } else {
      this.eyeSubject = 0;
    }

    this.paintFade(now);

    // An arrival with no eye camera to place — a session that left eye mode — gives the screen back. An arrival whose
    // curtain is no longer holding is over: the safety valve already gave the screen back, and lifting it again from black
    // was a second flash. NOT on `inEye` or `playing`: for the first frames after the reset the session's own netId and its
    // avatar are not known yet, so both read false while the camera is very much in eye mode — and giving the screen back
    // then showed the glide this exists to hide.
    if (this.arrival !== null) {
      if (ui.transition.phase !== 'hold') {
        this.arrival = null;
      } else if (ui.cameraMode !== 'eye') {
        ui.setTransition(arrived(ui.transition, this.arrival.kind, now));
        this.arrival = null;
      }
    }

    // The map camera orbits the ground under its target, so it needs the terrain height there BEFORE it recomputes its eye.
    this.mapCamera.groundY = this.profile.ground.heightAt(this.mapCamera.targetX, this.mapCamera.targetZ);
    this.mapCamera.update(dt);
    const cam = inEye ? this.eyeCamera : this.mapCamera;
    this.activeAltitude = cam.altitude;
    this.updateNav(now);
    this.paintNav(cam.yaw);
    this.origin.follow(cam.targetX, cam.targetZ);
    // The placements are held in planet coordinates, so they follow the origin the frame it snaps. One node, every frame:
    // cheaper than asking whether it moved.
    this.assets?.rebase(this.origin.x, this.origin.z);
    const view = this.view;
    view.renderTick = this.clock.renderTick;
    view.renderFrac = this.clock.renderFrac;
    view.originX = this.origin.x;
    view.originZ = this.origin.z;
    view.eyeX = cam.eyeX - this.origin.x;
    view.eyeY = cam.eyeY;
    view.eyeZ = cam.eyeZ - this.origin.z;
    view.viewportWidth = this.cssSize.width;
    view.viewportHeight = this.cssSize.height;
    view.pixelsPerMetre = this.cssSize.height / (2 * Math.tan(cam.fov / 2));
    view.selectedNetId = ui.selectedNetId;
    if (this.predictor.active && live !== null) {
      this.predictedOverride.netId = live.selfNetId;
      this.predictedOverride.x = this.predictor.x;
      this.predictedOverride.z = this.predictor.z;
      // Always the prediction, "Server pos" or not: that toggle adds a translucent ghost at the server's position beside
      // this body (`driveAvatar`) rather than moving this one there, so both are on screen and the gap between them is the
      // lead (prediction.md § 5).
      view.predicted = this.predictedOverride;
    } else {
      view.predicted = null;
    }

    this.camera.position.set(view.eyeX, view.eyeY, view.eyeZ);
    // The eye camera's VIEW pitch: in a room it places the eye with a flattened one, and the view must look along the same ray.
    this.camera.rotation.set(inEye ? this.eyeCamera.viewPitch : cam.pitch, cam.yaw, 0);
    // EVERY field of the active camera's contract, `fov` included. It used to be set once in the constructor from the map
    // camera, so eye mode rendered at the map's 0.8 rad while `pixelsPerMetre` above was computed from the eye's 1.1 — a
    // 1.45× disagreement that put every LOD threshold 31 % low and would never throw, only look wrong.
    this.camera.fov = cam.fov;
    this.camera.minZ = cam.nearPlane;
    this.camera.maxZ = cam.farPlane;
    this.scene.updateTransformMatrix(true);
    const matrix = this.scene.getTransformMatrix().m;
    extractFrustumPlanes(matrix, view.planes);

    // What something other than the packer is drawing: the rigged avatar, and every structure standing as an asset.
    const hidden = this.hidden;
    hidden.clear();
    const avatar = this.driveAvatar(live, now, dt);
    if (avatar !== 0) {
      hidden.add(avatar);
    }

    this.assetView.eyeX = cam.eyeX;
    this.assetView.eyeY = cam.eyeY;
    this.assetView.eyeZ = cam.eyeZ;
    this.assetView.pixelsPerMetre = view.pixelsPerMetre;
    for (const netId of this.updateStaticAssets(cam.eyeX, cam.eyeZ)) {
      hidden.add(netId);
    }

    for (const netId of this.updateLairAssets(cam.eyeX, cam.eyeZ)) {
      hidden.add(netId);
    }

    this.updateGizmos(ui);

    view.hidden = hidden;

    // <b>The crowd collects as the layers pack, not in a pass of its own.</b> Everything a body needs — its place in
    // render space, its heading, its ground speed — is computed by the packer anyway, so a character layer diverts its
    // near band into this array instead of writing a box. A second pass would evaluate every body twice.
    const crowd = this.crowd;
    const bodies = this.crowdBodies;
    // Not `length = 0`: the records are pooled and reused, so the array keeps its capacity and `crowdCount` is what
    // says how many of them this frame filled.
    view.crowdCount = 0;
    view.crowd = crowd === null ? null : bodies;
    view.perSecond = 1000 / this.clock.tickPeriodMs;
    // Only once the router exists: before it, a diverted creature would be routed nowhere and drawn by nobody.
    view.crowdDraws = (crowd?.layer.isDrawing ?? false) && this.crowdRouter !== null;

    for (const layer of this.layers) {
      layer.update(view);
    }

    const router = this.crowdRouter;
    if (crowd !== null && router !== null) {
      // Each body to the crowd that draws it: the characters (civilians and the camps' enemies) at layer 0, then each
      // species. The gait figures are the SERVER's; before the owner block lands they are NaN and the graph falls back to
      // the clips' own speeds, which is the same thing the avatar does.
      router.route(bodies, view.crowdCount);
      const walk = live?.selfNumber('walkMps') ?? Number.NaN;
      const run = live?.selfNumber('runMps') ?? Number.NaN;
      const people = router.layers[0];
      crowd.layer.update(people.bodies, people.count, view, now / 1000, walk, run);
      for (const [k, species] of this.creatureCrowds.entries()) {
        const routed = router.layers[k + 1];
        species.layer.update(routed.bodies, routed.count, view, now / 1000, walk, run);
      }

      this.crowdSwoops?.update(crowd.layer.riders, crowd.layer.riderCount);
    }

    this.attacks.update(view);
    this.sendRegion(now, ui.viewRadius, cam.targetX, cam.targetZ, playing);
    this.sendViewRadius(ui.playerRadius);
    // `stats` builds a whole snapshot — objects, a template string, a pass over the byte window — so it is read four
    // times a second by `publishStats`, never once a frame. The radius the server is actually serving has its own cheap
    // accessor for exactly this reason.
    const serverRadius = this.source?.effectiveRadiusM ?? 0;
    const nearRadius = serverRadius > 0 && Number.isFinite(serverRadius) ? serverRadius : ui.viewRadius;

    const g = this.groundView;
    g.originX = view.originX;
    g.originZ = view.originZ;
    g.eyeX = view.eyeX;
    g.eyeY = view.eyeY;
    g.eyeZ = view.eyeZ;
    g.viewCenterX = this.regionX;
    g.viewCenterZ = this.regionZ;
    g.centerX = cam.targetX;
    g.centerZ = cam.targetZ;
    // The terrain's tolerance is spent in RENDERED pixels; everything else in this frame is in CSS pixels and stays
    // that way. `pixelsPerMetre` is built from the CSS height while the canvas renders at up to MAX_DEVICE_RATIO times
    // it, so passing it unscaled solved `d = ε·P/τ` in the wrong units and delivered 1.5× the geometric error the Detail
    // slider asked for on every HiDPI display. Sprites, labels, lines and the pick are deliberately CSS-pixel
    // quantities — a 12 px dot is 12 px to the viewer whatever the backing store does — so only this one is scaled.
    g.pixelsPerMetre = view.pixelsPerMetre * this.renderScale;
    g.pixelTolerance = ui.terrainPixelError;
    this.frameSun(ui.shadows, cam.targetX, cam.targetZ, view.originX, view.originZ);
    g.nearRadius = nearRadius;
    g.heatMask = this.heatMask;
    g.showGrid = ui.showGrid;
    g.showDebug = ui.showDebug;
    g.hasSelection = hasSelection;
    g.selectionX = this.selectedX;
    g.selectionZ = this.selectedZ;
    this.profile.update(g, this.grid, this.source?.debug ?? null);

    if (!ui.showLabels) {
      this.labels.hideAll();
    } else {
      // Place names are the PLANET's: they are Tatooine's towns, and in a room or in space they name somewhere the
      // viewer is not. The selection's tag below is realm-agnostic and stays either way.
      if (this.profile.kind === 'planet') {
        this.labels.update(
          matrix,
          view.originX,
          view.originZ,
          this.cssSize.width,
          this.cssSize.height,
          this.profile.ground,
        );
      } else {
        this.labels.hidePlaces();
      }

      if (hasSelection) {
        this.labels.updateSelection(
          matrix,
          view.originX,
          view.originZ,
          this.cssSize.width,
          this.cssSize.height,
          this.selectedX,
          this.selectedZ,
          this.selectionText,
          this.selectedY,
        );
      } else {
        this.labels.hideSelection();
      }
    }

    this.frameJs[this.frameJsNext] = performance.now() - now;
    this.frameJsNext = (this.frameJsNext + 1) % FRAME_SAMPLES;
    this.frameJsCount = Math.min(FRAME_SAMPLES, this.frameJsCount + 1);
    if (now - this.lastStatsMs >= STATS_INTERVAL_MS) {
      this.lastStatsMs = now;
      this.publishStats(ui, nearRadius);
    }
  }

  /** Logs a failed frame at most every few seconds: a bug that throws every frame must not flood the console. */
  private reportFrameError(error: unknown): void {
    const now = performance.now();
    if (now - this.lastErrorLogMs < ERROR_LOG_INTERVAL_MS) {
      this.suppressedErrors++;
      return;
    }

    const suppressed = this.suppressedErrors;
    this.suppressedErrors = 0;
    this.lastErrorLogMs = now;
    console.error(suppressed > 0 ? `Frame failed (${suppressed} more since the last report)` : 'Frame failed', error);
  }

  /**
   * The region of interest follows the ACTIVE camera's look-at point, sent when it moved 5 % of the radius, at most five
   * times a second.
   *
   * It used to read the map camera unconditionally. That is right only while the map camera is following the same thing
   * the eye is riding, which the store arranges on entering eye mode and nothing keeps true: turn following off and the eye
   * rides an NPC out of the served disc while the region stays where the map was left, so entities silently stop arriving.
   */
  /**
   * Asks the server for a wider sphere, when the slider has moved.
   *
   * <b>On CHANGE, not on a cadence, which is the opposite of {@link sendRegion}.</b> A region is a hull the server can
   * clamp or reject and the client re-offers; a radius is a single number the server either grants until further notice
   * or refuses, so re-sending an unchanged one is pure ingress. The command is coalesced latest-per-session, so a drag
   * costs one command however many frames it spans.
   *
   * Null is "the server has not told us its range yet", not a radius: sending 0 would mean "back to the profile's own",
   * which is a different statement and would fight a slider the person has already moved.
   */
  private sendViewRadius(radius: number | null): void {
    const source = this.source;
    if (source === null || radius === null || radius === this.sentViewRadius) {
      return;
    }

    source.setViewRadius(radius);
    this.sentViewRadius = radius;
  }

  /** The radius last asked for, so an unchanged slider sends nothing. */
  private sentViewRadius: number | null = null;

  /**
   * Keeps what the server serves in step with the camera: a player in the god camera on a planet is served the planet
   * (`Overview`), and is back behind its avatar anywhere else.
   *
   * <b>The camera alone is not the god camera for a player.</b> Its session is served a sphere around the avatar, so a
   * camera flown up over it looked down on an empty planet with one lit disc. Asked every frame with the state wanted,
   * which the source turns into one command per change; a new session (a resume) starts off and is asked again.
   */
  private syncOverview(): void {
    const source = this.source;
    if (source === null) {
      return;
    }

    const ui = useUi.getState();
    const decision = overviewPolicy(ui.playing, ui.cameraMode, source.realm?.scene ?? null);
    if (decision.backToAvatar) {
      // Through a door, onto a shuttle to space or into a dungeon: the server ends the overview on its side for the same
      // crossing.
      ui.setCameraMode('eye');
    }

    source.setOverview(decision.overview);
  }

  private sendRegion(now: number, radius: number, x: number, z: number, playing: boolean): void {
    if (
      this.source === null ||
      // A ride anchors the session on an entity, so the profile is a sphere around IT and the region is not read at all.
      // Sending one anyway is not merely waste: a 1 500 m disc quantized into a 64 m interior collapses to a degenerate
      // hull, which the server refuses and counts — so the HUD grew a refusal warning that meant nothing was wrong.
      rideOwnsTheView(this.ride) ||
      // <b>And a session steering its own body is anchored for exactly the same reason, which this used not to say.</b>
      // The server's geometry for an anchored session is a sphere around the avatar; a region it is also sent is a
      // DIFFERENT geometry, and switching between the two costs a full `RESET` every time — "a session whose committed
      // geometry is a ClientRegion's holds what its hull named: only a RESET says what it holds now"
      // (`PushReplication.Geometry.cs`). Measured in the browser at one reset per region sent, 3 for 3 over 45 s, and
      // none at all over the next 45 with the sends suppressed. Each one empties the store, renumbers every netId and
      // re-sends the view: visible as a hitch, as a chat clear, and — until the fix beside this one — as the camera
      // snapping back to the avatar's facing every twenty seconds.
      //
      // Except in the god camera (`Overview`), where the session's profile is the god camera's: under `--god-region` its
      // region IS what the server serves, exactly as for a god session.
      (playing && !this.source.overview) ||
      !regionDue(
        x,
        z,
        radius,
        this.regionX,
        this.regionZ,
        this.regionRadius,
        now,
        this.lastRegionMs,
        REGION_INTERVAL_MS,
      )
    ) {
      return;
    }

    this.source.setRegion(x, z, radius);
    this.regionX = x;
    this.regionZ = z;
    this.regionRadius = radius;
    this.lastRegionMs = now;
  }

  /**
   * The entity under a click, in CSS pixels relative to the canvas; 0 when none is under it or near it.
   *
   * The ray comes from inverting the very matrix the frame was drawn with, so the pick cannot disagree with the picture
   * about where anything is. Each layer tests it against what it actually drew — boxes in the near band, sprite discs in
   * the far one — and falls back to the nearest centre within {@link PICK_RADIUS_PX} only when nothing was hit at all.
   */
  private pickAt(x: number, y: number): number {
    const matrix = this.scene.getTransformMatrix().m;
    if (!invertMatrix4(matrix, this.pickInverse)) {
      return 0;
    }

    if (!unprojectRay(this.pickInverse, x, y, this.view.viewportWidth, this.view.viewportHeight, this.pickRay)) {
      return 0;
    }

    // What the viewer can actually see past. The ray is in render space and the field is in planet space, so the march
    // is handed an origin shifted by the render origin — the same shift `layer-packer` applies to every instance.
    const planet = this.pickPlanetRay;
    planet.ox = this.pickRay.ox + this.view.originX;
    planet.oy = this.pickRay.oy;
    planet.oz = this.pickRay.oz + this.view.originZ;
    planet.dx = this.pickRay.dx;
    planet.dy = this.pickRay.dy;
    planet.dz = this.pickRay.dz;
    const groundT = rayGroundT(planet, this.field, PICK_MAX_DISTANCE_M);

    const best = this.pickBest;
    best.archetype = -1;
    best.netId = 0;
    best.pixels = Infinity;
    best.depth = Infinity;
    best.inside = false;
    for (const layer of this.layers) {
      layer.pick(this.pickRay, matrix, this.view, x, y, PICK_RADIUS_PX, best, groundT);
    }

    return best.netId;
  }

  /** Evaluates the selection's position into {@link selectedX}/{@link selectedZ}; false when it has none. */
  private locateSelected(netId: number): boolean {
    if (!this.evaluate(netId)) {
      return false;
    }

    this.selectedX = this.evalX;
    this.selectedZ = this.evalZ;
    // Altitude and heading, for the eye camera (CLI3D-10). A still entity keeps the heading it last had, the way the
    // renderer's own meshes do: `headingOf`'s fallback, not a snap to north.
    this.selectedY = this.evalY;
    this.selectedHeading = headingOf(this.evalVx, this.evalVz, this.selectedHeading);
    if (this.selectionTextNetId !== netId || this.selectionTextArchetype !== this.evalArchetype) {
      this.selectionTextNetId = netId;
      this.selectionTextArchetype = this.evalArchetype;
      this.selectionText = `${this.archetypes.label(this.evalArchetype)} #${netId}`;
    }

    return true;
  }

  /**
   * Evaluates the EYE SUBJECT's position into {@link eyeX}/{@link eyeY}/{@link eyeZ}/{@link eyeHeading}.
   *
   * @param netId The entity the camera is riding — a player's own avatar, or the god camera's selection.
   * @returns Whether it was found and has a position.
   *
   * @remarks
   * Separate from {@link locateSelected} rather than sharing its slot, and it deliberately does NOT touch
   * {@link selectionText}: the label belongs to whatever the viewer clicked. See {@link eyeX}.
   */
  private locateEyeSubject(netId: number): boolean {
    if (!this.evaluate(netId)) {
      return false;
    }

    this.eyeX = this.evalX;
    this.eyeZ = this.evalZ;
    this.eyeY = this.evalY;
    this.eyeHeading = headingOf(this.evalVx, this.evalVz, this.eyeHeading);
    return true;
  }

  /**
   * Interpolates one netId's placement for this frame into the `eval*` fields.
   *
   * @param netId The entity.
   * @returns Whether it is here and has a position.
   *
   * @remarks
   * Fields rather than a returned object, so two subjects per frame cost no allocation. Written and read within one
   * frame step and never held across one.
   */
  /**
   * Publishes the shuttleport the avatar is standing at, or null.
   *
   * <b>Every eighth frame, not every frame.</b> The search walks the live `WorldObject` slots, which is hundreds inside
   * a city, and the answer it produces changes only as fast as a person walks — at a run, eight frames is under 15 cm.
   * Published on change, because the store is one React subscribes to and a prompt that re-rendered sixty times a
   * second would cost more than the search.
   *
   * @param playing Whether this session drives a body at all.
   * @param ui The UI state, already read this frame.
   */
  private updateShuttlePrompt(playing: boolean, ui: UiState): void {
    if (++this.shuttleFrames < SHUTTLE_SEARCH_FRAMES) {
      return;
    }

    this.shuttleFrames = 0;
    let next: ShuttleportPrompt | null = null;
    if (playing) {
      if (this.shuttleArchetype < 0) {
        this.shuttleArchetype = this.archetypes.infos.findIndex((info) => info.name === STRUCTURE_ARCHETYPE);
      }

      // Resolved against THIS catalog: `adopt` replaces the archetype set on every store swap and handles the two
      // differing, so an index cached across one would read another archetype's `kind` and `region`. It is reset there
      // with `select(0)`, for the same reason.

      if (this.shuttleArchetype >= 0) {
        // The predicted position when there is one, for the reason the camera uses it: the prompt has to agree with
        // where the body is drawn, or it appears a stride before or after the player sees themselves arrive.
        const x = this.predictor.active ? this.predictor.x : this.eyeX;
        const z = this.predictor.active ? this.predictor.z : this.eyeZ;
        // One search: the port whose shuttle is drawn is the nearest within sight, and the prompt's is that same port when it is within the
        // server's own boarding radius — published with the schedule, this file's figure only before it arrives.
        const seen = nearestShuttleport(
          this.world.archetypeStore(this.shuttleArchetype),
          this.clock.renderTick,
          this.clock.renderFrac,
          x,
          z,
          SHUTTLE_VIEW_M,
        );
        const reach = this.shuttleSchedule?.boardM ?? SHUTTLE_PROMPT_M;
        if (seen !== null && seen.distanceM <= reach) {
          next = { city: seen.city, distanceM: Math.round(seen.distanceM), atTerminal: this.atTicketTerminal(x, z) };
        }

        this.flightPort = seen === null ? null : { city: seen.city, x: seen.x, z: seen.z };
      }
    } else {
      this.flightPort = null;
    }

    const held = ui.shuttleport;
    if (
      held === null
        ? next !== null
        : next === null ||
          next.city !== held.city ||
          next.distanceM !== held.distanceM ||
          next.atTerminal !== held.atTerminal
    ) {
      ui.setShuttleport(next);
      if (next === null || !next.atTerminal) {
        ui.setTicketMenu(false);
      }
    }
  }

  /** The server's shuttle timetable, from `/typhon/demo.json`, or `null` where nothing flies. */
  private shuttleSchedule: ShuttleSchedule | null = null;

  /** Takes the timetable the server published. */
  setShuttleSchedule(schedule: ShuttleSchedule | null): void {
    this.shuttleSchedule = schedule;
  }

  private shuttleView: ShuttleView | null = null;

  /** The port whose shuttle is drawn, or `null`; found with the prompt's port, every eighth frame. */
  private flightPort: { readonly city: number; readonly x: number; readonly z: number } | null = null;

  /** The pad's top at {@link flightPort}, cached per port: the published floor at its centre. */
  private padTop = { x: Number.NaN, z: Number.NaN, y: Number.NaN };

  /** Whether the next eye frame puts the camera facing out of the gate the body just walked out of. */
  private faceOutOfGate = false;

  /** Where a user stands to use the ticket terminal of the shuttleport at (x, z), or `null` before the descriptors are in. */
  private ticketStandPoint(x: number, z: number): { x: number; z: number } | null {
    const library = this.assets?.library ?? null;
    const port = library?.described(SHUTTLEPORT_ASSET) ?? null;
    const terminal = library?.described(TICKET_TERMINAL_ASSET) ?? null;
    if (port === null || terminal === null) {
      return null;
    }

    // The shuttleport is never turned and stands on its own ground; the stand point's height does not matter here.
    return ticketTerminalOf(port, new Map([[TICKET_TERMINAL_ASSET, terminal]]), x, 0, z, 0);
  }

  /**
   * Whether a point is at the ticket terminal of a shuttleport in view: within its published interaction radius of
   * where a user stands to use it (`ticket-terminal.ts`).
   */
  private atTicketTerminal(x: number, z: number): boolean {
    const library = this.assets?.library ?? null;
    const set = this.assetSet;
    const port = library?.described(SHUTTLEPORT_ASSET) ?? null;
    const terminal = library?.described(TICKET_TERMINAL_ASSET) ?? null;
    if (library === null || set === null || port === null || terminal === null) {
      return false;
    }

    const children = new Map([[TICKET_TERMINAL_ASSET, terminal]]);
    for (let i = 0; i < this.structureCount; i++) {
      const s = this.structureSamples[i];
      if (s.kind !== StructureKind.Shuttleport || Math.abs(s.x - x) > 40 || Math.abs(s.z - z) > 40) {
        continue;
      }

      const use = ticketTerminalOf(port, children, s.x, s.y, s.z, s.yaw);
      if (use !== null && Math.hypot(use.x - x, use.z - z) <= use.radiusM) {
        return true;
      }
    }

    return false;
  }

  /**
   * The shuttle of the port in view, flown on the server's timetable; and, for a passenger with a ticket, the screen
   * going black as it touches down — the server boards a session's passenger on that tick and moves it to the
   * destination, and {@link updateTravel} brings the screen back when it has arrived.
   */
  private driveShuttle(nowMs: number, ui: UiState): void {
    const schedule = this.shuttleSchedule;
    const port = this.flightPort;
    const view = this.shuttleView;
    if (schedule === null || port === null || view === null || this.profile.kind !== 'planet') {
      view?.update(0, 0, 0, null);
      return;
    }

    if (this.padTop.x !== port.x || this.padTop.z !== port.z) {
      const terrain = this.profile.ground.heightAt(port.x, port.z);
      const count = this.gatherStructureShapes();
      this.predictedAt.x = port.x;
      this.predictedAt.z = port.z;
      this.padTop = {
        x: port.x,
        z: port.z,
        y: count > 0 ? groundOnShapes(this.placedShapes, count, 0.5, this.predictedAt, terrain, terrain + 3) : terrain,
      };
    }

    const tickHz = 1000 / this.clock.tickPeriodMs;
    const moment = shuttleMoment(
      this.clock.renderTick + this.clock.renderFrac,
      port.city,
      schedule,
      tickHz,
      this.momentScratch,
    );
    view.update(port.x, this.padTop.y, port.z, flightAt(moment, this.poseScratch));

    // A passenger with a ticket for this port's shuttle: black as it touches down — once per ticket. A landing the server did not board on would
    // otherwise fade, release on the hold valve, and fade again every few seconds for the whole window (review).
    const here = ui.shuttleport;
    if (
      ui.travel.phase === 'queued' &&
      moment.landed &&
      here !== null &&
      here.city === port.city &&
      ui.transition.phase === 'idle' &&
      this.fadedForTicket !== ui.travel.sinceMs
    ) {
      this.fadedForTicket = ui.travel.sinceMs;
      ui.setTransition(askedFor(ui.transition, 'travel', nowMs));
    }
  }

  /** The ticket (its queue time) the touchdown fade last fired for; see {@link driveShuttle}. */
  private fadedForTicket = Number.NaN;

  private readonly momentScratch: Writable<ShuttleMoment> = { landed: false, untilLandingS: 0, sinceDepartureS: 0 };
  private readonly poseScratch: Writable<FlightPose> = { x: 0, y: 0, z: 0, yaw: 0, noseUp: 0 };

  /** Frames since the last port search; see {@link updateShuttlePrompt}. */
  private shuttleFrames = 0;

  /**
   * Advances the travel machine and publishes what it says.
   *
   * <b>Every frame, unlike the port search above, and for two reasons.</b> One is the queue's elapsed figure, which a
   * person reads as a clock. The other is the unanswered-ask timeout: a command the client dropped or the wire lost
   * produces no frame at all, so the only thing that can notice is the passage of time.
   *
   * @param ui The UI state, already read this frame.
   */
  private updateTravel(ui: UiState): void {
    const source = this.source;
    const ask = ui.boardRequest;
    if (source === null || ask === null) {
      if (ui.travel.phase !== 'idle') {
        ui.setTravel(nextTravel(ui.travel, this.travelInputs(null, null)));
      }

      return;
    }

    // Fed once: a reason is consumed on the frame the machine acts on it, and a refusal that arrives in the same frame
    // as its own ask waits for the next one, when the machine is in `asking` and able to take it.
    const pending = ui.shuttleRefusal;
    const refusal = pending !== null && pending.seq !== this.travelRefusalSeq ? pending.reason : null;

    const next = nextTravel(ui.travel, this.travelInputs(ask, refusal));
    if (pending !== null && (next.phase === 'refused' || next.phase === 'idle')) {
      this.travelRefusalSeq = pending.seq;
      ui.clearShuttleRefusal();
    }

    // Arrived: the screen comes back, on the camera turned to look out of the destination's gate the body walked out of.
    if (next.phase === 'arrived' && ui.travel.phase !== 'arrived') {
      ui.setTransition(arrived(ui.transition, 'travel', performance.now()));
      // Only for the eye view, which consumes it: latched in the map, it turned the camera the next time the eye view was entered (review).
      this.faceOutOfGate = ui.cameraMode === 'eye';
    }

    ui.setTravel(next);
  }

  /** This frame's inputs for {@link nextTravel}: the ask, what the server says of the body, and where it stands. */
  private travelInputs(
    ask: { readonly city: number; readonly seq: number } | null,
    refusal: number | null,
  ): Parameters<typeof nextTravel>[1] {
    // The predicted position when there is one, for the reason the prompt uses it: the arrival test compares against
    // the destination's disc, and the drawn body is the one the person is watching.
    const city = ask === null ? undefined : CITIES[ask.city];
    return {
      ask,
      activity: this.selfActivity(),
      refusal,
      x: this.predictor.active ? this.predictor.x : this.eyeX,
      z: this.predictor.active ? this.predictor.z : this.eyeZ,
      destination: city === undefined ? null : { x: city.x, z: city.z, radiusM: city.radius },
      nowMs: performance.now(),
    };
  }

  /** The refusal sequence already fed to the travel machine; see {@link updateTravel}. */
  private travelRefusalSeq = 0;

  /**
   * The avatar's own `activity`, or `NaN` when it cannot be read.
   *
   * <b>From the world store and NOT from `SELF`, which is where this looked first and found nothing.</b> The `SELF`
   * block carries the owner-only fields a session is told about itself; `activity` is an ordinary replicated field on
   * the Player archetype, so `selfNumber('activity')` answers null and the travel machine was handed NaN for ever —
   * it never saw a queue, and every accepted boarding timed out as unanswered. Found by clicking the button in a
   * browser, which no unit test of the machine could have caught: the machine was right and its input was empty.
   */
  private selfActivity(): number {
    return this.selfField('activity');
  }

  /** One of the avatar's ordinary replicated fields, from the world store (see {@link selfActivity}), or `NaN`. */
  private selfField(name: string): number {
    const source = this.source;
    return source === null || source.selfNetId === 0 ? Number.NaN : this.fieldOf(source.selfNetId, name);
  }

  /** A replicated field of any entity in view, or `NaN`. */
  private fieldOf(netId: number, name: string): number {
    const location = this.world.locate(netId);
    if (location === NOT_FOUND) {
      return Number.NaN;
    }

    const store = this.world.archetypeStore(archetypeOf(location));
    const field = store.fieldIndex(name);
    return field < 0 ? Number.NaN : store.fieldAt(field)[slotOf(location)];
  }

  /** The `WorldObject` archetype's index in the live catalog, or -1 before it is known. */
  private shuttleArchetype = -1;

  private evaluate(netId: number): boolean {
    const location = this.world.locate(netId);
    if (location === NOT_FOUND) {
      return false;
    }

    const archetype = archetypeOf(location);
    const store = this.world.archetypeStore(archetype);
    if (!store.hasPosition) {
      return false;
    }

    const slot = slotOf(location);
    evaluateSlot(store, slot, this.clock.renderTick, this.clock.renderFrac, this.scratch, 0);
    const read = this.placement.read(store.dims, this.scratch, 0);
    this.evalX = read.x;
    this.evalZ = read.z;
    this.evalVx = read.vx;
    this.evalVz = read.vz;
    this.evalY = altitudeOf(altitudeField(store), slot, read, this.profile.ground);
    this.evalArchetype = archetype;
    return true;
  }

  /**
   * Draws this session's own body as a rigged character, and says which netId the packer must therefore skip.
   *
   * <b>The subject is the body this session is anchored on</b> — the ride's when it has one, the session's own
   * otherwise. A god camera is anchored on nothing and gets no character, which is right: it is a camera, not a person.
   *
   * <b>Predicted where the packer is predicted.</b> The rig stands at the same place the capsule would have, including
   * the locally predicted position (A14), because the two are the same body and a character half a metre behind its own
   * nameplate is worse than either choice alone. The VELOCITY stays the server's, which is what the heading and the clip
   * both come from.
   *
   * @param live The session's source, or `null` before one is started.
   * @param nowMs This frame's `performance.now()`.
   * @param dt Seconds since the last frame.
   * @returns The netId the packer must not draw, or 0.
   */
  private driveAvatar(live: DataSource | null, nowMs: number, dt: number): number {
    const character = this.assets?.character ?? null;
    if (character === null) {
      return 0;
    }

    const subject = rideSubjectOf(this.ride) || (live?.selfNetId ?? 0);
    if (subject === 0 || !this.evaluate(subject)) {
      character.park();
      this.assets?.ghost?.park();
      return 0;
    }

    // What the server says this session's gaits are worth. NaN until the owner block arrives, which the tuning reads as
    // "no figures yet" and answers with the clips' own speeds rather than with a gait it invented.
    if (live !== null) {
      character.retune(live.selfNumber('walkMps'), live.selfNumber('runMps'));
    }

    const predicted = this.view.predicted;
    const own = predicted !== null && predicted.netId === subject;
    const x = own ? predicted.x : this.evalX;
    const z = own ? predicted.z : this.evalZ;
    // <b>The altitude the prediction settled on</b>, which is the terrain raised by any published floor, deck or ramp
    // under the feet. Re-sampled at the predicted position for the same reason the packer re-samples there: a body
    // walking a dune at the altitude the server last confirmed reads as its feet sinking into the sand. Sampling the
    // TERRAIN here — which is what this line did until the ramps landed — walks the avatar up a building's entry ramp
    // at desert level and into its ground floor from below: the collision agrees the body is on the ramp, and only the
    // renderer disagrees. The server's own figure is the fallback, for the frames before a prediction exists.
    // <b>Bounded by the authority, exactly as the position is.</b> The server runs the same floor-or-wall rule over the
    // same colliders and its answer is the one the world agrees on; the client predicts it only to avoid a frame of lag
    // while climbing. So a prediction that disagrees with the server by more than a step is not a lead, it is a bug —
    // and the visible result of trusting it was an avatar standing in the air above a ramp. Within a step the
    // prediction stands, which is what keeps a climb smooth.
    // <b>The surface the collision rule computed, not the bare height field.</b> A published floor — a step, a deck, a
    // ramp — is what the body is standing ON, and the server agrees: measured at a house's doorstep, the height field
    // said 132.13 while both this client's sampler and the server said 132.92, and drawing the terrain put the avatar
    // 0.79 m inside the stone.
    //
    // This was briefly reverted to the height field while chasing an avatar that appeared to fly. It did not fly: the
    // ground shader had stopped compiling and the terrain was not being drawn at all, so a body standing correctly on
    // it had nothing underneath. The HUD's `Feet` row exists so that confusion cannot be repeated — it reports this
    // gap, and it is what identified both faults.
    //
    // The prediction leads the server by the render delay, so it is preferred; the server's own figure stands before
    // there is a prediction to use.
    const predictedY = this.predictedGroundY;
    const y = own ? (Number.isFinite(predictedY) ? predictedY : this.evalY) : this.evalY;
    // `own` is this session steering this body, which is exactly when an intent exists for it.
    // Otherwise from the replicated velocity, in the rig's convention (`headingFromVelocity`): the SDK's `headingOf` is a
    // Babylon yaw, and fed here it turned a body walking north to face east.
    // On the swoop, where the NOSE points: the intent is where the bike is going, and in a slide the two differ.
    this.avatarHeading =
      own && this.driving
        ? this.drive.yaw
        : own && this.haveIntentHeading
          ? this.intentHeading
          : headingFromVelocity(this.evalVx, this.evalVz, this.avatarHeading);
    // <b>Replicated velocity is metres per TICK, not per second</b> (the SDK's motion codec: a velocity per tick is what
    // makes a changing tick period harmless). The heading does not care — `atan2` is scale-free, which is why the body
    // faced the right way while walking at a fiftieth of its speed — but the clip does: at 50 Hz a 12 m/s run arrives as
    // 0.24 and chooses the walk clip at its slowest rate. The period is the server's own, through the clock.
    const perSecond = 1000 / this.clock.tickPeriodMs;
    const speed = Math.hypot(this.evalVx, this.evalVz) * perSecond;
    // Wall time in seconds: what the graph measures is how long a one-shot has been playing, which is a property of the
    // animation and not of the simulation. The render tick is the wrong clock for it — it is an integer, it is the
    // server's, and it stops when the stream does while the clip is still on screen.
    // On the swoop: this session's own ask at once, anyone else's (a ridden subject) as the server replicates it.
    character.setRiding(own ? this.ridingNow(performance.now()) : this.fieldOf(subject, 'mounted') === 1);
    const hoverY = own ? this.hoverY : Number.NaN;
    if (Number.isFinite(hoverY)) {
      character.setLean(this.drive.roll, this.hover.pitch, hoverY - y + this.drive.bob);
    } else {
      character.setLean(0, 0, 0);
    }
    character.update(subject, x, y, z, speed, this.avatarHeading, nowMs / 1000, dt);
    this.driveGhost(own, subject, speed, nowMs, dt);
    return subject;
  }

  /**
   * "Server pos": the avatar again, translucent, where the SERVER says it is — interpolated like every other entity,
   * facing its replicated velocity — beside the predicted body. The gap between the two is the prediction's lead.
   *
   * Only for this session's own steered body: there is nothing predicted to compare a ridden entity with.
   */
  private driveGhost(own: boolean, subject: number, speed: number, nowMs: number, dt: number): void {
    const ghost = this.assets?.ghost ?? null;
    if (ghost === null) {
      return;
    }

    if (!own || !useUi.getState().showAuthoritative) {
      ghost.park();
      return;
    }

    if (ghost.subjectNetId === 0) {
      // Appearing, it faces the way the avatar does until the server's velocity says otherwise.
      this.ghostHeading = this.avatarHeading;
    }

    this.ghostHeading = headingFromVelocity(this.evalVx, this.evalVz, this.ghostHeading);
    ghost.setRiding(this.fieldOf(subject, 'mounted') === 1);
    ghost.update(subject, this.evalX, this.evalY, this.evalZ, speed, this.ghostHeading, nowMs / 1000, dt);
  }

  /** Where the ghost last faced, kept while the server's velocity is zero. */
  private ghostHeading = 0;

  /**
   * Keeps the structures near the camera drawn as published assets, and says which the packer must skip.
   *
   * <b>Range, not frustum</b>: turning on the spot must not unload the street behind the viewer. The set a session holds
   * at all is already bounded by its interest radius, so this is a second, smaller bound inside it — the distance at
   * which a building is worth its geometry rather than its box.
   *
   * @param eyeX The camera's planet X.
   * @param eyeZ Its planet Z.
   * @returns The netIds drawn as assets.
   */
  private updateStaticAssets(eyeX: number, eyeZ: number): ReadonlySet<number> {
    // Nothing is in range until this frame says so: `structureSamples` keeps its records across frames, so the count is
    // what says how many are this frame's (see `structureCount`).
    this.structureCount = 0;
    this.structureEpoch++;
    const layer = this.staticAssets;
    if (layer === null) {
      return NONE_HIDDEN;
    }

    // BY NAME, never by the schema's index. The live catalog decides the order its archetypes arrive in, and it is not
    // this schema's: `Archetype.WorldObject` is 0 here while the server's catalog put `CityNpc` there. Reading the
    // store at a constant index drew every city NPC as a building and asked a creature store for a `kind` field it does
    // not have — C10's lesson, which the layers already follow and this did not.
    const archetype = this.archetypes.indexOf('WorldObject');
    if (archetype < 0) {
      // Still told, with nothing: a frame that skipped the call would leave the placed instances standing AND stop
      // reporting them as hidden, so every one of them would be drawn twice.
      return layer.update(this.structureSamples, 0, this.assetView);
    }

    const store = this.world.archetypeStore(archetype);
    const samples = this.structureSamples;
    let taken = 0;
    const kindField = store.fieldIndex('kind');
    const yawField = store.fieldIndex('yaw');
    const assetField = store.fieldIndex('asset');
    if (store.hasPosition && kindField >= 0) {
      const kinds = store.fieldAt(kindField);
      // A catalog that does not replicate it leaves every structure facing +X, which is what the mock does.
      const yaws = yawField < 0 ? null : store.fieldAt(yawField);
      // A catalog that does not replicate it leaves every structure without an asset, which draws the renderer's own
      // shape — the behaviour of every structure before the asset pipeline.
      const assets = assetField < 0 ? null : store.fieldAt(assetField);
      const ground = altitudeField(store);
      const reach = STATIC_ASSET_RANGE_M * STATIC_ASSET_RANGE_M;
      for (let i = 0; i < store.liveCount; i++) {
        const slot = store.live[i];
        evaluateSlot(store, slot, this.clock.renderTick, this.clock.renderFrac, this.scratch, 0);
        const at = this.placement.read(store.dims, this.scratch, 0);
        const dx = at.x - eyeX;
        const dz = at.z - eyeZ;
        if (dx * dx + dz * dz > reach) {
          continue;
        }

        // Into the record already there: the array keeps its capacity across frames, which is what the field's own
        // comment asks for and what a fresh literal per structure per frame was not doing.
        if (taken >= samples.length) {
          samples.push({ netId: 0, x: 0, y: 0, z: 0, kind: 0, yaw: 0, asset: -1 });
        }

        const sample = samples[taken++];
        sample.netId = store.netIds[slot];
        sample.x = at.x;
        sample.y = altitudeOf(ground, slot, at, this.profile.ground);
        sample.z = at.z;
        sample.kind = kinds[slot];
        sample.yaw = yaws === null ? 0 : yaws[slot];
        sample.asset = assets === null ? -1 : assets[slot];
      }
    }

    this.structureCount = taken;
    return layer.update(samples, taken, this.assetView);
  }

  /** Reused each frame: the structures in range, which is tens of entries and must not allocate. */
  private readonly structureSamples: StructureSample[] = [];

  /**
   * The lairs within the static-asset range, handed to their layer: a lair's `template` is its sample's asset index, which
   * the layer resolves to its prop.
   *
   * <b>Facing a quarter turn drawn from the position</b>, because a lair replicates no orientation: its creatures are
   * born on the diagonals (`WorldBuilder.LairRingM`), which every quarter turn keeps on the prop's own spawn markers, and
   * a position is stable where a netId is renumbered by a `RESET`.
   */
  private updateLairAssets(eyeX: number, eyeZ: number): ReadonlySet<number> {
    const layer = this.lairAssets;
    if (layer === null) {
      return NONE_HIDDEN;
    }

    const archetype = this.archetypes.indexOf('CreatureLair');
    const store = archetype < 0 ? null : this.world.archetypeStore(archetype);
    const templateField = store === null ? -1 : store.fieldIndex('template');
    if (store === null || !store.hasPosition || templateField < 0) {
      return layer.update(this.lairSamples, 0, this.assetView);
    }

    const samples = this.lairSamples;
    const templates = store.fieldAt(templateField);
    const ground = altitudeField(store);
    const reach = STATIC_ASSET_RANGE_M * STATIC_ASSET_RANGE_M;
    let taken = 0;
    for (let i = 0; i < store.liveCount; i++) {
      const slot = store.live[i];
      evaluateSlot(store, slot, this.clock.renderTick, this.clock.renderFrac, this.scratch, 0);
      const at = this.placement.read(store.dims, this.scratch, 0);
      const dx = at.x - eyeX;
      const dz = at.z - eyeZ;
      if (dx * dx + dz * dz > reach) {
        continue;
      }

      if (taken >= samples.length) {
        samples.push({ netId: 0, x: 0, y: 0, z: 0, kind: 0, yaw: 0, asset: -1 });
      }

      const sample = samples[taken++];
      sample.netId = store.netIds[slot];
      sample.x = at.x;
      sample.y = altitudeOf(ground, slot, at, this.profile.ground);
      sample.z = at.z;
      sample.kind = 0;
      sample.yaw = lairQuarter(at.x, at.z);
      sample.asset = templates[slot];
    }

    return layer.update(samples, taken, this.assetView);
  }

  /**
   * Builds each creature species' crowd, and the router that sends bodies to it.
   *
   * <b>Only the species the server's table names</b>, each once: a beast is one crowd however many templates wear it.
   * A species that will not build stays the renderer's own shape — the router simply has no layer for it.
   */
  private async mountCreatureCrowds(
    library: AssetLibrary,
    set: AssetSet,
    described: ReadonlyMap<string, Descriptor>,
  ): Promise<void> {
    const species: string[] = [];
    for (const kind of set.creatures) {
      for (const id of kind.bodies) {
        if (described.get(id)?.kind === 'creature' && !species.includes(id)) {
          species.push(id);
        }
      }
    }

    const mounted: CrowdMount[] = [];
    const ids: string[] = [];
    for (const id of species) {
      const crowd = await mountCreatureCrowd(this.scene, library, this.lighting, id, SPECIES_CAPACITY, described);
      if (crowd !== null) {
        mounted.push(crowd);
        ids.push(id);
      }
    }

    this.creatureCrowds = mounted;
    this.crowdRouter = new CrowdRouter(set.creatures, this.crowd?.outfits.map((o) => o.id) ?? null, ids);
    this.attacks.onStrike = (netId) => this.crowdRouter?.strike(netId);
  }

  /**
   * How many of {@link structureSamples} are this frame's.
   *
   * <b>The array is pooled, so its length is its high-water mark, not this frame's count.</b> Collision used to walk all of
   * it, so the prediction pushed the body out of structures that had left range — and, through a door, out of the planet's
   * buildings inside a 64 m room, where the server had none.
   */
  private structureCount = 0;

  /** Counts refreshes of {@link structureSamples}, so what is derived from them is rebuilt once per refresh. */
  private structureEpoch = 0;

  /**
   * Reused each frame: where the camera is, in PLANET metres, for the structures' detail level.
   *
   * Planet rather than render space because that is the frame the samples themselves are read in — the origin is
   * subtracted when a thing is handed to the renderer, and a structure's instance carries its own planet placement.
   */
  private readonly assetView = { eyeX: 0, eyeY: 0, eyeZ: 0, pixelsPerMetre: 0 };

  /**
   * The debug overlay ("Gizmos", "Colliders"): drawn over the structures this frame placed, from their descriptors and
   * from the colliders the server published, plus the player's own collision body.
   */
  private updateGizmos(ui: UiState): void {
    const assets = this.assets;
    const set = this.assetSet;
    if (assets === null || set === null) {
      return;
    }

    const options = { markers: ui.showGizmos, colliders: ui.showColliders };
    if (this.gizmos === null) {
      if (!options.markers && !options.colliders) {
        return;
      }

      this.gizmos = new GameplayGizmos(this.scene, assets.root);
    }

    const pool = this.gizmoPlacements;
    let count = 0;
    if (options.markers || options.colliders) {
      for (let i = 0; i < this.structureCount; i++) {
        const sample = this.structureSamples[i];
        const id = assetForStructure(set, sample.asset);
        if (id === null) {
          continue;
        }

        pool[count++] = {
          netId: sample.netId,
          asset: id,
          descriptor: assets.library.described(id),
          pose: placement([sample.x, sample.y, sample.z], structureYaw(sample.yaw)),
          shape: this.shapeFor(set, sample.asset),
          quarters: sample.yaw,
          x: sample.x,
          y: sample.y,
          z: sample.z,
        };
      }
    }

    this.gizmos.update(pool, count, options, (asset) => assets.library.described(asset));

    // The body the prediction collides: the server's square, at the predicted feet. Only for a body this client steers.
    const predicted = this.view.predicted;
    const feetY = this.predictedGroundY;
    this.gizmos.setBody(
      options.markers && predicted !== null && this.bodyHalfM > 0 && Number.isFinite(feetY)
        ? { x: predicted.x, y: feetY, z: predicted.z, half: this.bodyHalfM, height: assets.character?.height ?? 1.65 }
        : null,
    );
  }

  /** The debug overlay, created the first time it is switched on. */
  private gizmos: GameplayGizmos | null = null;

  /** Reused each frame: the placements the overlay is told about. */
  private readonly gizmoPlacements: GizmoPlacement[] = [];

  /** The netIds the packer must skip this frame, rewritten in place. */
  private readonly hidden = new Set<number>();

  /** The structures-as-assets layer, once the asset set is mounted. */
  private staticAssets: StaticAssets | null = null;

  /** The avatar's facing, kept across frames: a body that stops keeps the heading it stopped with. */
  private avatarHeading = 0;

  /** The heading this session last ASKED to move in, which is where its own body faces; see {@link sendMovementIntent}. */
  private intentHeading = 0;
  /** Whether {@link intentHeading} has ever been set — before the first step there is nothing to face but the velocity. */
  private haveIntentHeading = false;

  /**
   * Whether the next eye frame is looking at the same body under a new netId, after a `RESET` renumbered the view.
   *
   * It exists because "the subject's id changed" and "the subject changed" are not the same fact, and the camera used
   * to act on the first while meaning the second.
   */
  private eyeRebased = false;

  /** {@link evaluate}'s output: one subject's interpolated placement, valid until the next call. */
  private evalX = 0;
  private evalZ = 0;
  private evalY = 0;
  private evalVx = 0;
  private evalVz = 0;
  private evalArchetype = -1;

  private publishStats(ui: UiState, nearRadius: number): void {
    const source = this.source?.stats;
    if (source === undefined) {
      return;
    }

    // Rewritten in place, not rebuilt. The toolbar selects `stats.layers` narrowly ON PURPOSE — zustand compares by
    // `Object.is`, so a fresh array four times a second re-rendered the whole panel at exactly the rate those narrow
    // selectors were written to avoid, including the place buttons and the layer toggles. `heatChannels` was already
    // stable for the same reason; this was the one that was not.
    const layers = this.layerStats;
    for (let i = 0; i < layers.length; i++) {
      const row = layers[i];
      row.archetype = this.archetypes.infos[i];
      row.held = this.world.archetypeStore(i).liveCount;
      row.near = this.layers[i].nearCount;
      row.far = this.layers[i].farCount;
    }

    // The layers past the archetypes are the extra ones (the shuttleport's dome): their instances belong to the row of
    // the archetype they draw, or the panel would report a drawn count that misses every port.
    for (let i = layers.length; i < this.layers.length; i++) {
      // An extra layer draws an archetype of this same schema, so its index is always one of the rows above.
      const extra = this.layers[i];
      const row = layers[extra.archetype];
      row.near += extra.nearCount;
      row.far += extra.farCount;
    }

    const js = this.frameJsSummary;
    meanAndP95(this.frameJs, this.frameJsCount, this.sortScratch, js);

    useStats.getState().publish({
      fps: this.engine.getFps(),
      frameJsMs: js[0],
      frameJsP95Ms: js[1],
      frameTotalMs: this.instrumentation.frameTimeCounter.lastSecAverage,
      drawCalls: this.instrumentation.drawCallsCounter.current,
      layers,
      heatChannels: this.heatChannels,
      attackLines: this.attacks.drawn,
      renderTime: this.clock.renderTime,
      latestTick: this.clock.latestTick,
      renderDelayMs: this.clock.renderDelayMs,
      // The ACTIVE camera's, not the map's: in eye view the map camera is still smoothing along at its own altitude,
      // and reporting that read as "you are 1 474 m up" while standing in a street.
      altitude: this.activeAltitude,
      // What Shift gives, or the swoop while riding, and the boost on it.
      gait: this.ridingNow(performance.now()) ? SpeedClass.swoop : SpeedClass.run,
      boost: this.boost,
      // What the server says that is worth at the boost. NaN before the owner block arrives, which the HUD shows as a
      // dash rather than as a figure it made up.
      gaitMps:
        this.source === null
          ? Number.NaN
          : speedForClass(
              this.ridingNow(performance.now()) ? SpeedClass.swoop : SpeedClass.run,
              this.grantedSpeeds(this.source),
              this.boost,
            ),
      groundM: this.profile.ground.heightAt(this.groundView.centerX, this.groundView.centerZ),
      feet: this.feetReport(),
      terrainBakeMs: this.terrainBakeMs,
      terrainTriangles: this.profile.stats.triangles,
      terrainNodes: this.profile.stats.nodes,
      terrainFinestM: this.profile.stats.finestM,
      terrainCapped: this.profile.stats.capped,
      nearRadius,
      source,
      realm: this.source?.realm ?? null,
      canPause: this.source?.canPause ?? false,
      canViewRealm: this.source?.canViewRealm ?? false,
      canSpectate: this.source?.canSpectate ?? false,
      ridingNetId: rideSubjectOf(this.ride),
      replication: replicationStatsOf(this.source?.debug ?? null),
      inspection: ui.selectedNetId === 0 ? null : this.inspect(ui.selectedNetId),
      held: this.world.entityCount,
      anomalies: this.world.anomalies,
      // A14. The lead is the latency expressed as a distance — the gap between the body on screen and the server's own
      // figure — and the three counters are how a prediction fault announces itself; see PredictionStats for what each means.
      prediction: this.predictor.active
        ? {
            leadM: Math.hypot(this.predictor.x - this.eyeX, this.predictor.z - this.eyeZ),
            snaps: this.predictor.snaps,
            fallbacks: this.predictor.fallbacks,
            stale: this.predictor.staleReads,
          }
        : null,
    });
  }

  private inspect(netId: number): Inspection | null {
    const location = this.world.locate(netId);
    if (location === NOT_FOUND) {
      return null;
    }

    const archetype = archetypeOf(location);
    const slot = slotOf(location);
    const store = this.world.archetypeStore(archetype);
    const fields = describeFields(store, slot);
    const label = this.archetypes.label(archetype);
    if (!store.hasPosition) {
      return {
        archetype: label,
        altitudeM: 0,
        netId,
        x: 0,
        z: 0,
        speedMps: 0,
        headingDeg: 0,
        epoch: 0,
        hasPosition: false,
        fields,
      };
    }

    const tick = this.clock.renderTick;
    evaluateSlot(store, slot, tick, this.clock.renderFrac, this.scratch, 0);
    const at = this.placement.read(store.dims, this.scratch, 0);
    return {
      archetype: label,
      netId,
      x: at.x,
      z: at.z,
      altitudeM: at.y,
      speedMps: at.groundSpeedMps(this.clock.tickPeriodMs),
      headingDeg: at.headingDeg(),
      epoch: epochAt(store, slot, tick),
      hasPosition: true,
      fields,
    };
  }
}
