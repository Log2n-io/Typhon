import { NullEngine } from '@babylonjs/core/Engines/nullEngine';
import { TransformNode } from '@babylonjs/core/Meshes/transformNode';
import { Scene } from '@babylonjs/core/scene';
import { afterEach, describe, expect, it } from 'vitest';
import type { AssetLibrary, Instance } from '../src/assets/asset-library';
import { CROWD_SWOOPS, CrowdSwoops } from '../src/app/crowd-swoops';
import type { CrowdRider } from '../src/render/crowd-layer';
import type { SceneLighting } from '../src/render/scene-lighting';

const engine = new NullEngine();
const scenes: Scene[] = [];

afterEach(() => {
  for (const scene of scenes.splice(0)) {
    scene.dispose();
  }
});

/** A library whose swoop "loads" when the test says so, and which counts what it handed out and what was given back. */
function library(scene: Scene) {
  const pending: (() => void)[] = [];
  const stats = { made: 0, disposed: 0 };
  const lib = {
    catalogue: { assets: { swoop: {} } },
    instantiate(_id: string, _pose: unknown, parent: TransformNode): Promise<Instance> {
      stats.made++;
      return new Promise((resolve) => {
        pending.push(() => {
          const root = new TransformNode('swoop', scene);
          root.parent = parent;
          resolve({
            root,
            meshes: [],
            dispose() {
              stats.disposed++;
              root.dispose();
            },
          } as unknown as Instance);
        });
      });
    },
  } as unknown as AssetLibrary;
  return {
    lib,
    stats,
    load: () => {
      for (const f of pending.splice(0)) {
        f();
      }
    },
  };
}

const lighting = { castShadows: () => undefined } as unknown as SceneLighting;

function rider(netId: number, x = 0): CrowdRider {
  return { netId, x, y: 0, z: 0, heading: 1 };
}

function setup() {
  const scene = new Scene(engine);
  scenes.push(scene);
  const lib = library(scene);
  return { scene, ...lib, swoops: new CrowdSwoops(scene, lib.lib, lighting) };
}

describe('CrowdSwoops', () => {
  it('puts one swoop under each rider, where the rider is, and shows it once loaded', async () => {
    const { swoops, stats, load, scene } = setup();
    swoops.update([rider(7, 12)], 1);
    expect(stats.made).toBe(1);
    load();
    await Promise.resolve();
    swoops.update([rider(7, 15)], 1);
    const node = scene.getTransformNodeByName('crowd-swoop#7');
    expect(node?.position.x).toBe(15);
    expect(node?.isEnabled()).toBe(true);
    swoops.update([rider(7, 16)], 1);
    expect(stats.made).toBe(1);
  });

  it('lets a swoop go the first frame its rider is not seated — and one still loading is disposed when it lands', async () => {
    const { swoops, stats, load } = setup();
    swoops.update([rider(1), rider(2)], 2);
    swoops.update([rider(1)], 1);
    expect(swoops.count).toBe(1);
    load();
    await Promise.resolve();
    expect(stats.disposed).toBe(1);
    swoops.update([], 0);
    expect(swoops.count).toBe(0);
    expect(stats.disposed).toBe(2);
  });

  it('holds no more than its cap, however many ride', () => {
    const { swoops, stats } = setup();
    const many = Array.from({ length: CROWD_SWOOPS + 10 }, (_, i) => rider(i + 1));
    swoops.update(many, many.length);
    expect(swoops.count).toBe(CROWD_SWOOPS);
    expect(stats.made).toBe(CROWD_SWOOPS);
  });
});
