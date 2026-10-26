import { beforeAll, describe, expect, it } from 'vitest';
import { AssetContainer } from '@babylonjs/core/assetContainer';
import { NullEngine } from '@babylonjs/core/Engines/nullEngine';
import { PointLight } from '@babylonjs/core/Lights/pointLight';
import { ShadowGenerator } from '@babylonjs/core/Lights/Shadows/shadowGenerator';
import { ImageProcessingConfiguration } from '@babylonjs/core/Materials/imageProcessingConfiguration';
import { StandardMaterial } from '@babylonjs/core/Materials/standardMaterial';
import type { RenderTargetTexture } from '@babylonjs/core/Materials/Textures/renderTargetTexture';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import type { AbstractMesh } from '@babylonjs/core/Meshes/abstractMesh';
import { CreateBox } from '@babylonjs/core/Meshes/Builders/boxBuilder';
import { InstancedMesh } from '@babylonjs/core/Meshes/instancedMesh';
import { Mesh } from '@babylonjs/core/Meshes/mesh';
import { Scene } from '@babylonjs/core/scene';
import { placement } from '../src/assets/asset-frame';
import { AssetLibrary, type LoadedAsset } from '../src/assets/asset-library';
import type { Descriptor } from '../src/assets/descriptor';
import { LAMP_SHADOWS_MAX, MAX_LIGHTS_PER_MATERIAL, SceneLighting } from '../src/render/scene-lighting';
import { SUN_INTENSITY } from '../src/render/sun';

/**
 * A room is lit the testbench's way (`render/lighting.ts`): no sun, a dim sky, tone mapping, and its own lamps — which
 * light everything in it and cast a cube shadow each. Measured live before these: the room's walls were lit by the sun
 * alone, because an instanced placement takes its source mesh's light list and the source is never in the scene.
 */

beforeAll(() => {
  // Babylon's texture loader reaches for `XMLHttpRequest`, which vitest's node environment does not have (see
  // `scene-profile.test.ts`). Nothing here is fetched; the stub only keeps the environment probe from throwing.
  const host = globalThis as unknown as { XMLHttpRequest?: unknown };
  host.XMLHttpRequest ??= class {
    open(): void {}
    send(): void {}
    setRequestHeader(): void {}
    addEventListener(): void {}
    abort(): void {}
  };
});

function scene(): Scene {
  return new Scene(new NullEngine());
}

/** What Babylon does as a shadow map is about to render: its list is filled then, once per render. */
function render(map: RenderTargetTexture): void {
  map.onBeforeBindObservable.notifyObservers(map);
}

describe('SceneLighting.setIndoors', () => {
  it("turns the sun to nothing, dims the sky to the testbench's 0.15 and tone-maps, and undoes all three outside", () => {
    const s = scene();
    const lighting = new SceneLighting(s, '/vendor/');
    const sun = s.getLightByName('sun')!;

    lighting.setIndoors(true);
    expect(sun.intensity).toBe(0);
    expect(sun.isEnabled(), 'dimmed, not disabled: a light count change would recompile nothing it should').toBe(true);
    expect(s.environmentIntensity).toBeCloseTo(0.15, 9);
    expect(s.imageProcessingConfiguration.toneMappingEnabled).toBe(true);
    expect(s.imageProcessingConfiguration.toneMappingType).toBe(
      ImageProcessingConfiguration.TONEMAPPING_KHR_PBR_NEUTRAL,
    );

    lighting.setIndoors(false);
    expect(sun.intensity).toBe(SUN_INTENSITY);
    expect(s.environmentIntensity).toBe(1);
    expect(s.imageProcessingConfiguration.toneMappingEnabled).toBe(false);
  });

  it("draws nothing into the sun's map indoors", () => {
    const s = scene();
    const lighting = new SceneLighting(s, '/vendor/');
    lighting.setShadows(true);
    const wall = CreateBox('wall', {}, s);
    lighting.castShadows([wall]);
    const map = s.getLightByName('sun')!.getShadowGenerator()!.getShadowMap()!;

    render(map);
    expect([...map.renderList!]).toEqual([wall]);
    lighting.setIndoors(true);
    render(map);
    expect([...map.renderList!]).toEqual([]);
  });
});

describe("SceneLighting's casters", () => {
  function street() {
    const s = scene();
    const lighting = new SceneLighting(s, '/vendor/');
    lighting.setShadows(true);
    const map = s.getLightByName('sun')!.getShadowGenerator()!.getShadowMap()!;
    return { s, lighting, map };
  }

  it('are the marked meshes within the slab, enabled and visible — not the scene', () => {
    const { s, lighting, map } = street();
    const wall = CreateBox('wall', {}, s);
    const hidden = CreateBox('hidden', {}, s);
    const off = CreateBox('off', {}, s);
    const far = CreateBox('far', {}, s);
    CreateBox('unmarked', {}, s);
    far.position.x = 2000;
    far.computeWorldMatrix(true);
    lighting.castShadows([wall, hidden, off, far]);
    hidden.isVisible = false;
    off.setEnabled(false);

    render(map);
    expect([...map.renderList!]).toEqual([wall]);
    expect(map.renderListPredicate, 'a predicate is run over every mesh of the scene').toBeUndefined();
  });

  it('leave when they leave the scene', () => {
    const { s, lighting, map } = street();
    const wall = CreateBox('wall', {}, s);
    const shed = CreateBox('shed', {}, s);
    lighting.castShadows([wall, shed]);
    render(map);
    expect(map.renderList).toHaveLength(2);

    shed.dispose();
    render(map);
    expect([...map.renderList!]).toEqual([wall]);
  });

  /**
   * Babylon's own predicate path emptied the list and pushed into it every frame, and the first push into an empty list
   * marks every mesh of the scene light-dirty: every receiver re-derived its defines, every frame.
   */
  it("mark the scene's meshes light-dirty only when the list empties or fills, never frame after frame", () => {
    const { s, lighting, map } = street();
    const wall = CreateBox('wall', {}, s);
    lighting.castShadows([wall]);
    const receiver = CreateBox('ground', {}, s);
    let marked = 0;
    const mark = receiver._markSubMeshesAsLightDirty.bind(receiver);
    receiver._markSubMeshesAsLightDirty = (dispose?: boolean) => {
      marked++;
      mark(dispose);
    };

    for (let frame = 0; frame < 3; frame++) {
      render(map);
    }

    expect(marked, 'once, as the list filled').toBe(1);
    lighting.setShadows(false);
    render(map);
    render(map);
    expect(marked, 'once more, as it emptied').toBe(2);
    lighting.setShadows(true);
    render(map);
    render(map);
    expect(marked, 'and again as it filled').toBe(3);
  });
});

describe('SceneLighting.lampShadows', () => {
  function room(): { s: Scene; lighting: SceneLighting; lamp: PointLight; wall: AbstractMesh } {
    const s = scene();
    const lighting = new SceneLighting(s, '/vendor/');
    lighting.setShadows(true);
    const lamp = new PointLight('lamp', new Vector3(0, 2.5, 0), s);
    const wall = CreateBox('wall', {}, s);
    wall.material = new StandardMaterial('stone', s);
    lighting.castShadows([wall]);
    lighting.lampShadows([lamp], [wall]);
    return { s, lighting, lamp, wall };
  }

  it("shadows a room's brightest lamps only, within a shader's texture units — the town hall's eight", () => {
    const s = scene();
    const lighting = new SceneLighting(s, '/vendor/');
    const lamps = [14, 14, 32, 119, 14, 14, 32, 20].map((w, i) => {
      const lamp = new PointLight(`lamp${i}`, new Vector3(i, 2.5, 0), s);
      lamp.intensity = w;
      return lamp;
    });
    lighting.lampShadows(lamps, []);
    const casting = lamps.filter((l) => l.getShadowGenerator() !== null).map((l) => l.intensity);
    expect(casting.sort((a, b) => b - a)).toEqual([119, 32, 32, 20]);
    expect(LAMP_SHADOWS_MAX).toBe(4);
  });

  it('lets every material take every lamp of the hall and the sun, made before the lighting or after', async () => {
    const s = scene();
    const before = new StandardMaterial('before', s);
    new SceneLighting(s, '/vendor/');
    const after = new StandardMaterial('after', s);
    await new Promise((resolve) => setTimeout(resolve, 20)); // the scene announces a material on a timer
    expect(before.maxSimultaneousLights).toBe(MAX_LIGHTS_PER_MATERIAL);
    expect(after.maxSimultaneousLights).toBe(MAX_LIGHTS_PER_MATERIAL);
    expect(MAX_LIGHTS_PER_MATERIAL, 'eight lamps and the sun').toBeGreaterThanOrEqual(9);
  });

  it("gives each lamp the testbench's cube map: Poisson, 0.05 to 40 m, and the room receives", () => {
    const { lamp, wall } = room();
    const generator = lamp.getShadowGenerator() as ShadowGenerator;
    expect(generator).not.toBeNull();
    expect(generator.usePoissonSampling).toBe(true);
    expect(generator.mapSize).toBe(1024);
    expect([lamp.shadowMinZ, lamp.shadowMaxZ]).toEqual([0.05, 40]);
    expect(wall.receiveShadows).toBe(true);
  });

  it('casts what is marked and in reach — a body walking in included — and nothing while shadows are off', () => {
    const { s, lighting, lamp, wall } = room();
    const map = lamp.getShadowGenerator()!.getShadowMap()!;
    const body = CreateBox('body', {}, s);
    const far = CreateBox('far', {}, s);
    far.position.x = 100;
    CreateBox('unmarked', {}, s);
    lighting.castShadows([body, far]);
    far.computeWorldMatrix(true);

    render(map);
    expect([...map.renderList!], 'far is beyond the 40 m range').toEqual([wall, body]);
    lighting.setShadows(false);
    render(map);
    expect([...map.renderList!]).toEqual([]);
  });

  it("renders the casters double-sided in the shadow pass, on the engine's state, never touching a material", () => {
    const { s, lamp, wall } = room();
    const engine = s.getEngine();
    const states: boolean[] = [];
    const setState = engine.setState.bind(engine);
    engine.setState = (culling, ...rest) => {
      states.push(culling);
      setState(culling, ...rest);
    };
    const generator = lamp.getShadowGenerator() as ShadowGenerator;
    let dirtied = 0;
    const material = wall.material!;
    const markAsDirty = material.markAsDirty.bind(material);
    material.markAsDirty = (flag) => {
      dirtied++;
      markAsDirty(flag);
    };

    generator.onBeforeShadowMapRenderMeshObservable.notifyObservers(wall as Mesh);
    expect(states).toEqual([false]);
    expect(material.backFaceCulling, 'the main pass still culls').toBe(true);
    expect(dirtied, 'a material marked dirty is re-prepared: per face, per lamp, per frame').toBe(0);
  });

  it('goes with the lamp: disposing the room disposes the map', () => {
    const { lamp } = room();
    const generator = lamp.getShadowGenerator()!;
    lamp.dispose();
    expect(generator.getShadowMap()).toBeNull();
  });
});

describe('AssetLibrary.instantiate, for a room', () => {
  /** A library holding one asset of the given kind: a `__root__` with a box and a lamp under it, as a room's GLB has. */
  function libraryWith(s: Scene, kind: Descriptor['kind']): AssetLibrary {
    const root = new Mesh('__root__', s);
    const box = CreateBox('shell', {}, s);
    box.parent = root;
    const lamp = new PointLight('lamp', new Vector3(0, 2, 0), s);
    lamp.parent = root;
    const container = new AssetContainer(s);
    container.moveAllFromScene();
    const descriptor = { id: 'asset', kind, children: [] } as unknown as Descriptor;
    const loaded: LoadedAsset = { id: 'asset', descriptor, container };
    const make = AssetLibrary as unknown as new (scene: Scene, base: string, catalogue: unknown) => AssetLibrary;
    const library = new make(s, '/', { schema: 2, version: 1, assets: {} });
    (library as unknown as { loading: Map<string, Promise<LoadedAsset>> }).loading.set(
      'asset',
      Promise.resolve(loaded),
    );
    return library;
  }

  it('clones it, so its meshes are in the scene and take its lamps, and leaves the lamps unrestricted', async () => {
    const s = scene();
    const instance = await libraryWith(s, 'interior').instantiate('asset', placement([0, 0, 0]));
    expect(instance.meshes.length).toBeGreaterThan(0);
    for (const mesh of instance.meshes) {
      expect(mesh).not.toBeInstanceOf(InstancedMesh);
      expect(s.meshes).toContain(mesh);
    }

    expect(instance.lights.length).toBe(1);
    expect(instance.lights[0].includedOnlyMeshes.length).toBe(0);
    expect(instance.meshes[0].lightSources, 'what was measured missing live').toContain(instance.lights[0]);
  });

  it('instances a building and keeps its lamps on its own meshes, so they do not flood the street', async () => {
    const s = scene();
    const instance = await libraryWith(s, 'building').instantiate('asset', placement([0, 0, 0]));
    expect(instance.meshes.some((mesh) => mesh instanceof InstancedMesh)).toBe(true);
    const included = instance.lights[0].includedOnlyMeshes;
    expect(included.length).toBe(instance.meshes.length);
    expect(instance.meshes.every((mesh) => included.includes(mesh))).toBe(true);
  });
});
