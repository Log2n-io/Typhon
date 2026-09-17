import { FreeCamera } from '@babylonjs/core/Cameras/freeCamera';
import { Engine } from '@babylonjs/core/Engines/engine';
import { SceneInstrumentation } from '@babylonjs/core/Instrumentation/sceneInstrumentation';
import { Color4 } from '@babylonjs/core/Maths/math.color';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { Scene } from '@babylonjs/core/scene';
import {
  AggregateGrid,
  archetypeOf,
  Clock,
  epochAt,
  evaluateSlot,
  MOTION_STRIDE,
  NOT_FOUND,
  slotOf,
  WorldStore,
} from '@typhondb/client';
import { CameraInput } from '../camera/camera-input';
import { MapCamera } from '../camera/map-camera';
import { meanAndP95, regionDue } from './frame-policy';
import type { DataSource, EventSink } from '../data/source';
import { describeFields } from '../data/swg-format';
import { AGG_GRID, ARCHETYPE_LABELS, SWG_SCHEMA, TICK_PERIOD_MS } from '../data/swg-schema';
import { CITIES } from '../data/world-data';
import { AttackLines } from '../render/attack-lines';
import { EntityLayer, type FrameView, type PickHit } from '../render/entity-layer';
import { Ground, GroundView, SKY } from '../render/ground';
import { Labels } from '../render/labels';
import { LAYER_STYLES } from '../render/styles';
import { extractFrustumPlanes, RenderOrigin } from '../render/view-math';
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
  private readonly input: CameraInput;
  private readonly origin = new RenderOrigin();
  private readonly view = new MutableFrameView();
  private readonly groundView = new GroundView();
  private readonly layers: EntityLayer[];
  private readonly ground: Ground;
  private readonly attacks: AttackLines;
  private readonly labels: Labels;
  private readonly resizeObserver: ResizeObserver;

  private world: WorldStore;
  private grid: AggregateGrid;
  private clock: Clock;
  private source: DataSource | null = null;

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
  private readonly scratch = new Float64Array(MOTION_STRIDE);
  private readonly sortScratch = new Float64Array(FRAME_SAMPLES);
  private readonly frameJsSummary = new Float64Array(2);
  /** The selection's evaluated planet position this frame, valid when {@link locateSelected} returned true. */
  private selectedX = 0;
  private selectedZ = 0;
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

    this.ground = new Ground(this.scene);
    this.layers = LAYER_STYLES.map((style, archetype) => new EntityLayer(archetype, style, this.scene));
    this.attacks = new AttackLines(this.scene);
    this.labels = new Labels(overlay);

    this.world = new WorldStore(SWG_SCHEMA);
    this.grid = new AggregateGrid(AGG_GRID);
    this.clock = new Clock({ tickPeriodMs: TICK_PERIOD_MS });

    const home = CITIES[0];
    this.mapCamera.jumpTo(home.x, home.z, 1800);

    this.input = new CameraInput(canvas, this.mapCamera, this.cssSize, {
      onClick: (x, y) => {
        useUi.getState().select(this.pickAt(x, y));
      },
      onManualMove: () => {
        if (useUi.getState().follow) {
          useUi.getState().setFollow(false);
        }
      },
    });

    // Observer callbacks run after the frame's draw and before its paint: resizing the canvas there would paint it blank.
    this.resizeObserver = new ResizeObserver(() => {
      this.sizeDirty = true;
    });

    this.unsubscribe = useUi.subscribe((state, previous) => {
      this.onUiChange(state, previous);
    });
  }

  start(): void {
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

  dispose(): void {
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
    this.scene.dispose();
    this.engine.dispose();
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
    this.selectionTextNetId = 0;
    this.world = new WorldStore(SWG_SCHEMA);
    this.grid = new AggregateGrid(AGG_GRID);
    this.clock = new Clock({ tickPeriodMs: TICK_PERIOD_MS });
    for (const layer of this.layers) {
      layer.bind(this.world.archetypeStore(layer.archetype));
    }

    this.attacks.bind(this.world);
    this.source = this.createSource(
      { world: this.world, grid: this.grid, clock: this.clock, events: this.attacks },
      state,
    );
    this.source.start();
    this.source.setPaused(state.paused);
    this.regionRadius = Number.NaN;
    this.lastRegionMs = Number.NEGATIVE_INFINITY;
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
    this.layers.forEach((layer, archetype) => {
      layer.visible = state.layers[archetype];
    });
    this.attacks.visible = state.showAttacks;
  }

  private frame(): void {
    const now = performance.now();
    const dt = Math.min(0.1, (now - this.lastFrameMs) / 1000);
    this.lastFrameMs = now;
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

    this.mapCamera.update(dt);
    const cam = this.mapCamera;
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
    this.camera.minZ = cam.nearPlane;
    this.camera.maxZ = cam.farPlane;
    this.scene.updateTransformMatrix(true);
    const matrix = this.scene.getTransformMatrix().m;
    extractFrustumPlanes(matrix, view.planes);

    for (const layer of this.layers) {
      layer.update(view);
    }

    this.attacks.update(view);
    this.sendRegion(now, ui.viewRadius);
    const serverRadius = this.source?.stats.server?.effectiveRadius;
    const nearRadius = serverRadius !== undefined && Number.isFinite(serverRadius) ? serverRadius : ui.viewRadius;

    const g = this.groundView;
    g.originX = view.originX;
    g.originZ = view.originZ;
    g.eyeX = view.eyeX;
    g.eyeY = view.eyeY;
    g.eyeZ = view.eyeZ;
    g.viewCenterX = this.regionX;
    g.viewCenterZ = this.regionZ;
    g.nearRadius = nearRadius;
    g.heatMask = ui.heatmap;
    g.showGrid = ui.showGrid;
    g.hasSelection = hasSelection;
    g.selectionX = this.selectedX;
    g.selectionZ = this.selectedZ;
    this.ground.update(g, this.grid);

    if (!ui.showLabels) {
      this.labels.hideAll();
    } else {
      this.labels.update(matrix, view.originX, view.originZ, this.cssSize.width, this.cssSize.height);
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

  /** The god camera's region follows the look-at point, sent when it moved 5 % of the radius, at most five times a second. */
  private sendRegion(now: number, radius: number): void {
    const cam = this.mapCamera;
    const x = cam.targetX;
    const z = cam.targetZ;
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
    this.selectedX = this.scratch[0];
    this.selectedZ = this.scratch[1];
    if (this.selectionTextNetId !== netId || this.selectionTextArchetype !== archetype) {
      this.selectionTextNetId = netId;
      this.selectionTextArchetype = archetype;
      this.selectionText = `${ARCHETYPE_LABELS[archetype] ?? '?'} #${netId}`;
    }

    return true;
  }

  private publishStats(ui: UiState, nearRadius: number): void {
    const source = this.source?.stats;
    if (source === undefined) {
      return;
    }

    const layers: LayerStats[] = this.layers.map((layer, archetype) => ({
      held: this.world.archetypeStore(archetype).liveCount,
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
      attackLines: this.attacks.drawn,
      renderTime: this.clock.renderTime,
      latestTick: this.clock.latestTick,
      renderDelayMs: this.clock.renderDelayMs,
      altitude: this.mapCamera.altitude,
      nearRadius,
      source,
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
    const fields = describeFields(archetype, store, slot);
    if (!store.hasPosition) {
      return { archetype, netId, x: 0, z: 0, speedMps: 0, headingDeg: 0, epoch: 0, hasPosition: false, fields };
    }

    const tick = this.clock.renderTick;
    evaluateSlot(store, slot, tick, this.clock.renderFrac, this.scratch, 0);
    const vx = this.scratch[2];
    const vz = this.scratch[3];
    return {
      archetype,
      netId,
      x: this.scratch[0],
      z: this.scratch[1],
      speedMps: (Math.hypot(vx, vz) * 1000) / this.clock.tickPeriodMs,
      headingDeg: ((Math.atan2(vx, vz) * 180) / Math.PI + 360) % 360,
      epoch: epochAt(store, slot, tick),
      hasPosition: true,
      fields,
    };
  }
}
