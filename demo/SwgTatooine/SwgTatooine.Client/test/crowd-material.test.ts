import { describe, expect, it } from 'vitest';
import { CROWD_UNIFORMS_DECLARATION, CrowdSkinPlugin } from '../src/render/crowd-material';

/**
 * The crowd plugin declares its uniforms for both of Babylon's paths. With a uniform buffer they are members of it; without
 * one — a GPU or driver whose WebGL2 context reports no uniform buffers — the PBR shader declares only what the plugin's
 * `vertex` string carries, and a uniform missing from it is an undeclared identifier in every crowd shader and every
 * fallback (2026-10-25, a LAN machine). Reproduced live with `engine.disableUniformBuffers`.
 */
describe('CrowdSkinPlugin.getUniforms', () => {
  // getUniforms reads nothing of the instance: called on the prototype, it needs no material, scene or engine.
  const uniforms = CrowdSkinPlugin.prototype.getUniforms.call(null as unknown as CrowdSkinPlugin);

  it('declares every uniform-buffer member as a plain uniform too, for an engine without uniform buffers', () => {
    expect(uniforms.ubo.length).toBeGreaterThan(0);
    for (const u of uniforms.ubo) {
      expect(uniforms.vertex, u.name).toMatch(new RegExp(`uniform\\s+${u.type}\\s+${u.name}\\s*;`));
    }
  });

  it('guards the declarations by the plugin define, as its code is', () => {
    expect(CROWD_UNIFORMS_DECLARATION.trim().startsWith('#ifdef CROWDSKIN')).toBe(true);
    expect(CROWD_UNIFORMS_DECLARATION.trim().endsWith('#endif')).toBe(true);
  });

  it('declares every crowd uniform its vertex code reads', () => {
    const code = Object.values(
      CrowdSkinPlugin.prototype.getCustomCode.call(null as unknown as CrowdSkinPlugin, 'vertex') ?? {},
    ).join('\n');
    const declared = new Set(uniforms.ubo.map((u) => u.name));
    for (const name of new Set(code.match(/\bcrowd[A-Z][A-Za-z]*\b/g) ?? [])) {
      // Attributes, the sampler, functions and locals are declared in the code itself, after a type; a uniform the code
      // only reads is declared by getUniforms.
      const inCode = new RegExp(`\\b(?:float|int|bool|void|[ib]?vec[234]|mat[234]|sampler2D)\\s+${name}\\b`).test(code);
      expect(inCode || declared.has(name), name).toBe(true);
    }
  });
});
