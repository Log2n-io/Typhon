import type { Material } from '@babylonjs/core/Materials/material';
import { MaterialPluginBase } from '@babylonjs/core/Materials/materialPluginBase';
import { PBRMaterial } from '@babylonjs/core/Materials/PBR/pbrMaterial';
import { ShadowDepthWrapper } from '@babylonjs/core/Materials/shadowDepthWrapper';
import type { UniformBuffer } from '@babylonjs/core/Materials/uniformBuffer';
import type { Scene } from '@babylonjs/core/scene';

import type { CrowdRig } from './crowd-rig';

/**
 * The crowd's skinning, as a plugin on a PBR material: each vertex blends four bone matrices read from the baked
 * atlas, for the body's clip A and — while a crossfade runs — clip B, weighted by the fade.
 *
 * It replaces both Babylon's skeleton skinning (one skeleton per character, animated on the CPU) and its
 * `BakedVertexAnimationManager` (one clip per instance, no blending), so a crowd mesh carries no skeleton at all.
 *
 * Per vertex: `crowdBones`, `crowdWeights`. Per instance: `crowdClipA`, `crowdClipB` (start row, frame count —
 * negative for a one-shot that holds — start time, rate) and `crowdFade` (start time, 1 / duration). <b>The frame
 * formula is `frameOf` in `character/crowd-anim.ts`; the two must stay identical.</b>
 *
 * Blending is of the two POSES' matrices, linearly — which is what a short crossfade between gait cycles needs, and
 * costs a second set of fetches only while a fade actually runs: sixteen texel fetches per vertex normally,
 * thirty-two mid-fade.
 */
export class CrowdSkinPlugin extends MaterialPluginBase {
  /** Seconds since the crowd started. Not epoch seconds: float32 loses milliseconds there. */
  time = 0;
  private readonly rig: CrowdRig;

  constructor(material: Material, rig: CrowdRig) {
    super(material, 'CrowdSkin', 200, { CROWDSKIN: false });
    this.rig = rig;
    this._enable(true);
  }

  override getClassName(): string {
    return 'CrowdSkinPlugin';
  }

  override prepareDefines(defines: Record<string, unknown>): void {
    defines['CROWDSKIN'] = true;
  }

  override getAttributes(attributes: string[]): void {
    attributes.push('crowdBones', 'crowdWeights', 'crowdClipA', 'crowdClipB', 'crowdFade');
  }

  override getSamplers(samplers: string[]): void {
    samplers.push('crowdVat');
  }

  override getUniforms(): { ubo: { name: string; size: number; type: string }[] } {
    return {
      ubo: [
        { name: 'crowdTime', size: 1, type: 'float' },
        { name: 'crowdFps', size: 1, type: 'float' },
      ],
    };
  }

  override bindForSubMesh(uniformBuffer: UniformBuffer): void {
    uniformBuffer.updateFloat('crowdTime', this.time);
    uniformBuffer.updateFloat('crowdFps', this.rig.fps);
    uniformBuffer.setTexture('crowdVat', this.rig.texture);
  }

  override getCustomCode(shaderType: string): Record<string, string> | null {
    if (shaderType !== 'vertex') {
      return null;
    }

    return {
      CUSTOM_VERTEX_DEFINITIONS: `
#ifdef CROWDSKIN
uniform highp sampler2D crowdVat;
attribute vec4 crowdBones;
attribute vec4 crowdWeights;
attribute vec4 crowdClipA;
attribute vec4 crowdClipB;
attribute vec2 crowdFade;
// Mirrors frameOf() in character/crowd-anim.ts. GLSL mod and JS % differ on negatives; the max(0.0) excludes them.
float crowdFrame(vec4 c) {
  float n = abs(c.y);
  float f = max(0.0, (crowdTime - c.z) * crowdFps * c.w);
  f = c.y > 0.0 ? mod(f, n) : min(f, n - 1.0);
  return c.x + floor(f);
}
mat4 crowdBone(float bone, float frame) {
  ivec2 p = ivec2(int(bone) * 4, int(frame));
  return mat4(texelFetch(crowdVat, p, 0), texelFetch(crowdVat, p + ivec2(1, 0), 0),
              texelFetch(crowdVat, p + ivec2(2, 0), 0), texelFetch(crowdVat, p + ivec2(3, 0), 0));
}
mat4 crowdPose(float frame) {
  return crowdBone(crowdBones.x, frame) * crowdWeights.x + crowdBone(crowdBones.y, frame) * crowdWeights.y
       + crowdBone(crowdBones.z, frame) * crowdWeights.z + crowdBone(crowdBones.w, frame) * crowdWeights.w;
}
#endif
`,
      CUSTOM_VERTEX_UPDATE_POSITION: `
#ifdef CROWDSKIN
float crowdW = clamp((crowdTime - crowdFade.x) * crowdFade.y, 0.0, 1.0);
mat4 crowdM = crowdPose(crowdFrame(crowdClipA));
if (crowdW < 1.0) {
  crowdM = crowdM * crowdW + crowdPose(crowdFrame(crowdClipB)) * (1.0 - crowdW);
}
positionUpdated = (crowdM * vec4(positionUpdated, 1.0)).xyz;
#endif
`,
      // <b>Renormalised, unlike the spike.</b> A linear blend of two rotation matrices is shorter than either, so
      // mid-crossfade every normal was a little short and every body shaded a little dark for a quarter of a second
      // on each gait change. Harmless where the crowd was the only thing on screen; not harmless in a scene with one
      // light transport, where a character is meant to be lit like the wall behind it.
      CUSTOM_VERTEX_UPDATE_NORMAL: `
#if defined(CROWDSKIN) && defined(NORMAL)
normalUpdated = normalize(mat3(crowdM) * normalUpdated);
#endif
`,
    };
  }
}

/**
 * The one material every crowd outfit shares.
 *
 * <b>It must meet this client's light contract</b> (`client.md` §16), which the spike never had to: a `PBRMaterial`,
 * lit by the one sun and `scene.environmentTexture`, composing in linear.
 *
 * <b>Vertex colours are NOT linearised here, and that is a real difference from the terrain.</b> The terrain's palette
 * is authored in sRGB and must be converted; these colours come from glTF `baseColorFactor`, which the specification
 * already defines as linear. Babylon multiplies `surfaceAlbedo` by `vColor.rgb` with no conversion, which is correct.
 * Converting them "for consistency" would brighten every character against every building.
 *
 * <b>And it casts.</b> The depth pass uses its OWN vertex shader, so without a {@link ShadowDepthWrapper} the crowd
 * would cast bind-pose shadows that never move — the one part of this the spike could not have found, because it
 * never ran with a shadow generator at all. The wrapper rebuilds the depth effect from this material's vertex shader,
 * plugin injection included, so the shadow is cast by the pose that is drawn.
 *
 * @param scene The live scene.
 * @param rig The baked atlas.
 * @returns The material and its skinning plugin.
 */
export function createCrowdMaterial(scene: Scene, rig: CrowdRig): { material: PBRMaterial; skin: CrowdSkinPlugin } {
  const material = new PBRMaterial('crowd', scene);
  // White: the albedo is the outfit's own vertex colours, and a tint here would multiply them twice.
  material.albedoColor.set(1, 1, 1);
  material.metallic = 0;
  // Cloth and skin, not sand: a little tighter than the terrain's 1.0, which is flat by intent.
  material.roughness = 0.6;
  // Every material in the pack is doubleSided, and a crowd seen from both sides at LOD2 has thin geometry.
  material.backFaceCulling = false;
  material.shadowDepthWrapper = new ShadowDepthWrapper(material, scene);

  const skin = new CrowdSkinPlugin(material, rig);
  return { material, skin };
}
