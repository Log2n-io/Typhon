import { describe, expect, it } from 'vitest';

import {
  DAMAGE_FRAGMENT,
  DAMAGE_VERTEX,
  ENTITY_FRAGMENT,
  ENTITY_VERTEX,
  LINE_FRAGMENT,
  LINE_VERTEX,
  ROOM_FRAGMENT,
  ROOM_VERTEX,
  SPACE_FRAGMENT,
  SPACE_VERTEX,
  SPRITE_FRAGMENT,
  SPRITE_VERTEX,
  GROUND_ALBEDO,
  GROUND_HELPER_FUNCTIONS,
  GROUND_OVERLAY_UNIFORMS,
  HEIGHT_FUNCTIONS,
} from '../src/render/shaders';

/**
 * Every shader declares the uniforms and varyings it reads.
 *
 * <b>This exists because a broken ground shader is invisible in every other check this project runs.</b> A text splice
 * into `GROUND_FRAGMENT` cut `main()` and the declarations above it out of the file; typecheck passed, 619 tests
 * passed, lint passed, and the terrain simply stopped being drawn — the material reported `hasEffect: false`, and
 * nothing in the suite looks at a material. In game it read as the character flying, because the ground it was
 * correctly standing on was not rendered, and hours went into measuring collision numbers that were right all along.
 *
 * GLSL cannot be compiled under vitest, so this checks the property a splice destroys and a driver reports first:
 * every `uXxx` and `vXxx` the body reads is declared in the same source. The names are a convention this codebase
 * keeps without exception — `u` for a uniform, `v` for a varying — which is what makes the check both narrow and
 * total. The driver's own words for the failure were `'vPlanet' : undeclared identifier`.
 */

const SHADERS: ReadonlyArray<readonly [string, string]> = [
  ['ENTITY_VERTEX', ENTITY_VERTEX],
  ['ENTITY_FRAGMENT', ENTITY_FRAGMENT],
  ['SPRITE_VERTEX', SPRITE_VERTEX],
  ['SPRITE_FRAGMENT', SPRITE_FRAGMENT],
  ['LINE_VERTEX', LINE_VERTEX],
  ['LINE_FRAGMENT', LINE_FRAGMENT],
  ['DAMAGE_VERTEX', DAMAGE_VERTEX],
  ['DAMAGE_FRAGMENT', DAMAGE_FRAGMENT],
  ['ROOM_VERTEX', ROOM_VERTEX],
  ['ROOM_FRAGMENT', ROOM_FRAGMENT],
  ['SPACE_VERTEX', SPACE_VERTEX],
  ['SPACE_FRAGMENT', SPACE_FRAGMENT],
];

/** The source with comments and preprocessor lines taken out, so a name in prose is not a use. */
function code(src: string): string {
  return src
    .replace(/\/\*[\s\S]*?\*\//g, ' ')
    .replace(/\/\/[^\n]*/g, ' ')
    .replace(/^\s*#.*$/gm, ' ');
}

/** The `uXxx` / `vXxx` names a shader DECLARES, as a uniform, attribute or varying. */
function declared(src: string): Set<string> {
  const names = new Set<string>();
  const decl =
    /\b(?:uniform|attribute|varying|in|out)\s+(?:lowp\s+|mediump\s+|highp\s+)?[A-Za-z_]\w*\s+([uv][A-Z]\w*)/g;
  for (const m of code(src).matchAll(decl)) {
    names.add(m[1]);
  }

  return names;
}

/** The `uXxx` / `vXxx` names a shader READS. */
function used(src: string): Set<string> {
  const names = new Set<string>();
  for (const m of code(src).matchAll(/\b([uv][A-Z]\w*)\b/g)) {
    names.add(m[1]);
  }

  return names;
}

describe('every shader declares what it reads', () => {
  for (const [name, src] of SHADERS) {
    it(`${name} declares every uniform and varying it uses`, () => {
      const have = declared(src);
      const missing = [...used(src)].filter((id) => !have.has(id)).sort();
      expect(missing, `${name} reads names it does not declare`).toEqual([]);
    });

    it(`${name} has an entry point`, () => {
      // Both spellings are in use across these twelve; what matters is that there IS one.
      expect(/void\s+main\s*\(\s*(?:void)?\s*\)/.test(src), `${name} has no main()`).toBe(true);
    });
  }

  it('the ground albedo still carries the parts a splice removed', () => {
    // Named one by one, so a failure says which piece went missing rather than that something did. The ground is a
    // material plugin now, so these are the shared strings it injects rather than a shader of its own.
    //
    // <b>And they are the strings the plugin ACTUALLY injects.</b> This used to assert against `GROUND_HELPERS` and a
    // `GROUND_OVERLAY_UNIFORMS` that still declared its own samplers — two blocks left over from the ground's own
    // `ShaderMaterial`, kept compiling by nothing, while the material injected near-identical copies beside them. A
    // splice into the copies would have passed here. There is one of each now, and this names it.
    expect(HEIGHT_FUNCTIONS, 'the height lookup').toContain('float heightAt(');
    expect(HEIGHT_FUNCTIONS, 'the ground-normal helper').toContain('vec3 groundNormal(');
    expect(GROUND_HELPER_FUNCTIONS, 'the noise the albedo calls').toContain('float noise(');
    expect(GROUND_OVERLAY_UNIFORMS, 'the palette uniforms').toContain('uniform vec3 uPalette0');
    expect(GROUND_ALBEDO, 'the albedo itself').toContain('vec3 color');
  });

  it('declares no sampler that the material plugin also declares', () => {
    // Babylon assigns a texture unit to each name in `getSamplers` and declares it; a second declaration of the same
    // name is a redefinition the driver rejects, and skipping `getSamplers` to dodge that leaves the sampler with no
    // unit at all — which is how the terrain's per-pixel normal became NaN and the whole planet rendered black. These
    // shared blocks are injected into a plugin, so a `sampler2D` in any of them is that bug coming back.
    for (const [name, src] of [
      ['HEIGHT_FUNCTIONS', HEIGHT_FUNCTIONS],
      ['GROUND_HELPER_FUNCTIONS', GROUND_HELPER_FUNCTIONS],
      ['GROUND_OVERLAY_UNIFORMS', GROUND_OVERLAY_UNIFORMS],
    ] as const) {
      expect(src, `${name} declares a sampler the plugin must own`).not.toContain('uniform sampler2D');
    }
  });
});
