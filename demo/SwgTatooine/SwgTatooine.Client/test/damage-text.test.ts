import { NullEngine } from '@babylonjs/core/Engines/nullEngine';
import { RawTexture } from '@babylonjs/core/Materials/Textures/rawTexture';
import { Scene } from '@babylonjs/core/scene';
import { afterEach, describe, expect, it } from 'vitest';

import { DAMAGE_MS, DAMAGE_RANGE_M, DamageText, type Anchor } from '../src/render/damage-text';
import type { FrameView } from '../src/render/layer-packer';
import { FLAT_GROUND } from '../src/terrain/ground-sampler';

const view: FrameView = {
  renderTick: 5,
  renderFrac: 0,
  originX: 0,
  originZ: 0,
  eyeX: 0,
  eyeY: 2,
  eyeZ: 0,
  planes: new Float64Array(24),
  pixelsPerMetre: 400,
  viewportWidth: 800,
  viewportHeight: 800,
  selectedNetId: 0,
  ground: FLAT_GROUND,
  predicted: null,
  hidden: new Set<number>(),
  crowd: null,
  crowdDraws: false,
  crowdCount: 0,
  perSecond: 1,
};

/** A body's head at `z` metres in front of the camera; only netId 7 is held. */
const headAt =
  (z: number) =>
  (netId: number, out: Anchor): boolean => {
    if (netId !== 7) {
      return false;
    }

    out.x = 0;
    out.y = 2;
    out.z = z;
    return true;
  };

/** The damage of a blow, in the scene over the body it landed on: one glyph per character of "-N", for a second, near enough. */
describe('DamageText (NullEngine)', () => {
  const engine = new NullEngine();
  const scene = new Scene(engine);
  afterEach(() => {
    scene.meshes.slice().forEach((m) => {
      m.dispose();
    });
  });

  it('spells "-amount" over the body, one glyph a character', () => {
    const text = new DamageText(scene, RawTexture.CreateRGBATexture(new Uint8Array(4), 1, 1, scene));
    text.spawn(7, 95, 0);
    text.update(view, 100, headAt(10));
    expect(text.glyphs).toBe(3);
  });

  it('is gone after its second', () => {
    const text = new DamageText(scene, RawTexture.CreateRGBATexture(new Uint8Array(4), 1, 1, scene));
    text.spawn(7, 95, 0);
    text.update(view, DAMAGE_MS + 1, headAt(10));
    expect(text.glyphs).toBe(0);
  });

  /** A fight across the desert is not read by its numbers. */
  it('is not drawn past its range', () => {
    const text = new DamageText(scene, RawTexture.CreateRGBATexture(new Uint8Array(4), 1, 1, scene));
    text.spawn(7, 95, 0);
    text.update(view, 100, headAt(DAMAGE_RANGE_M + 5));
    expect(text.glyphs).toBe(0);
  });

  it('is not drawn over a body never seen, and stays where a body that left was last seen', () => {
    const text = new DamageText(scene, RawTexture.CreateRGBATexture(new Uint8Array(4), 1, 1, scene));
    text.spawn(8, 40, 0);
    text.update(view, 100, headAt(10));
    expect(text.glyphs).toBe(0);

    text.spawn(7, 40, 0);
    text.update(view, 100, headAt(10));
    text.update(view, 200, () => false);
    expect(text.glyphs).toBe(3);
  });
});
