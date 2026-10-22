// Node types for THIS FILE only — the app's tsconfig sets `types: ["vite/client"]` deliberately, so `src` cannot reach a
// node API by accident. The same exemption `no-speed-literals.test.ts` takes, for the same reason: this reads source.
/// <reference types="node" />
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { SHADOW_BACKOFF_M, SHADOW_MAP_SIZE, SHADOW_RADIUS_M, SUN_DIRECTION } from '../src/render/sun';

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

  it('is what the ground shader is given, rather than a vector of its own', async () => {
    const shaders = await import('../src/render/shaders');
    // The ground takes the direction as a uniform. A literal here would be a fourth sun, and the one surface that
    // covers the whole screen.
    expect(shaders.GROUND_FRAGMENT).toContain('uniform vec3 uSunDirection');
    expect(shaders.GROUND_FRAGMENT).toContain('dot(ground, -uSunDirection)');
    expect(shaders.GROUND_FRAGMENT).not.toContain('0.42, 0.84, 0.34');
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
