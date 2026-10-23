import type { AbstractMesh } from '@babylonjs/core/Meshes/abstractMesh';
import type { TransformNode } from '@babylonjs/core/Meshes/transformNode';
import { placement } from '../assets/asset-frame';
import type { AssetLibrary, Instance } from '../assets/asset-library';
import { assetForStructure, structureYaw } from '../assets/structure-assets';
import type { LodDesc } from '../assets/descriptor';
import type { AssetSet } from '../data/server-config';
import { NO_LEVEL, levelForError, lodLevelOf } from '../render/asset-lod';
import type { SceneLighting } from '../render/scene-lighting';

/**
 * The structures the server placed, drawn as published assets.
 *
 * <b>This is the half of the renderer that the asset pipeline is for.</b> `render/entity-layer.ts` evaluates thousands
 * of positions a frame and packs them into one thin-instance buffer per archetype — the right shape for a crowd whose
 * every member is moving. A building is the opposite: it never moves, there are tens of them near a player rather than
 * thousands, and what matters is that it looks like a building. So a structure in range gets a real instance in the
 * scene graph, and the packer is told to skip it.
 *
 * <b>Instances are created and destroyed by RANGE, not by frustum.</b> Turning on the spot would otherwise unload the
 * street behind you and reload it a second later — a GLB parse and a GPU upload each time, for geometry that was
 * already resident. What a session holds at all is already bounded by its interest radius, so this is a second, smaller
 * bound inside it.
 *
 * <b>Loading is budgeted per frame</b> because `LoadAssetContainerAsync` parses and uploads on the main thread. The
 * first frame in a city would otherwise try to start forty of them at once, and the frame they all land on is a visible
 * stall rather than a gradual fill.
 *
 * <b>One detail level is drawn, and this layer is what chooses it.</b> A published GLB carries every level as nodes of
 * the one file, and they arrive ENABLED — the descriptor's note that `KHR_node_visibility` hides the coarse ones does
 * not hold here. Measured on catalogue `f131e3e086ab` before this: 52 placed structures drew 435 705 triangles, of
 * which 172 937 — two fifths — were coarse levels z-fighting inside the fine one. The level is picked from the error
 * the publisher measured, in screen pixels; see `render/asset-lod.ts`.
 */

/**
 * What the app reads off one structure for this layer: identity, where it stands, and what it is.
 *
 * <b>Pooled and rewritten in place.</b> The app refills this every frame for every structure in range, and the range
 * now holds about 190 of them — a fresh literal each was 190 objects a frame, 11 400 a second, against the comment on
 * the array that says it must not allocate. The layer reads a record inside the frame it was handed and keeps only
 * the few fields it needs.
 */
export interface StructureSample {
  netId: number;
  x: number;
  /** Ground altitude under it — a building's origin sits ON the ground, its foundation skirt below. */
  y: number;
  z: number;
  kind: number;
  /** The replicated quarter turn; see `structureYaw`. */
  yaw: number;
  /** The replicated asset index; see `assetForStructure`. */
  asset: number;
}

/**
 * What a structure may lose to its own level of detail, in screen pixels.
 *
 * <b>Named and explicit, because it is load-bearing and it is NOT the terrain's figure.</b> The terrain's tolerance is
 * the viewer's, on a slider, and defaults to 8 px: a dune's silhouette may drift that far because nothing else in the
 * scene tells you it has. A building's does — its straight edges meet its neighbour's, and a doorway two pixels out of
 * place reads as a modelling fault rather than as distance. 1.5 px is the point at which the published LOD1 of a
 * `tat_house_large` (0.10 m) takes over at about 66 m and its LOD2 (0.25 m) at about 166 m, both of which put the swap
 * well outside the range at which a viewer is looking at the building rather than past it.
 */
export const STRUCTURE_PIXEL_ERROR = 1.5;

/** Where the camera is and how big a metre is on screen, which is all the level choice needs. */
export interface AssetView {
  /** Planet metres — the same frame the samples are in, NOT render space. */
  readonly eyeX: number;
  readonly eyeY: number;
  readonly eyeZ: number;
  /** `renderHeight / (2 tan(fovY / 2))`, from the ACTIVE camera. */
  readonly pixelsPerMetre: number;
}

/** A placed instance and the structure it stands for. */
interface Standing {
  readonly instance: Instance;
  readonly asset: string;
  readonly x: number;
  /** Ground altitude. Mutable: it is re-sampled as the height field loads; see `update`. */
  y: number;
  readonly z: number;
  readonly yaw: number;
  /**
   * This placement's meshes grouped by detail level, finest first.
   *
   * Empty for an asset that publishes one level, which is most of them — a terminal has no LODs and must not pay a
   * per-frame pass to be told so.
   */
  readonly levels: readonly (readonly AbstractMesh[])[];
  /** Each level's published error in metres, in the same order. */
  readonly errors: readonly number[];
  /** The level currently enabled, or {@link NO_LEVEL} before the first choice. */
  level: number;
}

/**
 * Sorts a placement's meshes into detail levels, and reads each level's published error.
 *
 * <b>By the mesh's own name, not by the descriptor's node names.</b> The published GLBs carry EMPTY transform nodes
 * sharing the level names (the exporter's `regroup_lods` residue, see {@link LodDesc}), so matching the descriptor
 * against the scene graph finds decoys. The suffix on the drawn mesh is unambiguous, and a mesh with no vertices is
 * skipped whatever it is called.
 *
 * @param meshes The placement's meshes.
 * @param lods What the descriptor publishes, or `null`.
 * @param id The asset, for the warnings — every one of them names a publishing fault somebody has to fix.
 * @returns The meshes by level and each level's error in metres; both empty when there is only one level.
 */
// Exported for its own test: it is the one place a publishing fault turns into an invisible or a tripled building.
export function groupLevels(
  meshes: readonly AbstractMesh[],
  lods: readonly LodDesc[] | null,
  id = '?',
): { levels: readonly (readonly AbstractMesh[])[]; errors: readonly number[] } {
  const drawn = meshes.filter((mesh) => mesh.getTotalVertices() > 0);
  if (lods === null || lods.length < 2) {
    // <b>The GLB says there are levels and the descriptor does not.</b> Silence here is the 40 % of wasted triangles
    // this file was written to end, reinstated by a publishing mismatch rather than by a loader — so it is said out
    // loud. Drawing every level is still the right fallback: visibly heavy beats invisible.
    if (drawn.some((mesh) => lodLevelOf(mesh.name) > 0)) {
      console.warn(`assets: ${id} ships LOD meshes its descriptor does not declare; every level will draw`);
    }

    return NO_LEVELS;
  }

  // <b>Published errors, checked once, because `levelForError` cannot check them per frame and would not notice.</b>
  // A missing error reads as zero, and a zero error at a coarse level says "this mesh is perfect" — so the coarsest
  // level wins at EVERY distance and the doorway you are standing in is a 721-triangle box. That is the worst failure
  // this design has, it is one `??` away, and the catalogue window where `lods` exists without `error_m` is real.
  const errors = lods.map((lod) => lod.error_m);
  for (const [at, error] of errors.entries()) {
    const previous = errors[at - 1];
    if (error === undefined) {
      console.warn(`assets: ${id} declares LOD${String(at)} with no error_m; every level will draw`);
      return NO_LEVELS;
    }

    if (previous !== undefined && error < previous) {
      console.warn(
        `assets: ${id} LOD${String(at)} claims a smaller error than the level above it; every level will draw`,
      );
      return NO_LEVELS;
    }
  }

  const levels: AbstractMesh[][] = lods.map(() => []);
  for (const mesh of drawn) {
    const level = lodLevelOf(mesh.name);
    if (level >= levels.length) {
      // A level past the end of the declared ladder. Drawn with the finest, which is the safe way to be wrong — an
      // extra mesh is visible, an orphan level is a building that vanishes at a distance nobody tested — but it is a
      // publishing fault and it z-fights, so it is not allowed to be quiet.
      console.warn(`assets: ${id} ships ${mesh.name} past its ${String(lods.length)} declared levels; drawn with LOD0`);
      levels[0].push(mesh);
      continue;
    }

    levels[level].push(mesh);
  }

  // <b>A level with no mesh must not become an invisible building.</b> Collapsing to one level draws every mesh,
  // which is what this did before levels existed — visibly wrong rather than absent.
  if (levels.some((meshesAt) => meshesAt.length === 0)) {
    console.warn(`assets: ${id} declares a detail level with no mesh; every level will draw`);
    return NO_LEVELS;
  }

  return { levels, errors: errors as readonly number[] };
}

/**
 * How many placements of an ALREADY RESIDENT asset may start in one frame.
 *
 * Eight rather than two because the work is a node-graph clone, not a GLB parse, and the thing it is racing is a town
 * of boxes turning into buildings after a view reset. Measured shape, not a guess at a cost: 190 placements over six
 * distinct assets means the parse budget is spent in the first three frames and everything after it is warm.
 */
const WARM_PLACEMENTS_PER_FRAME = 8;

/**
 * Every mesh of a placement, its child assets' included.
 *
 * @param instance The placed instance.
 * @param into Reused across the recursion.
 * @returns The meshes.
 */
function allMeshes(instance: Instance, into: AbstractMesh[] = []): AbstractMesh[] {
  for (const mesh of instance.meshes) {
    into.push(mesh);
  }

  for (const child of instance.children) {
    allMeshes(child, into);
  }

  return into;
}

/** "This asset has no levels to choose between", shared so the fallbacks cannot drift apart. */
const NO_LEVELS = { levels: [], errors: [] };

export class StaticAssets {
  private readonly library: AssetLibrary;
  /** Marks each placement as a shadow caster, or `null` on a mount without lighting (the tests'). */
  private readonly lighting: SceneLighting | null;
  private readonly set: AssetSet;
  private readonly root: TransformNode;
  private readonly budget: number;
  private readonly warmBudget: number;
  /** Live instances by netId, with what each one stands for so a renumbered id is caught. */
  private readonly placed = new Map<number, Standing>();
  /** Loads in flight, so a second frame does not start the same one again. */
  private readonly loading = new Set<number>();
  /** What this frame's samples asked for, reused so a frame allocates nothing. */
  private readonly wanted = new Set<number>();
  /** What is on screen, handed to the packer as the set it must not draw. */
  private readonly drawn = new Set<number>();
  /**
   * The camera the last frame was drawn with, so a placement that lands mid-frame can choose its level at once.
   *
   * <b>Without it a new building draws EVERY level for one frame.</b> A GLB lands between frames with `NO_LEVEL`, and
   * nothing has told it otherwise until the next `update` — so the first frame of every placement is the defect this
   * whole file exists to fix, in miniature. At a budget of two placements a frame, crossing a town means one or two of
   * them doing it every frame for several seconds.
   */
  private view: AssetView | null = null;
  /** Assets whose container has been parsed at least once, so a further placement of one is cheap. */
  private readonly resident = new Set<string>();
  /** Whether this frame left work undone, which is the only thing that makes the next one sort by distance. */
  private wantsMore = false;
  private wantsMoreLast = false;
  /** Reused by {@link byDistance}: sorting must not allocate an array a frame. */
  private readonly ordering: StructureSample[] = [];
  private disposed = false;

  constructor(
    library: AssetLibrary,
    root: TransformNode,
    set: AssetSet,
    budgetPerFrame = 2,
    lighting: SceneLighting | null = null,
    warmBudgetPerFrame = WARM_PLACEMENTS_PER_FRAME,
  ) {
    this.set = set;
    this.library = library;
    this.root = root;
    this.budget = budgetPerFrame;
    this.warmBudget = warmBudgetPerFrame;
    this.lighting = lighting;
  }

  /**
   * This frame's samples, nearest first.
   *
   * Sorted in a reused array rather than through `toSorted`, and only on a frame that has a backlog.
   *
   * @param samples What the app handed in.
   * @param count How many of them are live.
   * @param view The camera.
   * @returns The same samples, ordered.
   */
  private byDistance(samples: readonly StructureSample[], count: number, view: AssetView): readonly StructureSample[] {
    const ordering = this.ordering;
    ordering.length = 0;
    for (let n = 0; n < count; n++) {
      ordering.push(samples[n]);
    }

    ordering.sort((a, b) => {
      const ax = a.x - view.eyeX;
      const az = a.z - view.eyeZ;
      const bx = b.x - view.eyeX;
      const bz = b.z - view.eyeZ;
      return ax * ax + az * az - (bx * bx + bz * bz);
    });
    return ordering;
  }

  /** How many structures are drawn as assets, for the HUD and for a test. */
  get count(): number {
    return this.placed.size;
  }

  /** How many are being loaded right now. */
  get pending(): number {
    return this.loading.size;
  }

  /**
   * Brings the placed set in line with what is in range, and draws each placement at one detail level.
   *
   * @param samples The pooled structure records; only the first `count` are this frame's.
   * @param count How many of `samples` are live. The array keeps its capacity, so its length is not the count.
   * @param view The camera, for the level choice. Omitted by a caller with no camera — a test — which leaves every
   * placement at its finest level, the behaviour before levels existed.
   * @returns The netIds that have an instance, which the packer must skip.
   */
  update(samples: readonly StructureSample[], count: number, view: AssetView | null = null): ReadonlySet<number> {
    if (this.disposed) {
      return this.drawn;
    }

    this.view = view;

    this.wanted.clear();
    let cold = 0;
    let warm = 0;
    this.wantsMore = false;
    // <b>Nearest first, whenever the budget cannot take them all.</b> `samples` arrives in the store's slot order,
    // which is the order the server happened to create the entities in — so a budgeted frame could spend itself on a
    // building 1 200 m away while the one the player is standing inside was still a box. Sorting only matters while
    // there is a backlog, which is the first second of a session and the frames after a view reset; the rest of the
    // time this is skipped and the array is read as it came.
    const ordered = this.wantsMoreLast && view !== null ? this.byDistance(samples, count, view) : samples;
    this.wantsMoreLast = false;
    for (let n = 0; n < count; n++) {
      const sample = ordered[n];
      const id = assetForStructure(this.set, sample.asset);
      if (id === null) {
        continue;
      }

      this.wanted.add(sample.netId);
      const standing = this.placed.get(sample.netId);
      if (standing !== undefined) {
        // <b>A netId is not an identity across a view RESET.</b> The server renumbers every entity on one, so an id
        // can come back naming a different structure — and an instance kept on the id alone would stand as the wrong
        // building at the wrong place, while suppressing the new entity's own shape through `drawn`.
        if (
          standing.asset === id &&
          standing.x === sample.x &&
          standing.z === sample.z &&
          standing.yaw === sample.yaw
        ) {
          // <b>Altitude is NOT part of the identity, and it is not allowed to go stale either.</b> It is sampled from
          // a height field that loads after the placement does, so a building placed early stands at a fallback
          // altitude — sunk into the dune or hovering over it — for the rest of the session. Re-placing on it would
          // re-parse a GLB every time the ground resolved a centimetre differently; moving the root is free and is
          // what a changed ground actually means. It also keeps the level's distance honest, which now reads this.
          if (standing.y !== sample.y) {
            standing.y = sample.y;
            standing.instance.root.position.y = sample.y;
          }

          if (view !== null) {
            this.chooseLevel(standing, view);
          }

          continue;
        }

        standing.instance.dispose();
        this.placed.delete(sample.netId);
        this.drawn.delete(sample.netId);
      }

      if (this.loading.has(sample.netId) || this.library.hasFailed(id)) {
        continue;
      }

      // <b>The budget pays for the PARSE, not for the placement.</b> `LoadAssetContainerAsync` parses and uploads a
      // GLB on the main thread, which is what two-a-frame was protecting the frame from; placing a second copy of an
      // asset already resident is `instantiateModelsToScene` over a node graph that is already in memory. Charging
      // both at the same rate is what made a town take 95 frames to appear — and the range now holds 190 placements
      // against the 52 that figure was chosen for, so it would have been 1.6 seconds of boxes after every view reset.
      if (this.resident.has(id)) {
        if (warm >= this.warmBudget) {
          this.wantsMore = true;
          continue;
        }

        warm++;
      } else {
        if (cold >= this.budget) {
          this.wantsMore = true;
          continue;
        }

        cold++;
      }

      this.begin(sample, id);
    }

    this.wantsMoreLast = this.wantsMore;

    // Everything that was in range and is not any more. A load in flight for one of them is left alone: it is cheaper to
    // let it land and dispose it on the next frame than to carry a cancellation through the loader.
    for (const [netId, standing] of this.placed) {
      if (!this.wanted.has(netId)) {
        standing.instance.dispose();
        this.placed.delete(netId);
        this.drawn.delete(netId);
      }
    }

    return this.drawn;
  }

  /**
   * Enables the one level this placement is worth, when that is not the level it already has.
   *
   * The distance is to the placement's ORIGIN rather than to the nearest point of its bounds. A building is tens of
   * metres across, so the two differ most when the camera is inside the building — where the origin's distance is the
   * smaller of the two and therefore the conservative one, asking for more detail rather than less.
   *
   * @param standing The placement.
   * @param view The camera.
   */
  private chooseLevel(standing: Standing, view: AssetView): void {
    if (standing.levels.length < 2) {
      return;
    }

    const dx = standing.x - view.eyeX;
    const dy = standing.y - view.eyeY;
    const dz = standing.z - view.eyeZ;
    const level = levelForError(
      standing.errors,
      view.pixelsPerMetre,
      Math.sqrt(dx * dx + dy * dy + dz * dz),
      STRUCTURE_PIXEL_ERROR,
      standing.level,
    );
    if (level === standing.level) {
      return;
    }

    standing.level = level;
    // Index loops: `entries()` allocates an iterator, and this runs in the render path.
    for (let at = 0; at < standing.levels.length; at++) {
      const meshes = standing.levels[at];
      const on = at === level;
      for (let m = 0; m < meshes.length; m++) {
        meshes[m].setEnabled(on);
      }
    }
  }

  dispose(): void {
    this.disposed = true;
    for (const standing of this.placed.values()) {
      standing.instance.dispose();
    }

    this.placed.clear();
    this.loading.clear();
    this.drawn.clear();
  }

  private begin(sample: StructureSample, id: string): void {
    if (this.library.catalogue.assets[id] === undefined) {
      // A catalogue that does not publish it: the renderer's own shape stands, which is what happens for every kind
      // that has no asset at all. Not counted as pending, or the budget would be spent on it every frame.
      return;
    }

    const netId = sample.netId;
    this.loading.add(netId);
    const pose = placement([sample.x, sample.y, sample.z], structureYaw(sample.yaw));
    this.library
      .instantiate(id, pose, this.root, `${id}#${String(netId)}`)
      .then((instance) => {
        this.loading.delete(netId);
        // It may have left range, or the whole layer may be gone, while the GLB was parsing.
        if (this.disposed || !this.wanted.has(netId)) {
          instance.dispose();
          return;
        }

        // Marked where it is PLACED, because that is the mesh the shadow map can draw: the container's own meshes
        // are never in the scene. Nothing has to unmark it — a placement that leaves range is disposed, and the map's
        // predicate reads the scene.
        this.resident.add(id);
        // <b>Children included.</b> A child asset is instantiated from its own container and parented under this
        // one, so `instance.meshes` does not contain it — the shuttleport's ticket terminal stood in full sunlight
        // casting nothing, which reads as the terminal floating rather than as a missing shadow.
        this.lighting?.castShadows(allMeshes(instance));
        // Resolved: `load` awaits the descriptor before the GLB, so a placement that exists has one. Said out loud
        // because the alternative is `groupLevels` quietly deciding this asset has one level and every level drawing.
        const descriptor = this.library.described(id);
        if (descriptor === null) {
          console.warn(`assets: ${id} placed with no descriptor in hand; every detail level will draw`);
        } else if (descriptor.kind === 'interior') {
          this.lighting?.lampShadows(instance.lights, instance.meshes);
        }

        // Levels are the PARENT's: a child is a separate asset with its own descriptor and its own ladder, and
        // nothing here chooses one for it. No published child declares levels; say so if one ever does, because it
        // would silently draw all of them — the defect this file exists to fix, one level down.
        for (const child of instance.children) {
          if ((this.library.described(child.id)?.render.lods?.length ?? 0) > 1) {
            console.warn(`assets: ${id} child ${child.id} declares detail levels, which nothing chooses between`);
          }
        }

        const { levels, errors } = groupLevels(instance.meshes, descriptor?.render.lods ?? null, id);
        const standing = {
          instance,
          asset: id,
          x: pose.pos[0],
          y: pose.pos[1],
          z: pose.pos[2],
          yaw: sample.yaw,
          levels,
          errors,
          level: NO_LEVEL,
        };
        // Before it is ever drawn, not on the next frame: see `view`.
        if (this.view !== null) {
          this.chooseLevel(standing, this.view);
        }

        this.placed.set(netId, standing);
        this.drawn.add(netId);
      })
      .catch((error: unknown) => {
        this.loading.delete(netId);
        // One asset failing is not a reason for the street to stop drawing: the renderer's own shape stands in its
        // place, and the id is named so the failure is diagnosable rather than merely visible.
        console.error(`assets: ${id} not placed`, error);
      });
  }
}
