import { describe, expect, it } from 'vitest';
import type { AssetLibrary, Instance } from '../src/assets/asset-library';
import { StaticAssets, type StructureSample } from '../src/app/static-assets';

/**
 * A structure is drawn as its asset once the asset lands, and by NOBODY before that (Loïc, 2026-10-26: what is lazy loads
 * asynchronously, and what waits on it is not drawn). A structure with no asset coming keeps the renderer's box.
 */

const sample = (netId: number, asset = 1): StructureSample => ({
  netId,
  x: netId * 10,
  y: 0,
  z: 0,
  kind: 1,
  yaw: 0,
  asset,
});

function instance(): Instance {
  return {
    meshes: [],
    children: [],
    lights: [],
    root: { position: { y: 0 } },
    dispose() {
      /* nothing to release in a fake */
    },
  } as unknown as Instance;
}

/** A library whose loads land when the test says so; `house` is published, `shed` is not, `ruin` failed. */
function library() {
  const landing: (() => void)[] = [];
  const failed = new Set<string>(['ruin']);
  const lib = {
    catalogue: { assets: { house: {}, ruin: {} } },
    hasFailed: (id: string) => failed.has(id),
    described: () => null,
    instantiate: () =>
      new Promise<Instance>((resolve) => {
        landing.push(() => {
          resolve(instance());
        });
      }),
  } as unknown as AssetLibrary;
  return { lib, landing };
}

const ASSETS = ['', 'house', 'shed', 'ruin'];

async function settle(): Promise<void> {
  for (let i = 0; i < 5; i++) {
    await Promise.resolve();
  }
}

describe('a structure waiting on its asset', () => {
  it('is drawn by nobody until its asset lands, then by the asset', async () => {
    const { lib, landing } = library();
    const assets = new StaticAssets(lib, {} as never, (a) => ASSETS[a] || null, 8);
    const samples = [sample(1)];

    const drawn = assets.update(samples, 1);
    expect(assets.waiting.has(1)).toBe(true);
    expect(drawn.has(1)).toBe(false);

    landing.shift()?.();
    await settle();
    assets.update(samples, 1);
    expect(assets.waiting.has(1)).toBe(false);
    expect(assets.update(samples, 1).has(1)).toBe(true);
  });

  it('waits even while the frame budget holds its load back, so the box never shows in the meantime', () => {
    const { lib } = library();
    const assets = new StaticAssets(lib, {} as never, (a) => ASSETS[a] || null, 1);
    const samples = [sample(1), sample(2), sample(3)];
    assets.update(samples, 3);
    expect([...assets.waiting].sort()).toEqual([1, 2, 3]);
  });

  it('keeps the box for one with nothing coming: no asset published, or its load failed', () => {
    const { lib } = library();
    const assets = new StaticAssets(lib, {} as never, (a) => ASSETS[a] || null, 8);
    const samples = [sample(1, 2), sample(2, 3), sample(3, 0)];
    assets.update(samples, 3);
    expect(assets.waiting.size).toBe(0);
  });
});
