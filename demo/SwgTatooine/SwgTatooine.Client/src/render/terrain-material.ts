import { MaterialPluginBase } from '@babylonjs/core/Materials/materialPluginBase';
import { ShaderLanguage } from '@babylonjs/core/Materials/shaderLanguage';
import { PBRMaterial } from '@babylonjs/core/Materials/PBR/pbrMaterial';
import type { BaseTexture } from '@babylonjs/core/Materials/Textures/baseTexture';
import type { Effect } from '@babylonjs/core/Materials/effect';
import type { UniformBuffer } from '@babylonjs/core/Materials/uniformBuffer';
import { Color3 } from '@babylonjs/core/Maths/math.color';
import { Vector2, Vector3, Vector4 } from '@babylonjs/core/Maths/math.vector';
import type { AbstractEngine } from '@babylonjs/core/Engines/abstractEngine';
import type { SubMesh } from '@babylonjs/core/Meshes/subMesh';
import type { Scene } from '@babylonjs/core/scene';

import {
  GROUND_ALBEDO,
  GROUND_HELPER_FUNCTIONS,
  GROUND_OVERLAY_UNIFORMS,
  HEIGHT_FUNCTIONS,
  HEIGHT_UNIFORMS,
} from './shaders';
import { NODE_GRID } from './terrain-quadtree';

/**
 * The terrain as one of Babylon's own materials, so that Babylon lights it and shadows it.
 *
 * <b>This exists because a raw `ShaderMaterial` cannot receive a shadow.</b> Nothing injects the shadow includes into
 * one, so the terrain had to sample the shadow map by hand — and that forced the generator to store a format the hand
 * written sampler could read, which rules out every filter worth having. Measured in game, all three: PCF and contact
 * hardening put a comparison mode on the map that a plain `texture2D` cannot read at all and the whole desert loses
 * its shadows; Poisson keeps the float map but moires across flat stone. Meanwhile the published buildings, which ARE
 * Babylon materials, were left with a hard aliased edge because the generator had to stay unfiltered for the ground's
 * sake. One map cannot serve a custom sampler and Babylon's own receivers at once.
 *
 * The testbench has no such split, and that is the whole lesson: every one of its grounds is a `PBRMaterial`
 * (`modes/demo-mode.ts:201-207`, `viewer-mode.ts:65-70`, `crowd-mode.ts:142-155`) and its sun runs PCF at
 * `QUALITY_HIGH` over all of them. `grep ShaderMaterial` across it returns nothing.
 *
 * So the terrain moves onto a `PBRMaterial` — <b>the same material class as every published building</b>, which is the
 * other half of the lesson and the reason this is not a `StandardMaterial`. Two materials can both receive a shadow and
 * still disagree about everything else: a standard material lit by one directional light is black wherever that light
 * does not reach, so the port's first version carried the old shader's `0.66 + 0.5 * lambert` as a scene ambient and a
 * half-grey diffuse. That is a constant standing in for the sky, and it matched the buildings — which take their sky
 * from `scene.environmentTexture` — only by eye, at one time of day, under one palette. Change the environment, the
 * exposure or the planet and the desert and the walls drift apart with nothing tying them together.
 *
 * One material class, one sun, one environment. The 0.66 and the 0.5 are gone; the sky the buildings stand under is the
 * sky the sand stands under, because it is the same irradiance probe.
 *
 * The look is kept through injection points that Babylon documents and this file verifies against the installed shader
 * source (`node_modules/@babylonjs/core/Shaders/pbr.{vertex,fragment}.js`):
 *
 * - <b>`CUSTOM_VERTEX_UPDATE_POSITION`</b> sets `positionUpdated`, and sits BEFORE `#include<instancesVertex>` — which
 *   is what lets the quadtree node's planet coordinate and its height replace the unit grid's. Identical in both
 *   materials.
 * - <b>`CUSTOM_FRAGMENT_BEFORE_LIGHTS`</b> sets `normalW` AND `surfaceAlbedo`. In `pbr.fragment` it sits after
 *   `pbrBlockNormalFinal` and after `surfaceAlbedo` is unpacked from `albedoOpacityBlock`, and before
 *   `pbrBlockGeometryInfo`, the reflection block and the lights — so both writes land where everything downstream reads
 *   them, and in `main` rather than inside a `pbr_inline` function.
 *
 * <b>The albedo is converted to linear, and that is not cosmetic.</b> A standard material composes in whatever space it
 * is handed and writes `gl_FragColor` straight out; PBR works in linear and ends with `toGammaSpace`, via
 * `applyImageProcessing`. The palette is authored as sRGB, so handing it over raw brightens the whole desert and breaks
 * the match with the assets, whose own albedo textures the glTF loader marks `GAMMAALBEDO` and linearises for exactly
 * this reason.
 *
 * <b>The environment reaches the terrain even though the mesh has no normals.</b> Babylon would rather compute the
 * irradiance per vertex (`USESPHERICALINVERTEX`), and the vertex block that does it is inside `#ifdef NORMAL`. The
 * fragment path is guarded `!defined(NORMAL) || !defined(USESPHERICALINVERTEX)` (`pbrBlockReflection`), so a mesh
 * without a normal attribute takes it automatically, from `normalW` — the one this plugin writes.
 *
 * <b>Nothing new is installed for this.</b> `MaterialPluginBase` and `PBRMaterial` are both in `@babylonjs/core`;
 * `@babylonjs/materials`, which carries `CustomMaterial` and `TerrainMaterial`, is not a dependency and does not need
 * to become one.
 */

/**
 * The attributes the quadtree feeds per node, which Babylon must be told about or they are not bound.
 *
 * `nodeData` is the node's planet origin, its size in metres and a spare; `nodeMorph` is the camera distance over
 * which the vertex blends onto its parent's grid. Both are thin-instance buffers written every frame by
 * {@link TerrainMesh}.
 */
const NODE_ATTRIBUTES = ['nodeData', 'nodeMorph'];

/**
 * The quadtree's terrain, injected into Babylon's standard material.
 *
 * <b>Step one of the port, deliberately minimal.</b> It carries the vertex half — the node placement, the height
 * lookup and the CDLOD morph, which is the part that cannot be approximated — and a flat albedo. The 310 lines of
 * palette, overlays, city discs, heatmap, replication grid and selection rings follow once this foundation is proven
 * to draw in the right place and to receive a shadow. Porting them first would have been 310 lines resting on an
 * assumption about thin instances.
 */
export class TerrainMaterialPlugin extends MaterialPluginBase {
  /** The camera in planet metres (xz) and its altitude (y): what the morph measures its distance against. */
  readonly cameraPlanet = new Vector3();

  /** `originM`, `spacingM`, `posts`, `1 / posts` — what the height lookup needs to index the field. */
  readonly field = new Vector4(0, 1, 1, 1);

  /** The baked height field, as a texture. */
  heightTexture: BaseTexture | null = null;

  constructor(material: PBRMaterial) {
    super(material, 'QuadtreeTerrain', 200, { QUADTREE_TERRAIN: true });
    this._enable(true);
  }

  /**
   * Hands the frame's values to the shader.
   *
   * <b>Through the plugin, not through the material.</b> A `PBRMaterial` has no `setVector3`: its uniforms are a
   * fixed set, and anything a plugin adds is its own to bind. This is called once per sub-mesh per frame.
   */
  override bindForSubMesh(_uniformBuffer: UniformBuffer, _scene: Scene, _engine: AbstractEngine, subMesh: SubMesh): void {
    const effect = subMesh.effect;
    if (effect === null) {
      return;
    }

    effect.setVector3('uCameraPlanet', this.cameraPlanet);
    effect.setVector4('uField', this.field);
    if (this.heightTexture !== null) {
      effect.setTexture('uHeight', this.heightTexture);
    }

    for (const apply of this.values.values()) {
      apply(effect);
    }
  }

  override isCompatible(shaderLanguage: ShaderLanguage): boolean {
    return shaderLanguage === ShaderLanguage.GLSL;
  }

  override getClassName(): string {
    return 'TerrainMaterialPlugin';
  }

  override getAttributes(attributes: string[]): void {
    attributes.push(...NODE_ATTRIBUTES);
  }

  /**
   * The uniform names this plugin's own GLSL declares.
   *
   * <b>Without this, every `effect.setX()` below is a silent no-op, and that is the whole bug.</b> An `Effect` only
   * resolves a location for a name Babylon put in its uniform list, and the ONLY source of a plugin's names is this
   * method (`materialPluginManager.pure.js:206`). A name it never hears about gets no location, so `setVector4`
   * reaches `thinEngine`'s `if (!uniform) return false` and returns having done nothing — no error, no warning.
   *
   * It compiled and drew, which is what made it so expensive to find: `uField` stayed `vec4(0)`, so the height
   * lookup's post spacing was zero, `groundNormal()` normalised `vec3(0, 0, 0)`, and the per-pixel normal came out
   * NaN. A NaN normal takes `info.diffuse` to zero, which multiplies the correctly-computed shadow factor by zero —
   * and the terrain painted the whole viewport black, sky included. Measured: hiding the mesh restored a 161-level
   * sky, showing it returned a single luminance of 0.
   *
   * `externalUniforms` is the list for exactly this case — declared in the injected code rather than by Babylon, but
   * still needing a location. Naming one the compiler stripped as unused is harmless: it gets no location and the
   * setter returns early, which is the behaviour this method exists to make deliberate rather than accidental.
   */
  override getUniforms(): { externalUniforms: string[] } {
    return {
      externalUniforms: [
        // The vertex half: where the camera is, and how to index the height field.
        'uCameraPlanet',
        'uField',
        // The albedo's overlays. Declared in the shared strings, so they are this plugin's to register.
        'uCities',
        'uPois',
        'uHeatRect',
        'uHeatMask',
        'uHeatNear',
        'uViewCenter',
        'uHeatAlpha',
        'uGrid',
        'uSelection',
        'uCellRect',
        'uCellAlpha',
        'uReplGrid',
        'uPalette0',
        'uPalette1',
        'uPalette2',
        'uHull',
        'uHullCount',
        'uSessionDisc',
      ],
    };
  }

  override getSamplers(samplers: string[]): void {
    // <b>Babylon declares these and assigns each a texture unit; nothing else may declare them.</b> Leaving this
    // empty to dodge a redefinition was the wrong half of the trade: the samplers then had no unit, the FRAGMENT
    // stage read garbage from `uHeight`, the per-pixel normal came out NaN and the entire planet rendered black.
    // The shared declaration strings are injected in their sampler-free form for exactly this reason.
    samplers.push('uHeight', 'uHeat', 'uCells');
  }

  /**
   * The values this frame, by uniform name, replayed onto the effect when Babylon binds the sub-mesh.
   *
   * <b>Not a uniform buffer, deliberately.</b> Everything the terrain reads is already declared as a plain `uniform`
   * in the strings it shares with the raw shader, and a UBO entry of the same name is a redefinition the driver
   * rejects — that is what `uField` did on the first attempt. Recording the values and setting them on the effect
   * keeps ONE declaration of each, which is the thing that went wrong.
   */
  private readonly values = new Map<string, (effect: Effect) => void>();

  /** Records a scalar for the next bind. */
  setFloat(name: string, value: number): void {
    this.values.set(name, (e) => {
      e.setFloat(name, value);
    });
  }

  /** Records a vector for the next bind; the vector is copied, because the caller reuses its own. */
  setVector2(name: string, value: Vector2): void {
    const v = value.clone();
    this.values.set(name, (e) => {
      e.setVector2(name, v);
    });
  }

  setVector3(name: string, value: Vector3): void {
    const v = value.clone();
    this.values.set(name, (e) => {
      e.setVector3(name, v);
    });
  }

  setVector4(name: string, value: Vector4): void {
    const v = value.clone();
    this.values.set(name, (e) => {
      e.setVector4(name, v);
    });
  }

  setColor3(name: string, value: Color3): void {
    const v = value.clone();
    this.values.set(name, (e) => {
      e.setColor3(name, v);
    });
  }

  /** Records a flat array of vec4s — the place tables and the session's hull. */
  setArray4(name: string, value: readonly number[]): void {
    const v = [...value];
    this.values.set(name, (e) => {
      e.setArray4(name, v);
    });
  }

  setTexture(name: string, value: BaseTexture): void {
    this.values.set(name, (e) => {
      e.setTexture(name, value);
    });
  }

  override getCustomCode(shaderType: string): Record<string, string> | null {
    if (shaderType === 'vertex') {
      return {
        CUSTOM_VERTEX_DEFINITIONS: `
          attribute vec4 nodeData;
          attribute vec2 nodeMorph;
          // Declared here because getSamplers reaches the FRAGMENT stage only, and the height field is read
          // in the vertex shader: that is where a node's grid becomes a place on the planet.
          // The VERTEX stage must declare the height sampler itself: getSamplers reaches the fragment stage only.
          // The two stages are separate shaders, so this is not the redefinition that broke the fragment — declaring
          // it in BOTH is correct, and declaring it in NEITHER left the fragment reading garbage.
          uniform sampler2D uHeight;
          uniform vec4 uField;
          uniform vec3 uCameraPlanet;
          varying vec2 vPlanet;
          varying float vHeightM;
          ${HEIGHT_FUNCTIONS}
        `,
        // Before `#include<instancesVertex>`, which is why writing `positionUpdated` here is the right hook: the node's
        // own planet coordinate replaces the unit grid's, and whatever instance transform follows still applies.
        CUSTOM_VERTEX_UPDATE_POSITION: `
          vec2 grid = positionUpdated.xz;
          vec2 planet = nodeData.xy + grid * nodeData.z;

          // CDLOD morph: approaching the distance at which this node is replaced by its four children, blend each
          // vertex onto the grid its PARENT has, so the swap moves nothing. This is what closes the T-junction at a
          // level boundary without a stitching strip or a skirt.
          float toCamera = distance(vec3(planet.x, heightAt(planet), planet.y), uCameraPlanet);
          float morph = clamp((toCamera - nodeMorph.x) / max(nodeMorph.y - nodeMorph.x, 1e-3), 0.0, 1.0);
          vec2 coarse = floor(grid * ${NODE_GRID.toFixed(1)} * 0.5 + 0.5) * 2.0 / ${NODE_GRID.toFixed(1)};
          planet = nodeData.xy + mix(grid, coarse, morph) * nodeData.z;

          float nodeHeight = heightAt(planet);
          vPlanet = planet;
          vHeightM = nodeHeight;
          positionUpdated = vec3(planet.x, nodeHeight, planet.y);
        `,
      };
    }

    return {
      CUSTOM_FRAGMENT_DEFINITIONS: `
        varying vec2 vPlanet;
        varying float vHeightM;
        // <b>Declared once per stage, and registered in getSamplers.</b> The two jobs are separate: getSamplers tells
        // Babylon to assign a texture unit and makes effect.setTexture resolve, while the DECLARATION has to be
        // here. Getting this wrong either way costs a whole render: declaring twice is a redefinition the driver
        // rejects, and declaring nowhere leaves the fragment sampling garbage, which turned the terrain's per-pixel
        // normal into NaN and rendered the planet black.
        uniform sampler2D uHeight;
        uniform sampler2D uHeat;
        uniform sampler2D uCells;
        ${GROUND_OVERLAY_UNIFORMS}
        ${HEIGHT_UNIFORMS}
        ${HEIGHT_FUNCTIONS}
        ${GROUND_HELPER_FUNCTIONS}
      `,
      // <b>The surface normal and the albedo, in that order, both in `main`.</b>
      //
      // The normal first, per pixel, from the height field. The quadtree mesh carries POSITIONS ONLY: no normal
      // attribute, deliberately, because a per-vertex normal interpolated across triangles tens to hundreds of metres
      // wide crawls as the mesh re-tessellates. Babylon's fallback for a mesh without normals is the screen-space
      // derivative of the world position, which here left the sun contributing nothing at all - the terrain rendered as
      // pure ambient times albedo, and a shadow had no directional term to take away. That is why the ground looked lit
      // but never shadowed. `geometricNormalW` is the copy `pbrBlockNormalGeometric` took before this hook; only the
      // clearcoat block reads it, which the terrain has none of, and it costs one assignment to not leave it stale.
      //
      // Then the albedo, braced because it computes into a `vec3 color` and `main` declares a `vec4 color` of its own
      // further down. `surfaceAlbedo` is PBR's own variable, unpacked from `albedoOpacityBlock` a few lines above this
      // hook, and everything after it - the reflection block's irradiance, the direct lighting, the shadow - multiplies
      // into it. Writing it HERE rather than at `CUSTOM_FRAGMENT_UPDATE_ALBEDO` keeps the injection in `main`: that
      // point sits inside `albedoOpacityBlock`, a function the shader processor inlines, and nothing about this code
      // needs to be there.
      //
      // `toLinearSpace` comes from `helperFunctions`, which `pbr.fragment` includes. See this file's header for why the
      // conversion is load-bearing rather than a nicety.
      CUSTOM_FRAGMENT_BEFORE_LIGHTS: `
        normalW = groundNormal(vPlanet);
        geometricNormalW = normalW;
        {
          ${GROUND_ALBEDO}
          surfaceAlbedo = toLinearSpace(color);
        }
      `,
    };
  }
}

/**
 * A material for the quadtree terrain that Babylon lights, fogs and shadows.
 *
 * @param scene The live scene.
 * @returns The material and the plugin, whose fields carry the per-frame values.
 */
export function createTerrainMaterial(scene: Scene): { material: PBRMaterial; plugin: TerrainMaterialPlugin } {
  const material = new PBRMaterial('terrain', scene);
  // <b>Sand: a rough dielectric, and nothing else.</b> Metallic 0 and roughness 1 are what make this a diffuse surface
  // with no highlight sliding over a 16 km plane, which is the most obvious artefact on screen if the defaults are left
  // alone. The 4 % dielectric reflectance PBR keeps underneath stays, because the buildings have it too and it is part
  // of what makes them look like they are under the same sky.
  material.metallic = 0;
  material.roughness = 1;
  // White, because the albedo is written per pixel by the plugin. A tint here would multiply the palette twice.
  material.albedoColor = Color3.White();
  // <b>No ambient constant, and that is the whole point.</b> `vAmbientColor` is `scene.ambientColor` times this
  // material's, and PBR's default for the second is black - so the 0.66 the old shader carried reached the terrain and
  // never reached a building. The sky is `scene.environmentTexture` now, for both, which is what lets
  // `scene.ambientColor` be deleted outright; see `ground.ts`.
  //
  // Fog is the scene's too (`scene.fogMode`/`fogColor`/`fogDensity`, applied by `pbr.fragment`): the old shader mixed
  // towards the sky colour itself over a density it was handed each frame, and doing it in the albedo as well would be
  // a second haze over the first.
  material.backFaceCulling = false;
  return { material, plugin: new TerrainMaterialPlugin(material) };
}
