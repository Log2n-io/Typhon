import type { AnimationGroup } from '@babylonjs/core/Animations/animationGroup';
import type { AssetContainer } from '@babylonjs/core/assetContainer';
import { Light } from '@babylonjs/core/Lights/light';
import { LoadAssetContainerAsync } from '@babylonjs/core/Loading/sceneLoader';
import type { GLTFLoaderAnimationStartMode } from '@babylonjs/loaders/glTF/glTFFileLoader.pure';
import { Quaternion, Vector3 } from '@babylonjs/core/Maths/math.vector';
import type { AbstractMesh } from '@babylonjs/core/Meshes/abstractMesh';
import { TransformNode } from '@babylonjs/core/Meshes/transformNode';
import type { Scene } from '@babylonjs/core/scene';
import { poseToWorld, type Pose } from './asset-frame';
import type { Catalogue, Descriptor } from './descriptor';

/**
 * `GLTFLoaderAnimationStartMode.NONE`, spelled as its value so the glTF loader is not pulled into the boot bundle.
 *
 * The enum lives in `glTFFileLoader.pure`, the module that also carries the loader class, and `glTFFileLoader.types`
 * exports nothing at run time — so importing the enum as a VALUE would make the loader eager and undo the lazy
 * registration `asset-mount.ts` chose deliberately. The type import above is erased, and
 * `asset-animation-start.test.ts` asserts this number against the real enum, which a test may import freely.
 */
const ANIMATION_START_NONE = 0 as GLTFLoaderAnimationStartMode;

/**
 * <b>Nothing in a container may start playing by itself.</b> The loader's default is
 * `GLTFLoaderAnimationStartMode.FIRST`: it starts each container's first clip, and a container is never added to the
 * scene here — so those animatables run every frame, evaluating curves on nodes nobody draws, for every asset loaded.
 * The testbench measured 570 of them for 19 characters, about a tenth of its CPU (found by the physics session).
 *
 * A structure should have no animatable at all, and an avatar only the clips `CharacterRig` is crossfading. Clones from
 * `instantiateModelsToScene` do not inherit a playing state, so this takes nothing away from what we start ourselves.
 */
const ASSET_LOAD_OPTIONS = { pluginOptions: { gltf: { animationStartMode: ANIMATION_START_NONE } } } as const;

/**
 * Loads published assets and places instances of them.
 *
 * <b>One `AssetContainer` per asset, never added to the scene</b>; every placement is `instantiateModelsToScene` from
 * it, so geometry and materials are shared and N copies of a building cost one draw call per primitive. Concurrent
 * requests for one asset share a single in-flight promise, so a placement pass that asks for the same house twenty times
 * fetches it once.
 *
 * <b>Separate from the entity renderer, and that is a decision rather than an omission.</b> `render/entity-layer.ts`
 * evaluates up to ten thousand positions on the CPU each frame and packs them into one thin-instance buffer per
 * archetype; a transform there belongs to the packer. Here it belongs to the scene graph. The two cannot own the same
 * entity, so this path is for PLACEMENTS — buildings, terminals, a shuttleport, tens to low hundreds of them — and the
 * packer keeps the crowd.
 *
 * <b>Every GLB is fetched from the same folder URL.</b> Babylon's glTF loader keys a texture cache entry by
 * `"data:" + rootUrl + uri`, so the shared KTX2 library collapses to one GPU texture per file only while every asset
 * comes from one root. Do not shard the asset base.
 *
 * <b>The catalogue is the immutable one the server named.</b> `catalogue.json` is a mutable pointer for tools; the server
 * announces `catalogue-<version>.json` in `/typhon/demo.json` because it reads the same descriptors for collision and
 * portals, and two readers of a mutable pointer agree only by luck.
 */

export interface LoadedAsset {
  readonly id: string;
  readonly descriptor: Descriptor;
  readonly container: AssetContainer;
}

/** A placed instance: its root, what it drew, what it lit, and its child assets. */
export interface Instance {
  readonly id: string;
  readonly root: TransformNode;
  readonly meshes: readonly AbstractMesh[];
  readonly lights: readonly Light[];
  readonly animationGroups: readonly AnimationGroup[];
  readonly children: readonly Instance[];
  dispose(): void;
}

export class AssetLibrary {
  private readonly loading = new Map<string, Promise<LoadedAsset>>();
  private readonly describing = new Map<string, Promise<Descriptor>>();
  /** The resolved descriptors, for {@link described}. */
  private readonly descriptors = new Map<string, Descriptor>();
  /** Assets whose load failed: asked for once, then skipped. */
  private readonly failed = new Set<string>();
  private readonly scene: Scene;
  private readonly base: string;
  readonly catalogue: Catalogue;

  /**
   * Called once for every asset whose container has just loaded, children included.
   *
   * The renderer uses it to give a container's meshes their rendering group and their share of baked AO — both
   * properties of the asset, not of a placement, and both of which an instance inherits.
   */
  onLoad: ((asset: LoadedAsset) => void) | null = null;

  private constructor(scene: Scene, base: string, catalogue: Catalogue) {
    this.scene = scene;
    this.base = base;
    this.catalogue = catalogue;
  }

  /**
   * Reads a catalogue and holds it for the session.
   *
   * @param scene The scene instances are placed in.
   * @param base The asset folder URL, with its trailing slash — one root for every asset, see above.
   * @param catalogueName The immutable catalogue the server pinned.
   */
  static async open(scene: Scene, base: string, catalogueName: string): Promise<AssetLibrary> {
    const response = await fetch(base + catalogueName);
    if (!response.ok) {
      throw new Error(`${catalogueName}: HTTP ${response.status}`);
    }

    const catalogue = (await response.json()) as Catalogue;
    if (catalogue.schema !== 2) {
      // The descriptors are a contract with the server, so a schema this build does not know is refused rather than
      // read for the fields that happen to look familiar.
      throw new Error(`${catalogueName}: schema ${String(catalogue.schema)}, and this client reads schema 2`);
    }

    return new AssetLibrary(scene, base, catalogue);
  }

  get ids(): readonly string[] {
    return Object.keys(this.catalogue.assets);
  }

  get version(): string {
    return String(this.catalogue.version);
  }

  /**
   * The descriptor of an asset already fetched, or `null` — a synchronous look-up for a per-frame reader.
   *
   * Collision needs an asset's shapes on the frame a body touches it, and cannot await: the descriptor is already in
   * hand because the renderer fetched it to place the thing, so this is the reading of it that costs nothing.
   */
  described(id: string): Descriptor | null {
    return this.descriptors.get(id) ?? null;
  }

  /** One asset's descriptor (a few KB), cached: what placement planning needs before any geometry is fetched. */
  describe(id: string): Promise<Descriptor> {
    let pending = this.describing.get(id);
    if (pending === undefined) {
      pending = this.fetchDescriptor(id);
      this.describing.set(id, pending);
    }

    return pending;
  }

  /** The descriptor and container of an asset, loading them on first use, child assets included. */
  acquire(id: string): Promise<LoadedAsset> {
    let pending = this.loading.get(id);
    if (pending === undefined) {
      pending = this.load(id);
      // <b>A rejection is remembered as a failure, not as a pending load.</b> Caching the rejected promise made every
      // caller re-await the same failure for ever, which turned a 404 into a per-frame retry that consumed the
      // placement budget and starved every healthy structure behind it.
      pending.catch(() => {
        this.loading.delete(id);
        this.failed.add(id);
      });
      this.loading.set(id, pending);
    }

    return pending;
  }

  /** Whether an asset has failed to load; a caller skips it rather than asking again. */
  hasFailed(id: string): boolean {
    return this.failed.has(id);
  }

  /** Places an instance, and its child assets, at a world pose. */
  async instantiate(id: string, pose: Pose, parent: TransformNode | null = null, label = id): Promise<Instance> {
    const asset = await this.acquire(id);
    const root = new TransformNode(`place:${label}`, this.scene);
    root.position = Vector3.FromArray(pose.pos);
    root.rotationQuaternion = Quaternion.FromArray(pose.rot);
    root.parent = parent;

    // <b>`doNotInstantiate: false` is not the default, and the default is a trap.</b> Babylon 9.26's
    // `instantiateModelsToScene` CLONES unless told otherwise (`assetContainer.js`: `doNotInstantiate: true`), so a
    // street of thirty-four buildings arrived as thirty-four full meshes and 192 draw calls where it should have been
    // one draw per primitive per ASSET. Measured in the browser: zero `InstancedMesh` in the scene until this was
    // passed. Skinned meshes are instanced by nobody — Babylon refuses them itself, a few lines above the flag it
    // reads — so the character keeps its own skeleton and this is safe for it.
    const entries = asset.container.instantiateModelsToScene((name) => `${label}/${name}`, false, {
      doNotInstantiate: false,
    });
    for (const node of entries.rootNodes) {
      node.parent = root;
    }

    const meshes = entries.rootNodes.flatMap((node) => node.getChildMeshes(false));
    const lights = entries.rootNodes
      .flatMap((node) => node.getDescendants(false))
      .filter((node): node is Light => node instanceof Light);
    // An asset's lamps light that asset and nothing else: an interior's lights must not flood the street outside it.
    for (const light of lights) {
      light.includedOnlyMeshes = meshes;
    }

    // Children are placed in WORLD space with the composed pose rather than under the parent's glTF root: that root
    // mirrors X and the child's own root mirrors again, which `asset-frame.ts` explains at length.
    const children = await Promise.all(
      asset.descriptor.children.map((child, index) =>
        this.instantiate(
          child.asset,
          poseToWorld(pose, child.pos, child.rot),
          parent,
          `${label}/${child.asset}.${index}`,
        ),
      ),
    );

    return {
      id,
      root,
      meshes,
      lights,
      animationGroups: entries.animationGroups,
      children,
      dispose() {
        for (const child of children) {
          child.dispose();
        }

        entries.dispose();
        root.dispose();
      },
    };
  }

  private async fetchDescriptor(id: string): Promise<Descriptor> {
    const entry = this.catalogue.assets[id];
    if (entry === undefined) {
      throw new Error(`asset "${id}" is not in catalogue ${this.catalogue.version}`);
    }

    const response = await fetch(this.base + entry.descriptor);
    if (!response.ok) {
      // The rejected promise must not stay in `describing`, or the descriptor can never be fetched again this session.
      this.describing.delete(id);
      throw new Error(`${entry.descriptor}: HTTP ${response.status}`);
    }

    const descriptor = (await response.json()) as Descriptor;
    this.descriptors.set(id, descriptor);
    return descriptor;
  }

  private async load(id: string): Promise<LoadedAsset> {
    const entry = this.catalogue.assets[id];
    if (entry === undefined) {
      throw new Error(`asset "${id}" is not in catalogue ${this.catalogue.version}`);
    }

    const descriptor = await this.describe(id);
    const container = await LoadAssetContainerAsync(this.base + entry.glb, this.scene, ASSET_LOAD_OPTIONS);
    const children = await Promise.all(descriptor.children.map((child) => this.acquire(child.asset)));
    const asset = { id, descriptor, container };
    // Prepared here so a CHILD asset is prepared too. Moving the rendering group from the instance to the container
    // was right — an instance takes its group from what it instances — but a child is instantiated from its own
    // container, which nothing else reaches: the shuttleport's ticket terminal drew in group 0 and the depth clear
    // before the scene group wiped it.
    for (const prepared of [asset, ...children]) {
      this.onLoad?.(prepared);
    }

    return asset;
  }
}
