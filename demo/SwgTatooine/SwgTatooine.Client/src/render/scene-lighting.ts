import { DirectionalLight } from '@babylonjs/core/Lights/directionalLight';
import { ShadowGenerator } from '@babylonjs/core/Lights/Shadows/shadowGenerator';
import '@babylonjs/core/Lights/Shadows/shadowGeneratorSceneComponent';
import { PBRMaterial } from '@babylonjs/core/Materials/PBR/pbrMaterial';
import { CubeTexture } from '@babylonjs/core/Materials/Textures/cubeTexture';
import { Color3 } from '@babylonjs/core/Maths/math.color';
import { SphericalPolynomial } from '@babylonjs/core/Maths/sphericalPolynomial';
import '@babylonjs/core/Materials/Textures/Loaders/envTextureLoader';
import type { AbstractMesh } from '@babylonjs/core/Meshes/abstractMesh';
import type { Scene } from '@babylonjs/core/scene';

import {
  GROUND_BOUNCE,
  SHADOW_BACKOFF_M,
  SHADOW_FAR_M,
  SHADOW_MAP_SIZE,
  SHADOW_NEAR_M,
  SHADOW_RADIUS_M,
  SHADOW_SLAB_REACH_M,
  SKY_IRRADIANCE,
  SUN_DIRECTION,
  SUN_INTENSITY,
} from './sun';

/** Marks a placed mesh as one the sun's shadow map should draw. */
const CASTS_SUN = 'castsSun';

/** Whether a mesh has been marked by {@link AssetLighting.castShadows}. */
function isCaster(mesh: AbstractMesh): boolean {
  return (mesh.metadata as Record<string, unknown> | null)?.[CASTS_SUN] === true;
}

/**
 * What every lit surface in this scene needs: a sun, and an environment.
 *
 * <b>The SCENE's, not the asset mount's, and that distinction is load-bearing.</b> This used to be created inside
 * `mountAssets`, which is correct only as long as the published buildings are the one thing that is lit. The terrain is
 * a `PBRMaterial` now and takes its light from here too — and `mountAssets` is allowed to fail. A 404 on the catalogue,
 * a decoder that will not load, a schema this build does not read: all three are reported and swallowed, deliberately,
 * because none of them is a reason for the simulation to stop being watchable. With the sun owned by the mount, every
 * one of them would have left the desert pitch black, lit by nothing, on a run whose only fault was a missing GLB.
 *
 * So the app creates this once, beside the scene, and hands it to the mount. The mount uses it; it does not own it.
 *
 * <b>Image-based lighting is not a nicety here, it is a requirement of the asset contract.</b> Each GLB carries its own
 * baked ambient occlusion, which the glTF loader puts in `PBRMaterial.ambientTexture` — and that only attenuates
 * environment and ambient light. Under a sun alone, with `scene.environmentTexture` unset, every asset's AO is simply
 * invisible and the geometry reads flat. The same probe is the terrain's whole ambient: see
 * {@link createTerrainMaterial} for why the 0.66 constant that stood in for it is gone.
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

export class SceneLighting {
  readonly sun: DirectionalLight;
  private readonly scene: Scene;
  private readonly environment: CubeTexture;
  private generator: ShadowGenerator | null = null;
  /** Where {@link frameShadows} last put the slab, which {@link withinSlab} tests against. */
  private slabX = 0;
  private slabZ = 0;
  /** Whether anything is being rendered into the shadow map; see {@link setShadows} for why this is not a dispose. */
  private casting = false;
  /** The sky and the sand this world is lit by; see {@link setSky}. */
  private readonly sky = new Color3(1, 1, 1);
  private readonly bounce = new Color3(1, 1, 1);

  /**
   * @param scene The scene to light — terrain, assets and all. Its clear colour and image processing are left as they are.
   * @param vendorRoot The absolute URL of the vendored Babylon run-time folder, as `configureDecoders` returns it.
   */
  constructor(scene: Scene, vendorRoot: string) {
    this.scene = scene;
    this.environment = CubeTexture.CreateFromPrefilteredData(vendorRoot + ENVIRONMENT, scene);
    scene.environmentTexture = this.environment;
    scene.environmentIntensity = 1;
    // <b>The file's own harmonics arrive with the file, and they would overwrite ours.</b> `environmentTextureTools`
    // writes `_sphericalPolynomial` from the `.env` header as it parses, so a sky set before the load is simply lost.
    // Re-applying on load is the whole of the handshake; `setSky` before then records the colours and does nothing.
    this.environment.onLoadObservable.add(() => {
      this.applySky();
    });
    // <b>The scene's one sun</b> — see {@link SUN_DIRECTION} for why this is no longer a direction of its own. High
    // enough to light the walls, low enough that a facade and a roof are different brightnesses.
    this.sun = new DirectionalLight('sun', SUN_DIRECTION.clone(), scene);
    this.sun.intensity = SUN_INTENSITY;
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
   * Lights the world by the sky of the planet it is on, rather than by the studio the probe was baked in.
   *
   * <b>The diffuse half of the environment, replaced; the specular half, kept.</b> A prefiltered `.env` carries two
   * separable things: mip levels that a glossy surface reflects, and nine spherical-harmonic coefficients that are the
   * irradiance every diffuse surface integrates. The mips are generic studio light and that is fine — they are a
   * reflection, they are small, and nothing in a desert is a mirror. The harmonics are the SKY, and this world's sky is
   * not the probe's: its clear colour and its haze are warm dust, while the probe's harmonics are blue-dominant.
   *
   * Measured, straight out of the shipped file: a flat term of (0.547, 0.483, 0.621) and a sky term of
   * (0.21, 0.23, 0.41) — more blue than red in both. On a horizontal desert that supplied 69 % of the light. The sand
   * lost half its warmth (R − B fell from 48 to 24) and every shadow flattened (shadow/lit rose from 0.50 to 0.72).
   *
   * So the harmonics are rebuilt from the palette: the sky above, the sand's bounce below, and a smooth gradient
   * between. In the polynomial form Babylon evaluates — `irradiance(N) = zz + y·N.y` once the other coefficients are
   * zero — that is a flat term of the mean and a `y` term of half the difference. A surface facing up gets the sky, one
   * facing down gets the bounce, a wall gets the average. That is what an exterior ambient IS, and it costs nine
   * uniforms that were being uploaded anyway.
   *
   * <b>It makes the palette load-bearing.</b> The same three colours already decide the terrain's albedo, the clear
   * colour and the fog; now they decide the light as well, so an ice planet is lit by a cold sky and this one is not.
   *
   * @param sky The palette's sky, in sRGB as it is authored.
   * @param bounce What the ground is, for the light it throws back up — the palette's base colour.
   */
  setSky(sky: Color3, bounce: Color3): void {
    this.sky.copyFrom(sky);
    this.bounce.copyFrom(bounce);
    this.applySky();
  }

  /** Builds the harmonics from {@link sky} and {@link bounce}. Called on a palette change and on the probe's load. */
  private applySky(): void {
    // Linear, because irradiance is a quantity of light and the palette is a colour someone picked by eye. PBR
    // composes in linear and converts once at the end; handing it an sRGB triple here is the same mistake the terrain
    // albedo would make without `toLinearSpace`, and it washes out for the same reason.
    const up = this.sky.toLinearSpace().scale(SKY_IRRADIANCE);
    const down = this.bounce.toLinearSpace().scale(SKY_IRRADIANCE * GROUND_BOUNCE);
    const flat = up.add(down).scale(0.5);
    const gradient = up.subtract(down).scale(0.5);

    // A fresh polynomial rather than the loaded one edited in place: `preScaledHarmonics` memoises the harmonics it
    // derives, and the shader reads THOSE (`SPHERICAL_HARMONICS`). Mutating the coefficients of a polynomial that has
    // already been read changes nothing at all — measured, with an ambient set to pure red at twice full strength and
    // not one pixel moving.
    const harmonics = new SphericalPolynomial();
    harmonics.y.set(gradient.r, gradient.g, gradient.b);
    harmonics.xx.set(flat.r, flat.g, flat.b);
    harmonics.yy.set(flat.r, flat.g, flat.b);
    harmonics.zz.set(flat.r, flat.g, flat.b);
    this.environment.sphericalPolynomial = harmonics;
  }

  /**
   * Turns the sun's shadow on or off.
   *
   * <b>By emptying the caster list — never by disposing the generator, and never through `light.shadowEnabled`.</b>
   * Both of those change the DEFINES the receiving materials compiled against, and the published assets are the one
   * place where Babylon cannot carry that change through. A placement is an `InstancedMesh` of a mesh that lives in
   * an `AssetContainer` and is deliberately never added to the scene; `Material._markAllSubMeshesAsDirty` walks
   * `scene.meshes`, so it never reaches that source mesh's sub-meshes and their effect is never rebuilt. It keeps
   * `SHADOW0`, `SHADOWPCF0` and a `sampler2DShadow shadowTexture0` that nothing binds any more, and WebGL rejects
   * every draw that uses it.
   *
   * Measured, on the same band of ground, with `gl.getError()` polled every frame:
   *
   * | "off" implemented as        | frames with GL_INVALID_OPERATION |
   * |-----------------------------|----------------------------------|
   * | `generator.dispose()`       | 113 of 113                       |
   * | `light.shadowEnabled=false` | 115 of 115                       |
   * | an empty caster list        | 0 of 103                         |
   *
   * `scene.markAllMaterialsAsDirty` and marking each used material by hand were both tried and both measured at no
   * effect, for the reason above. An empty list leaves every define and every binding exactly as they were, so the
   * receivers sample a real depth texture that simply has nothing in it. The price is a depth-only pass over an
   * empty render list while shadows are off, which is a clear of the map and no draws.
   *
   * @param enabled Whether to cast.
   */
  setShadows(enabled: boolean): void {
    this.casting = enabled;
    if (!enabled || this.generator !== null) {
      return;
    }

    const generator = new ShadowGenerator(SHADOW_MAP_SIZE, this.sun);
    generator.useExponentialShadowMap = false;
    generator.useBlurExponentialShadowMap = false;
    // <b>PCF at QUALITY_HIGH, the same as the testbench's sun</b> (`render/lighting.ts:123-124`).
    //
    // This was impossible until the terrain stopped being a raw ShaderMaterial. PCF renders into a hardware depth
    // texture with a comparison mode, which a plain `texture2D` cannot read at all -- so while the ground sampled the
    // map by hand, asking for any real filter cost the whole desert its shadows, and the published buildings were
    // left with a hard aliased edge for the ground's sake. Measured, all three: PCF and contact hardening blanked the
    // desert, and Poisson kept the map readable but moired across flat stone.
    //
    // The terrain is a Babylon material now, so Babylon filters both and nothing reads the map by hand.
    generator.usePoissonSampling = false;
    generator.usePercentageCloserFiltering = true;
    generator.filteringQuality = ShadowGenerator.QUALITY_HIGH;
    generator.bias = 0.0005;
    // <b>Zero, because the terrain has no vertex normal to offset along.</b> `normalBias` moves the shadow lookup
    // along the surface normal in the VERTEX stage, and the quadtree mesh carries positions only — its normal is
    // sampled per pixel from the height field, which is far too late for this. A bias along a normal that does not
    // exist is an offset by whatever happens to be in that register.
    generator.normalBias = 0;

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
      // <b>And a distance test, because the slab is 64 m and the world is not.</b> A caster outside it cannot put a
      // pixel in the map however long it is kept in the list; it is pure per-frame cost in the list build and in the
      // depth pass. That was harmless while structures were drawn to 220 m, and it stopped being harmless when the
      // range went to 1 280: the list went from about 50 casters to 377. Measured either way the frame holds 60 fps
      // — 6.51 ms against 6.06 ms mean, arms overlapping — so this is not a rescue, it is declining to render a
      // kilometre of geometry into a map that cannot show it.
      map.renderListPredicate = (mesh) =>
        this.casting && mesh.isEnabled() && mesh.isVisible && isCaster(mesh) && this.withinSlab(mesh);
      map.renderList = [];
    }

    this.generator = generator;
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

    this.slabX = x;
    this.slabZ = z;

    // Back along the direction the light travels, which is where the light comes FROM.
    this.sun.position.set(
      x - (SUN_DIRECTION.x * SHADOW_BACKOFF_M),
      y - (SUN_DIRECTION.y * SHADOW_BACKOFF_M),
      z - (SUN_DIRECTION.z * SHADOW_BACKOFF_M),
    );
  }

  /**
   * Whether a mesh is near enough to the shadow slab to put anything in it.
   *
   * Generous by a mesh's own bounding radius plus a margin, so a long wall whose origin is outside the slab but whose
   * body crosses it still casts. Reading the bounding sphere costs nothing here: Babylon keeps it on the mesh.
   *
   * @param mesh The candidate caster.
   * @returns Whether it can reach the slab.
   */
  private withinSlab(mesh: AbstractMesh): boolean {
    const centre = mesh.getBoundingInfo().boundingSphere.centerWorld;
    const dx = centre.x - this.slabX;
    const dz = centre.z - this.slabZ;
    const reach = SHADOW_SLAB_REACH_M + mesh.getBoundingInfo().boundingSphere.radiusWorld;
    return dx * dx + dz * dz <= reach * reach;
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
    this.environment.dispose();
    this.scene.environmentTexture = null;
  }
}
