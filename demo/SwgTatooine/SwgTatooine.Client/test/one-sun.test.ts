// Node types for THIS FILE only — the app's tsconfig sets `types: ["vite/client"]` deliberately, so `src` cannot reach a
// node API by accident. The same exemption `no-speed-literals.test.ts` takes, for the same reason: this reads source.
/// <reference types="node" />
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { GROUND_ALBEDO } from '../src/render/shaders';
import {
  SHADOW_BACKOFF_M,
  SHADOW_FAR_M,
  SHADOW_MAP_SIZE,
  SHADOW_NEAR_M,
  SHADOW_RADIUS_M,
  SHADOW_SLAB_REACH_M,
  SUN_DIRECTION,
} from '../src/render/sun';

/**
 * The scene has ONE sun, and every surface is lit by it.
 *
 * <b>This is a test about a failure that was invisible for as long as nothing cast a shadow.</b> There were three sun
 * directions in this client: the entity meshes shaded against `(-0.45, -0.8, -0.4)`, the ground shader against a
 * hard-coded `(0.42, 0.84, 0.34)` written the other way round (a direction TO the sun rather than of travel), and the
 * published assets' `DirectionalLight` against `(-0.6, -0.8, 0.7)` — lit from the opposite side in Z. Three sets of
 * surfaces, each self-consistent, and nobody compares a dune's brightness with a wall's.
 *
 * A shadow makes it visible at once, because a building's shadow is cast by one of those suns onto ground shaded by
 * another: the dark side of the building and the dark side of the ground are on opposite sides of it. So the direction
 * belongs in one place, and a second literal must fail rather than merely look odd.
 */

const SRC = fileURLToPath(new URL('../src', import.meta.url));

/**
 * A source file with its comments stripped, so a name in prose is not a use.
 *
 * These checks assert that a constant is ABSENT, and every one of them explains in a comment which constant and why —
 * which is precisely the text a naive search finds. Read the code.
 *
 * @param parts The path under `src`.
 * @returns The file's code, comments replaced by a space.
 */
function sourceCode(...parts: string[]): string {
  return readFileSync(join(SRC, ...parts), 'utf8')
    .replace(/\/\*[\s\S]*?\*\//g, ' ')
    .replace(/\/\/[^\n]*/g, ' ');
}

/** Every `.ts`/`.tsx` file under `src`. */
function sources(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir)) {
    const path = join(dir, entry);
    if (statSync(path).isDirectory()) {
      out.push(...sources(path));
    } else if (entry.endsWith('.ts') || entry.endsWith('.tsx')) {
      out.push(path);
    }
  }

  return out;
}

describe('the scene has one sun', () => {
  it('points down, so it is a direction of TRAVEL and not a direction to the sun', () => {
    // The sign is the thing two of the three old vectors disagreed about, and a shader that takes the wrong one lights
    // the underside of everything.
    expect(SUN_DIRECTION.y).toBeLessThan(0);
    expect(SUN_DIRECTION.length()).toBeCloseTo(1, 6);
  });

  it('is the only sun direction written anywhere in src', () => {
    // A three-float vector literal whose middle component is a plausible elevation: that is what each of the three
    // suns looked like. `sun.ts` itself is where the one allowed literal lives.
    const pattern = /new Vector3\(\s*-?\d*\.?\d+\s*,\s*-?0?\.[5-9]\d*\s*,\s*-?\d*\.?\d+\s*\)/g;
    const offenders: string[] = [];
    for (const file of sources(SRC)) {
      if (file.endsWith('sun.ts')) {
        continue;
      }

      const text = readFileSync(file, 'utf8');
      for (const hit of text.matchAll(pattern)) {
        offenders.push(`${file}: ${hit[0]}`);
      }
    }

    expect(offenders, 'a sun-shaped vector literal outside sun.ts').toEqual([]);
  });

  it('lights the ground through Babylon, not through a vector of its own', () => {
    // The terrain is a material plugin now: Babylon applies the sun, so the albedo must carry no light of its own.
    expect(GROUND_ALBEDO, 'a hard-coded sun left in the albedo').not.toContain('0.42, 0.84, 0.34');
    expect(GROUND_ALBEDO, 'the albedo must not light itself').not.toContain('lambert');
  });

  it('lights the terrain and the buildings through one transport', () => {
    // <b>One sun direction was never enough.</b> Both surfaces agreed about where the light came from and still
    // disagreed about everything else: the terrain was a `StandardMaterial` with a hand-written 0.66 ambient floor and
    // a 0.5 diffuse, the buildings a `PBRMaterial` lit by `scene.environmentTexture`. Two light transports, matched by
    // eye at one time of day under one palette, which is the kind of agreement that comes apart the moment the
    // environment, the exposure or the planet changes.
    //
    // So: same material class, and the constants that stood in for the sky are gone.
    const terrain = sourceCode('render', 'terrain-material.ts');
    expect(terrain, 'the terrain must be the same material class as the assets').toContain('new PBRMaterial(');
    expect(terrain, "a StandardMaterial terrain cannot share the assets' light transport").not.toContain(
      'new StandardMaterial(',
    );
    expect(terrain, 'the hand-rolled sun share is the sky constant coming back').not.toMatch(/diffuseColor\s*=/);
    expect(terrain, 'the ambient floor belongs to the environment probe').not.toMatch(/ambientColor\s*=/);

    // And the scene ambient the port briefly used to carry that floor: `scene.ambientColor` reaches a StandardMaterial
    // and, because PBR defaults its own `ambientColor` to black, never reached a building — the inconsistency wearing
    // the costume of a fix.
    const ground = sourceCode('render', 'ground.ts');
    expect(ground, 'a scene ambient floor is terrain-only light by construction').not.toMatch(/scene\.ambientColor/);
  });

  it('converts the terrain albedo to linear, because PBR composes in linear and outputs gamma', () => {
    // A `StandardMaterial` writes `gl_FragColor` in whatever space it was handed; `pbr.fragment` ends with
    // `toGammaSpace` via `applyImageProcessing`. The palette is authored as sRGB, so handing it over raw brightens the
    // whole desert — and breaks the match with the assets, whose albedo textures the glTF loader marks `GAMMAALBEDO`
    // and linearises for exactly this reason. This is the one line that keeps the two in the same space.
    const terrain = sourceCode('render', 'terrain-material.ts');
    expect(terrain, 'the albedo must be linearised before PBR composes with it').toContain(
      'surfaceAlbedo = toLinearSpace(color)',
    );
  });

  it('owns the sun on the scene, not on the asset mount', () => {
    // `mountAssets` reports its failures and swallows them, deliberately: a 404 on the catalogue is not a reason for
    // the simulation to stop being watchable. While the sun and the environment were created inside it, that made every
    // such failure put out the light for the terrain too — which is only harmless as long as the terrain lights itself,
    // and it no longer does.
    const mount = sourceCode('app', 'asset-mount.ts');
    expect(mount, 'the mount must not create the lighting it is handed').not.toContain('new SceneLighting(');
    const app = sourceCode('app', 'client-app.ts');
    expect(app, 'the app owns the scene lighting').toContain('new SceneLighting(');
  });

  it('frames a shadow slab that is smaller than the planet and deeper than the tallest thing in it', () => {
    // The slab follows the camera. A map that covered the 16 km planet would put a metre of ground in a texel, and one
    // fitted to the casters would do the same with the added property of changing whenever a building streams in.
    expect(SHADOW_RADIUS_M).toBeLessThan(256);
    // Enough ground per texel to resolve a doorway: under 15 cm.
    expect((2 * SHADOW_RADIUS_M) / SHADOW_MAP_SIZE).toBeLessThan(0.15);
    // The shadow camera has to clear the tallest caster, or whatever is nearer the light than the near plane stops
    // casting altogether.
    expect(SHADOW_BACKOFF_M).toBeGreaterThan(SHADOW_RADIUS_M);
  });
});

describe('the reach that decides which meshes are offered to the shadow map', () => {
  /**
   * The furthest a point of the shadow volume can be from the slab's centre, horizontally — computed the long way,
   * by walking the corners of the box the light actually renders, so the closed form in `sun.ts` has something
   * independent to agree with.
   */
  function furthestCorner(): number {
    const d = SUN_DIRECTION;
    // Two axes orthogonal to the light, which is what the ortho slab is measured in.
    const up = Math.abs(d.y) > 0.9 ? { x: 1, y: 0, z: 0 } : { x: 0, y: 1, z: 0 };
    const rx = { x: up.y * d.z - up.z * d.y, y: up.z * d.x - up.x * d.z, z: up.x * d.y - up.y * d.x };
    const rl = Math.hypot(rx.x, rx.y, rx.z);
    const r = { x: rx.x / rl, y: rx.y / rl, z: rx.z / rl };
    const u = { x: d.y * r.z - d.z * r.y, y: d.z * r.x - d.x * r.z, z: d.x * r.y - d.y * r.x };

    let worst = 0;
    for (const depth of [SHADOW_NEAR_M - SHADOW_BACKOFF_M, SHADOW_FAR_M - SHADOW_BACKOFF_M]) {
      for (const sx of [-SHADOW_RADIUS_M, SHADOW_RADIUS_M]) {
        for (const sy of [-SHADOW_RADIUS_M, SHADOW_RADIUS_M]) {
          const x = d.x * depth + r.x * sx + u.x * sy;
          const z = d.z * depth + r.z * sx + u.z * sy;
          worst = Math.max(worst, Math.hypot(x, z));
        }
      }
    }

    return worst;
  }

  it('covers every corner of the volume the sun actually renders', () => {
    // A reach that is short drops a caster that could have shadowed, silently and only at certain sun angles —
    // the failure this test exists for. Being generous costs a few meshes in a list; being short costs a shadow.
    expect(SHADOW_SLAB_REACH_M).toBeGreaterThanOrEqual(furthestCorner());
  });

  it('is not so generous that it stops meaning anything', () => {
    // It must still exclude the far town: the range structures are drawn to is 1 280 m.
    expect(SHADOW_SLAB_REACH_M).toBeLessThan(250);
  });
});
