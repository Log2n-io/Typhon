import { DirectionalLight } from '@babylonjs/core/Lights/directionalLight';
import { ShadowGenerator } from '@babylonjs/core/Lights/Shadows/shadowGenerator';
import '@babylonjs/core/Lights/Shadows/shadowGeneratorSceneComponent';
import { PBRMaterial } from '@babylonjs/core/Materials/PBR/pbrMaterial';
import { CubeTexture } from '@babylonjs/core/Materials/Textures/cubeTexture';
import type { RenderTargetTexture } from '@babylonjs/core/Materials/Textures/renderTargetTexture';
import { Texture } from '@babylonjs/core/Materials/Textures/texture';
import '@babylonjs/core/Materials/Textures/Loaders/envTextureLoader';
import type { Matrix } from '@babylonjs/core/Maths/math.vector';
import type { AbstractMesh } from '@babylonjs/core/Meshes/abstractMesh';
import type { Scene } from '@babylonjs/core/scene';

import {
  SHADOW_BACKOFF_M,
  SHADOW_FAR_M,
  SHADOW_MAP_SIZE,
  SHADOW_NEAR_M,
  SHADOW_RADIUS_M,
  SUN_DIRECTION,
} from './sun';

/** Marks a placed mesh as one the sun's shadow map should draw. */
const CASTS_SUN = 'castsSun';

/** Whether a mesh has been marked by {@link AssetLighting.castShadows}. */
function isCaster(mesh: AbstractMesh): boolean {
  return (mesh.metadata as Record<string, unknown> | null)?.[CASTS_SUN] === true;
}

/**
 * What a published asset needs to be visible at all: a sun, and an environment.
 *
 * <b>Image-based lighting is not a nicety here, it is a requirement of the asset contract.</b> Each GLB carries its own
 * baked ambient occlusion, which the glTF loader puts in `PBRMaterial.ambientTexture` — and that only attenuates
 * environment and ambient light. Under a sun alone, with `scene.environmentTexture` unset, every asset's AO is simply
 * invisible and the geometry reads flat. The environment also has to exist for a PBR material to show anything in a
 * scene which, until now, had no lights at all: this client draws terrain and entities through its own
 * `ShaderMaterial`s, which take neither.
 *
 * <b>Deliberately not touching tone mapping.</b> The asset pipeline asks for Khronos PBR Neutral tone mapping, and it is
 * right to for interiors: lamps and emissive panels clip to flat white without it. But image processing is a property of
 * the whole scene, so turning it on changes the colour of the terrain, the entity bands and the heatmap — every pixel
 * this client already draws — in a change whose subject is one building. It belongs with the interior lighting profiles,
 * where there is something to judge it against.
 *
 * @see {@link ASSET_AO_ON_DIRECT} for what carries the AO in the meantime.
 */

/**
 * How much of an asset's baked AO also shades the sun.
 *
 * Babylon's default is 0, so AO would show only where the environment reaches. On an exterior lit mostly by a strong
 * directional light that hides nearly all of it, which wastes the one thing the bake exists for. The asset pipeline
 * suggests 0.3–0.5; this is the middle of it.
 */
export const ASSET_AO_ON_DIRECT = 0.4;

/** Where the prefiltered environment lives, inside the vendored decoder folder. */
const ENVIRONMENT = 'environmentSpecular.env';

export class AssetLighting {
  readonly sun: DirectionalLight;
  private readonly scene: Scene;
  private generator: ShadowGenerator | null = null;

  /**
   * @param scene The scene to light. Its clear colour and image processing are left exactly as they are.
   * @param vendorRoot The absolute URL of the vendored Babylon run-time folder, as `configureDecoders` returns it.
   */
  constructor(scene: Scene, vendorRoot: string) {
    this.scene = scene;
    scene.environmentTexture = CubeTexture.CreateFromPrefilteredData(vendorRoot + ENVIRONMENT, scene);
    scene.environmentIntensity = 1;
    // <b>The scene's one sun</b> — see {@link SUN_DIRECTION} for why this is no longer a direction of its own. High
    // enough to light the walls, low enough that a facade and a roof are different brightnesses.
    this.sun = new DirectionalLight('sun', SUN_DIRECTION.clone(), scene);
    this.sun.intensity = 1.5;
    // The slab is placed by `frameShadows`, not fitted to the casters: see SHADOW_RADIUS_M.
    this.sun.autoUpdateExtends = false;
    this.sun.autoCalcShadowZBounds = false;
    this.sun.shadowMinZ = SHADOW_NEAR_M;
    this.sun.shadowMaxZ = SHADOW_FAR_M;
    this.sun.orthoLeft = -SHADOW_RADIUS_M;
    this.sun.orthoRight = SHADOW_RADIUS_M;
    this.sun.orthoBottom = -SHADOW_RADIUS_M;
    this.sun.orthoTop = SHADOW_RADIUS_M;
  }

  /**
   * Turns the sun's shadow map on or off.
   *
   * <b>Creating and disposing the generator, rather than keeping it and skipping it.</b> A live generator renders its
   * whole caster list into a 2 048 map every frame whether or not anything samples it, so a disabled map that still
   * exists costs exactly what an enabled one does.
   *
   * @param enabled Whether to cast.
   */
  setShadows(enabled: boolean): void {
    if (enabled === (this.generator !== null)) {
      return;
    }

    if (!enabled) {
      this.generator?.dispose();
      this.generator = null;
      return;
    }

    const generator = new ShadowGenerator(SHADOW_MAP_SIZE, this.sun);
    // <b>A plain depth map, deliberately.</b> The terrain is drawn by this client's own `ShaderMaterial`, which is
    // outside Babylon's material framework and so cannot take its shadow includes: the ground samples the map itself
    // (`shaders.ts`, `shadowAt`). PCF and the exponential maps change what is stored and how it must be read — PCF
    // wants a hardware depth texture and a comparison sampler — so the one format that can be read with a plain
    // `texture2D` is the one to ask for. The softening the others would buy is done in the ground's own 2x2 tap.
    generator.useExponentialShadowMap = false;
    generator.useBlurExponentialShadowMap = false;
    // <b>No filter, and this is a dead end rather than a preference.</b> Three were tried and measured in game:
    //
    //   - PCF (what the testbench's sun uses, `render/lighting.ts:123-124`): the meshes filter properly, but Babylon
    //     sets TEXTURE_COMPARE_MODE on the map, and a depth texture with a comparison mode cannot be read by a plain
    //     `texture2D` at all. The terrain samples the map by hand -- being a raw ShaderMaterial, nothing injects the
    //     shadow includes into it -- so the whole desert loses its shadows. Measured: map readback all zeros.
    //   - Contact hardening: the same depth texture, the same outcome.
    //   - Poisson: keeps the float map, so the ground survives, but its taps moire across a flat surface and a stone
    //     plinth came out striped. Worse than no filter.
    //
    // So the meshes keep a hard edge and the ground softens its own in `shaders.ts`. The testbench has no such split
    // because it has no custom shader at all: every one of its grounds is a PBRMaterial (`modes/demo-mode.ts:201-207`,
    // `modes/viewer-mode.ts:65-70`, `modes/crowd-mode.ts:142-155`) and Babylon runs PCF over it. Matching that means
    // moving this terrain onto Babylon's own pipeline -- a MaterialPluginBase port, not a setting -- after which PCF,
    // PCSS and CSM all work on both, from one map, and the hand-written sampler is deleted.
    generator.usePoissonSampling = false;
    generator.bias = 0.0005;
    generator.normalBias = 0.02;

    const map = generator.getShadowMap();
    if (map !== null) {
      // <b>A predicate, not a list — and not the containers' own meshes.</b> Two things were wrong with the obvious
      // version. A placement is an `InstancedMesh` of a mesh that lives in an `AssetContainer` and is deliberately
      // never added to the scene, so adding the container's mesh as a caster puts a mesh in the render list that is
      // not ready to render and parked at the origin: measured, the 2 048 map came back uniformly 1.0 — nothing drawn
      // at all — with 37 casters registered. And a structure streams in and out as the session moves, so any list
      // this kept would hold disposed meshes within seconds.
      //
      // The predicate is re-evaluated from `scene.meshes` every frame, which costs a flag test per mesh (about 250 of
      // them) and is correct by construction: a disposed placement is not in the scene, so it cannot be in the list.
      map.renderListPredicate = (mesh) => mesh.isEnabled() && mesh.isVisible && isCaster(mesh);
      map.renderList = [];
      // <b>NEAREST, because the ground interpolates the COMPARISON, not the depth.</b> That is what percentage-closer
      // filtering means: compare the four surrounding texels and blend the four answers. Letting the sampler blend the
      // depths first and comparing once invents a surface halfway between a wall and the ground behind it. (With PCF
      // this would have to go -- hardware comparison needs linear -- which is one more way the two cannot coexist.)
      map.updateSamplingMode(Texture.NEAREST_SAMPLINGMODE);
    }

    this.generator = generator;
  }

  /** Whether the sun is currently casting. */
  get shadowsEnabled(): boolean {
    return this.generator !== null;
  }

  /** The sun's shadow map, for a material that samples it itself, or `null` when nothing is casting. */
  get shadowMap(): RenderTargetTexture | null {
    return this.generator?.getShadowMap() ?? null;
  }

  /** The light's view-projection, which takes a world position into the shadow map's clip space. */
  get shadowTransform(): Matrix | null {
    return this.generator?.getTransformMatrix() ?? null;
  }

  /**
   * The light's near and far planes, which is what its shadow pass encodes depth against.
   *
   * <b>Measured against the live map rather than read off the documentation.</b> Babylon's shadow pass stores
   * `(z + minZ) / (minZ + maxZ)` where `z` is the LIGHT-VIEW depth in metres, not the clip-space z the vertex shader
   * appears to hand it. The difference is not subtle: for a terminal 231 m from the shadow camera the texture holds
   * 0.5596 and the clip-space reading gives 0.0027, so every comparison says "lit" and the ground is simply never
   * shadowed. The ground recovers the view depth from the clip z, which is exact because the projection is
   * orthographic — verified in the browser against the stored texel: 0.55983 computed against 0.55957 stored, a
   * tenth of a metre apart at a 413 m range.
   */
  get shadowDepthValues(): readonly [number, number] {
    return [this.sun.shadowMinZ, this.sun.shadowMaxZ];
  }

  /**
   * Puts the shadow slab over a point, in the scene's own (render-relative) metres.
   *
   * Called every frame: it moves one light and recomputes one matrix, against a map that is re-rendered anyway.
   *
   * @param x Where to centre it, east.
   * @param y The ground's altitude there, so the slab is centred on the ground and not on the camera's eye.
   * @param z Where to centre it, north.
   */
  frameShadows(x: number, y: number, z: number): void {
    if (this.generator === null) {
      return;
    }

    // Back along the direction the light travels, which is where the light comes FROM.
    this.sun.position.set(
      x - (SUN_DIRECTION.x * SHADOW_BACKOFF_M),
      y - (SUN_DIRECTION.y * SHADOW_BACKOFF_M),
      z - (SUN_DIRECTION.z * SHADOW_BACKOFF_M),
    );
  }

  /**
   * Marks meshes as casting and receiving the sun's shadow.
   *
   * <b>Called on PLACED meshes, not on a container's.</b> See the predicate in {@link setShadows}: a container's mesh
   * is not in the scene and renders nothing into the map.
   *
   * @param meshes The placement's meshes.
   */
  castShadows(meshes: readonly AbstractMesh[]): void {
    for (const mesh of meshes) {
      mesh.metadata = { ...(mesh.metadata as object | null), [CASTS_SUN]: true };
    }
  }

  /**
   * Gives a placed instance's materials their share of AO on direct light.
   *
   * Per material rather than per scene, because this is a property of materials that carry a baked occlusion map, and
   * nothing else in this client does.
   *
   * @param meshes The instance's meshes.
   */
  adopt(meshes: readonly AbstractMesh[]): void {
    for (const mesh of meshes) {
      // <b>Receiving is the SOURCE mesh's property, not an instance's.</b> Babylon says so out loud, once per
      // instance ("Setting receiveShadows on an instanced mesh has no effect"), which is a hundred and eighty lines
      // of console for one street — and the shadow would not have landed on the buildings either way.
      mesh.receiveShadows = true;
    }

    for (const material of new Set(meshes.map((mesh) => mesh.material))) {
      if (material instanceof PBRMaterial && material.ambientTexture !== null) {
        material.ambientTextureImpactOnAnalyticalLights = ASSET_AO_ON_DIRECT;
      }
    }
  }

  dispose(): void {
    this.generator?.dispose();
    this.generator = null;
    this.sun.dispose();
    this.scene.environmentTexture?.dispose();
    this.scene.environmentTexture = null;
  }
}
