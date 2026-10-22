import { describe, expect, it } from 'vitest';

import {
  ENTITY_FRAGMENT,
  ENTITY_VERTEX,
  GROUND_FRAGMENT,
  GROUND_VERTEX,
  LINE_FRAGMENT,
  LINE_VERTEX,
  ROOM_FRAGMENT,
  ROOM_VERTEX,
  SPACE_FRAGMENT,
  SPACE_VERTEX,
  SPRITE_FRAGMENT,
  SPRITE_VERTEX,
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
  ['GROUND_VERTEX', GROUND_VERTEX],
  ['GROUND_FRAGMENT', GROUND_FRAGMENT],
  ['ENTITY_VERTEX', ENTITY_VERTEX],
  ['ENTITY_FRAGMENT', ENTITY_FRAGMENT],
  ['SPRITE_VERTEX', SPRITE_VERTEX],
  ['SPRITE_FRAGMENT', SPRITE_FRAGMENT],
  ['LINE_VERTEX', LINE_VERTEX],
  ['LINE_FRAGMENT', LINE_FRAGMENT],
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

  it('the ground fragment still carries the parts a splice removed', () => {
    // Named one by one, so a failure says which piece went missing rather than that something did.
    expect(GROUND_FRAGMENT, 'the planet-space varying').toContain('varying vec2 vPlanet');
    expect(GROUND_FRAGMENT, 'the render-space varying the fog and the shadow both read').toContain('varying vec3 vRender');
    expect(GROUND_FRAGMENT, 'the palette uniforms').toContain('uniform vec3 uPalette0');
    expect(GROUND_FRAGMENT, 'the ground-normal helper').toContain('vec3 groundNormal(');
    expect(GROUND_FRAGMENT, 'the shadow lookup that was spliced in').toContain('float shadowAt(');
    expect(GROUND_FRAGMENT, 'and its bilinear tap').toContain('float pcfTap(');
  });
});
