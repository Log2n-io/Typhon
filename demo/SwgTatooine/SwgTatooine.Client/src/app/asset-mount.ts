import { registerBuiltInLoaders } from '@babylonjs/loaders/dynamic';
import { TransformNode } from '@babylonjs/core/Meshes/transformNode';
import type { Scene } from '@babylonjs/core/scene';
import { AssetLibrary, type LoadedAsset } from '../assets/asset-library';
import { configureDecoders } from '../assets/decoders';
import { mountCharacter, type CharacterMount } from './character-mount';
import type { AssetSet } from '../data/server-config';
import type { SceneLighting } from '../render/scene-lighting';
import { SCENE_GROUP } from '../render/render-groups';

/**
 * Opens the server's published asset set: the decoders, the catalogue, and the avatar.
 *
 * <b>The lighting is a parameter, not something this creates.</b> See {@link SceneLighting}: this function is allowed to
 * fail and be swallowed, and the terrain is lit by the same sun, so a missing GLB must not be able to put out the sky.
 *
 * <b>It owns the root every placement hangs off, and nothing else that is placed.</b> Structures are placed by
 * `static-assets.ts` from what the server replicated, and the avatar by `character-mount.ts` from the body this session
 * steers — both under this node, so one `Vector3` carries the render origin for all of them.
 *
 * The fixed arrangement this used to place was slice 1's scaffolding: four buildings at hardcoded offsets, to prove the
 * pipeline on things that do not move before anything depended on it. The server's own structures replaced it.
 */

/**
 * The character the avatar is drawn as until it knows whom it stands in for.
 *
 * <b>Only the first outfit, no longer the only one.</b> What a player looks like is on the wire (`outfit`, from its
 * character) and the app re-dresses the avatar to it through {@link AssetMount.wear} — until 2026-10-25 this constant was
 * what EVERY client drew its own avatar as, while it drew everyone else by netId, so no two sessions agreed on what either
 * of them looked like. `casual2_male` is the testbench's own demo character, and loading it at boot keeps the avatar's
 * first frame a person rather than a capsule.
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
export function prepareAsset(asset: LoadedAsset, lighting: SceneLighting): void {
  for (const mesh of asset.container.meshes) {
    mesh.renderingGroupId = SCENE_GROUP;
  }

  lighting.adopt(asset.container.meshes);
}

/**
 * Puts the root every placement hangs off at the render origin — only when the origin has moved.
 *
 * <b>Writing the same position is not free.</b> `Vector3.set` marks the vector dirty whatever it is given, so the root
 * recomputed its world matrix every frame and bumped its child counter, and every placement under it — measured, 2 251
 * meshes in a city and 5 675 over the planet — recomputed its world matrix and bounding box with nothing changed. The
 * origin snaps by the kilometre, so on all but one frame in thousands there is nothing to write.
 *
 * @param root The placements' root.
 * @param originX The render origin's planet X.
 * @param originZ Its planet Z.
 */
export function placeRoot(root: TransformNode, originX: number, originZ: number): void {
  const at = root.position;
  if (at.x !== -originX || at.y !== 0 || at.z !== -originZ) {
    at.set(-originX, 0, -originZ);
  }
}

/** How opaque the "Server pos" ghost is: enough to read its pose, little enough to see the predicted body through it. */
export const GHOST_ALPHA = 0.4;

export interface AssetMount {
  readonly library: AssetLibrary;
  /** The node every placement hangs off, which carries the render origin; see {@link AssetMount.rebase}. */
  readonly root: TransformNode;
  readonly lighting: SceneLighting;
  /**
   * The rigged avatar, or `null` while it loads — see {@link avatarPending} — or when the catalogue publishes no character
   * to be one. Replaced by {@link wear}.
   */
  readonly character: CharacterMount | null;
  /**
   * Whether the avatar is waiting on an asset: its first character still loading, or a re-dress whose outfit has not
   * landed. The body is drawn by nobody meanwhile — not a capsule, not the old clothes (Loïc, 2026-10-26: what waits on an
   * asset is not drawn).
   */
  readonly avatarPending: boolean;
  /**
   * The same character at {@link GHOST_ALPHA}, drawn where the SERVER says the avatar is while "Server pos" is on, beside
   * the predicted one; `null` when there is no avatar.
   */
  readonly ghost: CharacterMount | null;
  /** The asset the avatar is drawn as now, or the one it is being re-dressed in; `null` with no avatar. */
  readonly wearing: string | null;
  /**
   * Re-dresses the avatar and its ghost in another character asset. The old rigs keep drawing until the new ones have
   * loaded, then are disposed; the caller re-applies everything per-frame (subject, riding, weapon, opacity) on the next
   * frame, as it does every frame. A request for what is already worn, or being put on, does nothing; one that fails
   * keeps the old outfit and is not retried.
   *
   * @param assetId A `kind: "character"` asset with animations.
   */
  wear(assetId: string): void;
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
 * @param lighting The scene's sun and environment, created by the app and outliving this mount.
 * @returns The mount, which owns everything it created — the lighting excepted, which it did not.
 */
export async function mountAssets(
  scene: Scene,
  set: AssetSet,
  origin: { readonly x: number; readonly z: number },
  lighting: SceneLighting,
): Promise<AssetMount> {
  // Before the first load, so no asset pays the decoder's download and compile on its own critical path — and so nothing
  // is ever fetched from a third-party CDN, which is Babylon's default and has no business in a game's boot path.
  configureDecoders();
  // The glTF loader and its extensions, registered lazily: only the plugin metadata is in the bundle, and a loader's body
  // is dynamically imported when something is actually loaded.
  //
  // It registers all seven built-in formats, so `dist` gains chunks for FBX, OBJ, SPLAT, STL, USD and BVH that nothing
  // here will ever fetch — about half a megabyte of files served to nobody. Registering glTF alone means copying the
  // library's own registration block, and paying attention to it on every Babylon upgrade, to save bytes that are never
  // requested. One supported call is the better trade.
  registerBuiltInLoaders();

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
  // and may simply not publish characters. <b>Loaded in the background</b>: the mount — and with it every building, the
  // crowd and the terrain's props — used to wait for the avatar and its ghost to load one after the other first.
  let character: CharacterMount | null = null;
  let ghost: CharacterMount | null = null;
  // `wearing` is what was last asked for, `drawn` what the rigs are: they differ while a new outfit loads.
  let wearing: string | null = null;
  let drawn: string | null = null;
  let loading = false;
  const refused = new Set<string>();
  let disposed = false;
  // A call, not the variable: the compiler narrows a local across an await and would prove the check away (see `ClientApp.gone`).
  const isDisposed = (): boolean => disposed;
  if (library.catalogue.assets[AVATAR_ASSET] === undefined) {
    console.warn(`assets: ${AVATAR_ASSET} is not in ${library.version}; the avatar stays a capsule`);
  } else {
    loading = true;
    void (async () => {
      // Together: neither needs the other, and the second is the same GLB, already in hand once the first has it.
      // Settled rather than `all`, so a rig that loaded beside one that failed is disposed rather than leaked.
      const [rig, shadow] = await Promise.allSettled([
        mountCharacter(library, AVATAR_ASSET, root, lighting),
        mountCharacter(library, AVATAR_ASSET, root, lighting, GHOST_ALPHA),
      ]);
      loading = false;
      if (rig.status === 'rejected' || shadow.status === 'rejected' || isDisposed()) {
        if (rig.status === 'fulfilled') {
          rig.value.dispose();
        }

        if (shadow.status === 'fulfilled') {
          shadow.value.dispose();
        }

        if (!isDisposed()) {
          // A 404, a GLB that will not parse, a character published without animations: none of them is a reason for
          // the street to go undrawn. The catalogue check above was only one of the three ways this fails.
          const reason: unknown =
            rig.status === 'rejected' ? rig.reason : shadow.status === 'rejected' ? shadow.reason : null;
          console.warn(`assets: ${AVATAR_ASSET} did not mount; the avatar stays a capsule`, reason);
        }

        return;
      }

      character = rig.value;
      ghost = shadow.value;
      wearing = drawn = AVATAR_ASSET;
      console.info(`assets: avatar ${AVATAR_ASSET} ${rig.value.height.toFixed(2)} m`);
    })();
  }

  console.info(`assets: catalogue ${library.version}, ${String(library.ids.length)} published`);
  return {
    library,
    root,
    lighting,
    get character() {
      return character;
    },
    get ghost() {
      return ghost;
    },
    get wearing() {
      return wearing;
    },
    get avatarPending() {
      return loading || wearing !== drawn;
    },
    wear(assetId: string): void {
      // No avatar at all is a catalogue without characters; re-dressing cannot create one.
      if (character === null || assetId === wearing || refused.has(assetId)) {
        return;
      }

      if (library.catalogue.assets[assetId] === undefined) {
        refused.add(assetId);
        console.warn(`assets: ${assetId} is not in ${library.version}; the avatar keeps ${String(wearing)}`);
        return;
      }

      const asked = assetId;
      wearing = asked;
      void (async () => {
        let next: CharacterMount | null = null;
        let nextGhost: CharacterMount;
        try {
          next = await mountCharacter(library, asked, root, lighting);
          nextGhost = await mountCharacter(library, asked, root, lighting, GHOST_ALPHA);
        } catch (error) {
          next?.dispose();
          refused.add(asked);
          console.warn(`assets: ${asked} did not mount; the avatar keeps its outfit`, error);
          if (wearing === asked) {
            wearing = drawn;
          }

          return;
        }

        // Superseded while loading — a later wear, or the whole mount gone: this one is nobody's.
        if (disposed || wearing !== asked) {
          next.dispose();
          nextGhost.dispose();
          return;
        }

        character.dispose();
        ghost?.dispose();
        character = next;
        ghost = nextGhost;
        drawn = asked;
      })();
    },
    rebase(originX: number, originZ: number): void {
      placeRoot(root, originX, originZ);
    },
    dispose(): void {
      disposed = true;
      character?.dispose();
      ghost?.dispose();
      root.dispose();
      // Not the lighting: the app created it and the terrain is still lit by it.
    },
  };
}
