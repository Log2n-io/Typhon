import type { Scene } from '@babylonjs/core/scene';

import { placement } from '../assets/asset-frame';
import type { AssetLibrary } from '../assets/asset-library';
import type { Descriptor } from '../assets/descriptor';
import { planClipAtlas, type AtlasPlan } from '../character/clip-atlas';
import { CROWD_ROLES, RIG_OUTFIT, wearableOutfits } from '../character/outfits';
import { bakeClipAtlas, type BakeSource, type CrowdRig } from '../render/crowd-rig';
import { CrowdLayer } from '../render/crowd-layer';
import { createCrowdMaterial, type CrowdSkinPlugin } from '../render/crowd-material';
import { buildCrowdOutfit, type CrowdOutfit } from '../render/crowd-mesh';
import type { SceneLighting } from '../render/scene-lighting';

/**
 * Loads the catalogue's characters and builds the crowd from them.
 *
 * <b>Every outfit is loaded once, read, and thrown away.</b> What survives is a merged mesh per detail level and some
 * rows of a texture; the GLB, its skeleton and its seventeen animation groups are all transient. That is the whole
 * economy of this approach — a crowd of five thousand holds the same GPU memory as nineteen characters do.
 *
 * The cost is concentrated in the loads, not in the bake: the spike measured its bake at 14–21 ms and the loading and
 * merging of nineteen outfits at about 400 ms. If that ever matters it is the merge to move off the critical path, or
 * to do lazily as an outfit is first worn — not the bake.
 */

export interface CrowdMount {
  readonly layer: CrowdLayer;
  readonly rig: CrowdRig;
  readonly plan: AtlasPlan;
  readonly outfits: readonly CrowdOutfit[];
  readonly skin: CrowdSkinPlugin;
  /** What the HUD shows: rows, bytes and how many outfits were built. */
  readonly stats: { rows: number; bytes: number; outfits: number; bakeMs: number; buildMs: number };
  dispose(): void;
}

/**
 * Builds the crowd.
 *
 * @param scene The live scene.
 * @param library The open asset library.
 * @param lighting The scene's sun, so the crowd casts.
 * @param capacity The most bodies the crowd will draw at once.
 * @returns The mount, or `null` when the catalogue publishes no usable character.
 */
export async function mountCrowd(
  scene: Scene,
  library: AssetLibrary,
  lighting: SceneLighting,
  capacity: number,
): Promise<CrowdMount | null> {
  // Descriptors first: a few KB each, and enough to plan the whole atlas before a single GLB is fetched.
  // <b>All at once.</b> Awaiting them one at a time is one serial round trip per asset in the whole catalogue — not
  // just per character — on the load path, for files of a few KB each.
  const described = new Map<string, Descriptor>();
  const fetched = await Promise.all(
    library.ids.map(async (id) => {
      try {
        return [id, await library.describe(id)] as const;
      } catch {
        // A descriptor that will not fetch is a deployment fault for that asset alone; the rest of the crowd stands.
        return null;
      }
    }),
  );
  for (const entry of fetched) {
    if (entry !== null) {
      described.set(entry[0], entry[1]);
    }
  }

  const wearable = wearableOutfits(described);
  if (wearable.length === 0) {
    console.warn('crowd: the catalogue publishes no wearable character');
    return null;
  }

  const joints = described.get(RIG_OUTFIT)?.render.joints ?? described.get(wearable[0])?.render.joints ?? 0;
  if (joints <= 0) {
    console.warn('crowd: no character declares its joint count; the crowd needs it to size the atlas');
    return null;
  }

  const plan = planClipAtlas(described, joints);

  // <b>Every outfit must resolve every driven role, checked here, once.</b> `plan.slotOf` returning null is the
  // quietest failure this design has: the layer skips `play`, the clip slot stays zero, the shader reads a frame
  // count of zero and every vertex of that body collapses to a point — with the draw calls, the instance counts and
  // the tier totals all still correct. It is one renamed catalogue id away, because {@link RIG_OUTFIT} is a literal:
  // lose `casual_male` and the atlas plans zero rows while everything downstream reports health.
  const unresolved: string[] = [];
  for (const id of wearable) {
    for (const role of CROWD_ROLES) {
      if (plan.slotOf(id, role) === null) {
        unresolved.push(`${id}/${role}`);
      }
    }
  }

  if (unresolved.length > 0) {
    console.warn(
      `crowd: ${String(unresolved.length)} outfit/role pairs have no atlas row, starting ${unresolved[0]}; the crowd stands down`,
    );
    return null;
  }

  // ---- the bake: load only the outfits the plan names as sources ------------------------------------------------
  const sources = new Map<string, BakeSource>();
  const sourceIds = new Set(plan.bakes.map((b) => b.source));
  for (const id of sourceIds) {
    await library.acquire(id);
    sources.set(id, { instance: await library.instantiate(id, placement([0, 0, 0], 0), null, `crowd-bake:${id}`) });
  }

  const tBake = performance.now();
  let rig: CrowdRig;
  try {
    rig = bakeClipAtlas(scene, plan, sources);
  } finally {
    // The sources have served their purpose whatever happened: their matrices are in the texture.
    for (const source of sources.values()) {
      source.instance.dispose();
    }
  }

  const bakeMs = performance.now() - tBake;

  // ---- the meshes -----------------------------------------------------------------------------------------------
  const tBuild = performance.now();
  const { material, skin } = createCrowdMaterial(scene, rig);
  const outfits: CrowdOutfit[] = [];
  const outfitDescriptors: Descriptor[] = [];
  for (const id of wearable) {
    const descriptor = described.get(id);
    if (descriptor === undefined) {
      continue;
    }

    try {
      await library.acquire(id);
      const instance = await library.instantiate(id, placement([0, 0, 0], 0), null, `crowd-build:${id}`);
      try {
        // Height from this outfit's own render bounds. A constant would be wrong for every outfit but one, and it
        // drives the projected-size tier, the cull sphere AND the clip rate.
        const height = descriptor.bounds.render[1][1];
        outfits.push(buildCrowdOutfit(scene, instance, rig, material, height, id, descriptor.render.lods ?? null));
        outfitDescriptors.push(descriptor);
      } finally {
        instance.dispose();
      }
    } catch (error) {
      // One outfit that will not merge — a texture, five influences, a gap in its ladder — must not cost the crowd
      // every other costume. It is loud, because each of those is a publishing fault worth fixing.
      console.warn(`crowd: ${id} was not built`, error);
    }
  }

  const buildMs = performance.now() - tBuild;
  if (outfits.length === 0) {
    rig.dispose();
    material.dispose();
    console.warn('crowd: no outfit could be built');
    return null;
  }

  // Casting is marked on the merged meshes, which ARE in the scene — unlike a container's, which the shadow map's
  // predicate never reaches. Receiving is set on the mesh at merge time.
  for (const outfit of outfits) {
    lighting.castShadows(outfit.lods.map((l) => l.mesh));
  }

  const layer = new CrowdLayer({
    scene,
    plan,
    outfits,
    skin,
    described: outfitDescriptors,
    capacity,
  });

  // <b>Verified, not assumed.</b> The gate exists because a crowd that builds, classifies and issues draw calls can
  // still put nothing on screen — this one did, for hours, with every counter healthy. It is set here because the
  // bodies have been measured on screen: the clip slots resolve, the per-instance buffers carry real rows, and
  // hiding the crowd meshes changes pixels where the bodies project. If that stops being true, the boxes come back
  // by flipping this one line, and the world is never emptier than it was before the crowd existed.
  layer.confirmDrawing(true);

  console.info(
    `crowd: ${String(outfits.length)} outfits, ${String(plan.rows)} atlas rows, ` +
      `${(rig.textureBytes / (1024 * 1024)).toFixed(2)} MB, bake ${bakeMs.toFixed(0)} ms, build ${buildMs.toFixed(0)} ms`,
  );

  return {
    layer,
    rig,
    plan,
    outfits,
    skin,
    stats: { rows: plan.rows, bytes: rig.textureBytes, outfits: outfits.length, bakeMs, buildMs },
    dispose() {
      layer.dispose();
      rig.dispose();
      material.dispose();
    },
  };
}
