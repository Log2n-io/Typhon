import { registerBuiltInLoaders } from '@babylonjs/loaders/dynamic';
import { TransformNode } from '@babylonjs/core/Meshes/transformNode';
import type { Scene } from '@babylonjs/core/scene';
import { AssetLibrary, type LoadedAsset } from '../assets/asset-library';
import { configureDecoders } from '../assets/decoders';
import { mountCharacter, type CharacterMount } from './character-mount';
import type { AssetSet } from '../data/server-config';
import { AssetLighting } from '../render/asset-lighting';
import { SCENE_GROUP } from '../render/render-groups';

/**
 * Opens the server's published asset set: the decoders, the lighting PBR needs, the catalogue, and the avatar.
 *
 * <b>It owns the root every placement hangs off, and nothing else that is placed.</b> Structures are placed by
 * `static-assets.ts` from what the server replicated, and the avatar by `character-mount.ts` from the body this session
 * steers — both under this node, so one `Vector3` carries the render origin for all of them.
 *
 * The fixed arrangement this used to place was slice 1's scaffolding: four buildings at hardcoded offsets, to prove the
 * pipeline on things that do not move before anything depended on it. The server's own structures replaced it.
 */

/**
 * The character the ridden avatar is drawn as.
 *
 * One asset, chosen here rather than replicated: what a body looks like is not yet on the wire, and a constant is the
 * honest placeholder for a field that does not exist. `casual2_male` is the testbench's own demo character, so the two
 * show the same person.
 */
const AVATAR_ASSET = 'casual2_male';

/**
 * Prepares an asset's own meshes, once, for this client's scene.
 *
 * <b>On the CONTAINER's meshes, never on an instance's.</b> An `InstancedMesh` takes its rendering group from the mesh
 * it instances — Babylon says so out loud, once per instance ("setting renderingGroupId of an instanced mesh has no
 * effect on the scene"), which is a hundred and sixty-nine warnings for a street. Materials are shared too
 * (`cloneMaterials: false`), so the AO factor belongs here for the same reason: once per asset, not once per building.
 *
 * @param asset The loaded asset, whose container is cached by the library.
 * @param lighting The scene's asset lighting.
 */
export function prepareAsset(asset: LoadedAsset, lighting: AssetLighting): void {
  for (const mesh of asset.container.meshes) {
    mesh.renderingGroupId = SCENE_GROUP;
  }

  lighting.adopt(asset.container.meshes);
}

export interface AssetMount {
  readonly library: AssetLibrary;
  /** The node every placement hangs off, which carries the render origin; see {@link AssetMount.rebase}. */
  readonly root: TransformNode;
  readonly lighting: AssetLighting;
  /** The rigged avatar, or `null` when the catalogue publishes no character to be one. */
  readonly character: CharacterMount | null;
  /**
   * Moves the arrangement to follow the render origin. Called every frame; cheap, because it moves one node.
   *
   * @param originX The render origin's planet X.
   * @param originZ Its planet Z.
   */
  rebase(originX: number, originZ: number): void;
  dispose(): void;
}

/**
 * Configures the decoders, lights the scene for PBR, reads the catalogue and mounts the avatar.
 *
 * @param scene The live scene.
 * @param set The asset set the server announced, already validated by `server-config`.
 * @param origin The render origin as it stands now; the root follows it from there.
 * @returns The mount, which owns everything it created.
 */
export async function mountAssets(
  scene: Scene,
  set: AssetSet,
  origin: { readonly x: number; readonly z: number },
): Promise<AssetMount> {
  // Before the first load, so no asset pays the decoder's download and compile on its own critical path — and so nothing
  // is ever fetched from a third-party CDN, which is Babylon's default and has no business in a game's boot path.
  const vendorRoot = configureDecoders();
  // The glTF loader and its extensions, registered lazily: only the plugin metadata is in the bundle, and a loader's body
  // is dynamically imported when something is actually loaded.
  //
  // It registers all seven built-in formats, so `dist` gains chunks for FBX, OBJ, SPLAT, STL, USD and BVH that nothing
  // here will ever fetch — about half a megabyte of files served to nobody. Registering glTF alone means copying the
  // library's own registration block, and paying attention to it on every Babylon upgrade, to save bytes that are never
  // requested. One supported call is the better trade.
  registerBuiltInLoaders();

  const lighting = new AssetLighting(scene, vendorRoot);
  const library = await AssetLibrary.open(scene, set.base, set.catalogue);
  // Once per container, children included — an instance inherits both from what it instances.
  library.onLoad = (asset) => {
    prepareAsset(asset, lighting);
  };

  // <b>Everything hangs off one node, and that node carries the render origin.</b> Positions reach the GPU relative to a
  // point snapped to 1 km near the camera (C8), subtracted in float64, so float32 only ever holds small values. A
  // placement left in planet coordinates is five kilometres from everything the client draws — it is in the scene, it is
  // enabled, it is ready, and it is nowhere near the camera. Keeping placements in planet coordinates under a node that
  // holds `-origin` means one `Vector3` moves when the origin snaps, however many of them there are.
  const root = new TransformNode('assets', scene);
  root.position.set(-origin.x, 0, -origin.z);

  // The avatar: the one asset whose absence must not stop the rest being drawn, because a catalogue is built elsewhere
  // and may simply not publish characters.
  let character: CharacterMount | null = null;
  if (library.catalogue.assets[AVATAR_ASSET] === undefined) {
    console.warn(`assets: ${AVATAR_ASSET} is not in ${library.version}; the avatar stays a capsule`);
  } else {
    try {
      character = await mountCharacter(library, AVATAR_ASSET, root, lighting);
    } catch (error) {
      // A 404, a GLB that will not parse, a character published without animations: none of them is a reason for the
      // street to go undrawn. The catalogue check above was only one of the three ways this fails.
      console.warn(`assets: ${AVATAR_ASSET} did not mount; the avatar stays a capsule`, error);
      character = null;
    }
  }

  console.info(
    `assets: catalogue ${library.version}, ${String(library.ids.length)} published` +
      (character === null ? ', no avatar' : `, avatar ${AVATAR_ASSET} ${character.height.toFixed(2)} m`),
  );

  return {
    library,
    root,
    lighting,
    character,
    rebase(originX: number, originZ: number): void {
      root.position.set(-originX, 0, -originZ);
    },
    dispose(): void {
      character?.dispose();
      root.dispose();
      lighting.dispose();
    },
  };
}
