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
import { CameraInput } from '../camera/camera-input';
import { EyeCamera } from '../camera/eye-camera';
import { MapCamera } from '../camera/map-camera';
import { meanAndP95, regionDue } from './frame-policy';
import { resolveArchetypes, type ArchetypeInfo, type ArchetypeView } from '../data/archetypes';
import { Placement } from '../data/placement';
import { replicationStatsOf } from '../data/replication-stats';
import type { DataSource, EventSink } from '../data/source';
import { describeFields } from '../data/swg-format';
import { AGG_GRID, SWG_SCHEMA, TICK_PERIOD_MS } from '../data/swg-schema';
import { CITIES } from '../data/world-data';
import { AttackLines } from '../render/attack-lines';
import { EntityLayer, type FrameView, type PickHit } from '../render/entity-layer';
import { FLAT_GROUND, type GroundSampler } from '../terrain/ground-sampler';
import { Ground, GroundView, SKY } from '../render/ground';
import { startTerrainBake, type TerrainBake } from '../terrain/bake-client';
import { Heightfield } from '../terrain/heightfield';
import type { TerrainBaked } from '../terrain/terrain.worker';
import { Labels } from '../render/labels';
import { styleFor } from '../render/styles';
import { extractFrustumPlanes, RenderOrigin } from '../render/view-math';
import { useChat } from '../state/chat-store';
import { useStats, type Inspection, type LayerStats } from '../state/stats-store';
import { useUi, type UiState } from '../state/ui-store';

const STATS_INTERVAL_MS = 250;
const REGION_INTERVAL_MS = 200;
const PICK_RADIUS_PX = 8;
const FRAME_SAMPLES = 128;
const ERROR_LOG_INTERVAL_MS = 5000;
/**
 * Largest device pixel ratio rendered: a 1080p laptop at 150 % would otherwise shade the full-screen ground at 2880 × 1620.
 * Babylon applies its own `limitDeviceRatio` only at construction, so the client sets the scaling itself on every resize.
 */
const MAX_DEVICE_RATIO = 1.5;

/**
 * Stands in for an aggregate grid the session does not have. A live server declares one only under `--god-region`; the
 * ground shader needs a grid's extent whatever happens, and one empty cell shows nothing rather than crashing the frame.
 */
const NO_GRID: GridSchema = { index: 0, origin: [0, 0], cell: 1, dims: [1, 1], archetypes: [] };

/** Whether two resolved schemas name the same archetypes, in the same order. */
function sameArchetypes(a: ArchetypeView, b: ArchetypeView): boolean {
  return a.count === b.count && a.infos.every((info, index) => info.name === b.infos[index].name);
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
}

/**
 * The client, outside React: owns the Babylon engine, the SDK world (store, grid, clock), the data source, the layers and
 * the camera, and runs the frame. React mounts it on a canvas and talks to it only through the UI store.
 *
 * One frame: advance the clock → apply held keys, follow the selection → move the camera → pick the render origin →
 * update the view matrices → evaluate, cull and pack every layer → attack lines, ground, labels → Babylon draws.
 */
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
  private readonly groundView = new GroundView();
  /** One per archetype of the CURRENT world's schema, rebuilt when a source brings a different one. */
  private layers: EntityLayer[];
  private readonly ground: Ground;
  /** The planet's heightfield: flat until the worker's bake lands, then the one source of ground height for everything. */
  private readonly field: Heightfield;
  private readonly terrainBake: TerrainBake;
  /** How long the bake took, reported once in the HUD's session block. */
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
  /** {@link heatChannels} as the ground shader wants it, refilled in place each frame rather than rebuilt. */
  private heatMask: boolean[] = [];

  /** Canvas size in CSS pixels, kept by the resize observer: reading layout every frame would be a forced reflow. */
  private readonly cssSize = { width: 1, height: 1 };
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
  /** Whose eyes the eye camera is in; 0 for nobody. A change is a cut, not a glide. */
  private eyeSubject = 0;
  /** The altitude of whichever camera drew the last frame, for the HUD. */
  private activeAltitude = 0;
  private selectionText = '';
  /** The netId and archetype {@link selectionText} was built for: netIds are reused, by another archetype too. */
  private selectionTextNetId = 0;
  private selectionTextArchetype = -1;
  private readonly unsubscribe: () => void;

  constructor(canvas: HTMLCanvasElement, overlay: HTMLElement, createSource: SourceFactory) {
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

    // The ground starts FLAT and gains relief about a second later, when the worker's bake lands. That is not a
    // compromise forced by the worker — the flat field is the same code path with every post at zero, which is also the
    // regression the tests lean on, so the first second of the session exercises it for free.
    this.field = new Heightfield();
    this.view.ground = this.field;
    this.eyeCamera.ground = this.field;
    this.terrainBake = startTerrainBake((baked) => {
      this.onTerrainBaked(baked);
    });
    this.ground = new Ground(this.scene, this.field);
    this.attacks = new AttackLines(this.scene);
    this.labels = new Labels(overlay);

    this.world = new WorldStore(SWG_SCHEMA);
    this.grid = new AggregateGrid(AGG_GRID);
    this.clock = new Clock({ tickPeriodMs: TICK_PERIOD_MS });
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
          useUi.getState().select(this.pickAt(x, y));
        },
        onManualMove: () => {
          if (useUi.getState().follow) {
            useUi.getState().setFollow(false);
          }
        },
      },
      this.eyeCamera,
      () => useUi.getState().cameraMode === 'eye' && useUi.getState().selectedNetId !== 0,
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

    this.engine.stopRenderLoop();
    this.resizeObserver.disconnect();
    this.ratioQuery?.removeEventListener('change', this.onRatioChange);
    this.ratioQuery = null;
    this.unsubscribe();
    this.input.dispose();
    this.source?.dispose();
    this.source = null;
    this.labels.dispose();
    this.attacks.dispose();
    for (const layer of this.layers) {
      layer.dispose();
    }

    this.ground.dispose();
    this.terrainBake.terminate();
    this.scene.dispose();
    this.engine.dispose();
  }

  /**
   * Adopts the baked heightfield.
   *
   * Copied into the field's own array rather than swapped in, because the GPU texture was created over that array and a
   * swap would leave it pointing at the flat one. 16.8 MB is a few milliseconds, once.
   */
  private onTerrainBaked(baked: TerrainBaked): void {
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
    // The per-level error the worker measured: what the pixel tolerance is spent against. Measured there because it is a
    // pass over 4.19 M posts per level and the main thread has a 2 ms frame.
    this.ground.refreshTerrain(baked.nodeError, baked.nodeMinY, baked.nodeMaxY);
  }

  private resize(): void {
    this.sizeDirty = false;
    this.cssSize.width = Math.max(1, this.canvas.clientWidth);
    this.cssSize.height = Math.max(1, this.canvas.clientHeight);
    // Resizes the canvas too.
    this.engine.setHardwareScalingLevel(1 / Math.min(MAX_DEVICE_RATIO, window.devicePixelRatio || 1));
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
    this.clock = new Clock({ tickPeriodMs: TICK_PERIOD_MS });

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
    this.heatChannels = grid.schema.archetypes.map((index) => ({
      name: this.archetypes.name(index),
      label: this.archetypes.label(index),
    }));
    this.heatMask = this.heatChannels.map(() => false);
    this.applyUi(useUi.getState());
  }

  /** One layer per archetype of the current schema, each with the style its NAME selects. */
  private buildLayers(): EntityLayer[] {
    return this.archetypes.infos.map((info, index) => new EntityLayer(index, styleFor(info.name), this.scene));
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

    this.applyUi(state);
  }

  private applyUi(state: UiState): void {
    // By NAME, and visible unless the user turned it off: a schema may hold an archetype the UI has never named.
    this.layers.forEach((layer, index) => {
      layer.visible = state.layers[this.archetypes.name(index)] ?? true;
    });
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

    // Eye mode rides the selection; without one there is nothing to ride, so it falls back to the map rather than
    // stranding the viewer at the origin (CLI3D-10).
    const inEye = ui.cameraMode === 'eye' && hasSelection;
    if (inEye) {
      if (this.eyeSubject !== ui.selectedNetId) {
        this.eyeSubject = ui.selectedNetId;
        this.eyeCamera.jumpTo(this.selectedX, this.selectedY, this.selectedZ, this.selectedHeading);
      } else {
        this.eyeCamera.follow(this.selectedX, this.selectedY, this.selectedZ);
      }

      this.eyeCamera.update(dt);
    } else {
      this.eyeSubject = 0;
    }

    // The map camera orbits the ground under its target, so it needs the terrain height there BEFORE it recomputes its eye.
    this.mapCamera.groundY = this.field.heightAt(this.mapCamera.targetX, this.mapCamera.targetZ);
    this.mapCamera.update(dt);
    const cam = inEye ? this.eyeCamera : this.mapCamera;
    this.activeAltitude = cam.altitude;
    this.origin.follow(cam.targetX, cam.targetZ);
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

    this.camera.position.set(view.eyeX, view.eyeY, view.eyeZ);
    this.camera.rotation.set(cam.pitch, cam.yaw, 0);
    // EVERY field of the active camera's contract, `fov` included. It used to be set once in the constructor from the map
    // camera, so eye mode rendered at the map's 0.8 rad while `pixelsPerMetre` above was computed from the eye's 1.1 — a
    // 1.45× disagreement that put every LOD threshold 31 % low and would never throw, only look wrong.
    this.camera.fov = cam.fov;
    this.camera.minZ = cam.nearPlane;
    this.camera.maxZ = cam.farPlane;
    this.scene.updateTransformMatrix(true);
    const matrix = this.scene.getTransformMatrix().m;
    extractFrustumPlanes(matrix, view.planes);

    for (const layer of this.layers) {
      layer.update(view);
    }

    this.attacks.update(view);
    this.sendRegion(now, ui.viewRadius, cam.targetX, cam.targetZ);
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
    g.pixelsPerMetre = view.pixelsPerMetre;
    g.pixelTolerance = ui.terrainPixelError;
    g.nearRadius = nearRadius;
    g.heatMask = this.heatMask;
    g.showGrid = ui.showGrid;
    g.showDebug = ui.showDebug;
    g.hasSelection = hasSelection;
    g.selectionX = this.selectedX;
    g.selectionZ = this.selectedZ;
    this.ground.update(g, this.grid, this.source?.debug ?? null);

    if (!ui.showLabels) {
      this.labels.hideAll();
    } else {
      this.labels.update(matrix, view.originX, view.originZ, this.cssSize.width, this.cssSize.height, this.field);
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
  private sendRegion(now: number, radius: number, x: number, z: number): void {
    if (
      this.source === null ||
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

  /** The entity drawn nearest a click, in CSS pixels relative to the canvas; 0 when none is within reach. */
  private pickAt(x: number, y: number): number {
    const matrix = this.scene.getTransformMatrix().m;
    const best: PickHit = { archetype: -1, netId: 0, pixels: Infinity, depth: Infinity };
    for (const layer of this.layers) {
      layer.pick(matrix, this.view, x, y, PICK_RADIUS_PX, best);
    }

    return best.netId;
  }

  /** Evaluates the selection's position into {@link selectedX}/{@link selectedZ}; false when it has none. */
  private locateSelected(netId: number): boolean {
    const location = this.world.locate(netId);
    if (location === NOT_FOUND) {
      return false;
    }

    const archetype = archetypeOf(location);
    const store = this.world.archetypeStore(archetype);
    if (!store.hasPosition) {
      return false;
    }

    evaluateSlot(store, slotOf(location), this.clock.renderTick, this.clock.renderFrac, this.scratch, 0);
    const selected = this.placement.read(store.dims, this.scratch, 0);
    this.selectedX = selected.x;
    this.selectedZ = selected.z;
    // Altitude and heading, for the eye camera (CLI3D-10). A still entity keeps the heading it last had, the way the
    // renderer's own meshes do: `headingOf`'s fallback, not a snap to north.
    this.selectedY = selected.y + this.field.heightAt(selected.x, selected.z);
    this.selectedHeading = headingOf(selected.vx, selected.vz, this.selectedHeading);
    if (this.selectionTextNetId !== netId || this.selectionTextArchetype !== archetype) {
      this.selectionTextNetId = netId;
      this.selectionTextArchetype = archetype;
      this.selectionText = `${this.archetypes.label(archetype)} #${netId}`;
    }

    return true;
  }

  private publishStats(ui: UiState, nearRadius: number): void {
    const source = this.source?.stats;
    if (source === undefined) {
      return;
    }

    const layers: LayerStats[] = this.layers.map((layer, index) => ({
      archetype: this.archetypes.infos[index],
      held: this.world.archetypeStore(index).liveCount,
      near: layer.nearCount,
      far: layer.farCount,
    }));

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
      groundM: this.field.heightAt(this.groundView.centerX, this.groundView.centerZ),
      terrainBakeMs: this.terrainBakeMs,
      terrainTriangles: this.ground.terrainTriangles,
      terrainNodes: this.ground.terrainNodes,
      terrainFinestM: this.ground.terrainFinestM,
      terrainCapped: this.ground.terrainCapped,
      nearRadius,
      source,
      canPause: this.source?.canPause ?? false,
      replication: replicationStatsOf(this.source?.debug ?? null),
      inspection: ui.selectedNetId === 0 ? null : this.inspect(ui.selectedNetId),
      held: this.world.entityCount,
      anomalies: this.world.anomalies,
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
