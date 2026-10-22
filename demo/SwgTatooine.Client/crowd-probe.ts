import { ArcRotateCamera } from '@babylonjs/core/Cameras/arcRotateCamera';
import { Engine } from '@babylonjs/core/Engines/engine';
import { HemisphericLight } from '@babylonjs/core/Lights/hemisphericLight';
import { Color4 } from '@babylonjs/core/Maths/math.color';
import { Matrix, Vector3 } from '@babylonjs/core/Maths/math.vector';
import { Scene } from '@babylonjs/core/scene';

import { registerBuiltInLoaders } from '@babylonjs/loaders/dynamic';

import { placement } from './src/assets/asset-frame';
import { configureDecoders } from './src/assets/decoders';
import { AssetLibrary } from './src/assets/asset-library';
import type { Descriptor } from './src/assets/descriptor';
import { planClipAtlas } from './src/character/clip-atlas';
import { createCrowdMaterial } from './src/render/crowd-material';
import { buildCrowdOutfit } from './src/render/crowd-mesh';
import { bakeClipAtlas, type BakeSource } from './src/render/crowd-rig';

/**
 * One real outfit, really baked, really merged, one instance, a camera pointed at it.
 *
 * <b>Everything the app does to a crowd body, with nothing else in the scene.</b> No terrain, no buildings, no
 * replication, no origin rebasing, no shadow generator — so anything that goes wrong here is in the crowd files and
 * nowhere else. Served at `/crowd-probe.html`; delete both once the crowd draws in the app.
 */

const log = document.getElementById('log') as HTMLPreElement;
const say = (s: string): void => {
  log.textContent += s + '\n';
};

const canvas = document.getElementById('c') as HTMLCanvasElement;
const engine = new Engine(canvas, true, { preserveDrawingBuffer: false });
const scene = new Scene(engine);
scene.clearColor = new Color4(0.1, 0.12, 0.16, 1);
const camera = new ArcRotateCamera('cam', -Math.PI / 2, Math.PI / 2.3, 4, new Vector3(0, 0.9, 0), scene);
camera.attachControl(canvas, true);
camera.minZ = 0.05;
new HemisphericLight('h', new Vector3(0, 1, 0), scene);

const OUTFIT = 'casual2_female';
const start = performance.now();

async function main(): Promise<void> {
  const demo = (await (await fetch('/typhon/demo.json')).json()) as { assets?: { base: string; catalogue: string } };
  const set = demo.assets;
  if (set === undefined) {
    say('no asset set announced');
    return;
  }

  // The same two the app does before any GLB is fetched: the KTX2/meshopt decoders and the glTF loader itself.
  configureDecoders();
  registerBuiltInLoaders();
  say(`catalogue ............... ${set.catalogue}`);
  const library = await AssetLibrary.open(scene, set.base, set.catalogue);

  const described = new Map<string, Descriptor>();
  for (const id of library.ids) {
    try {
      described.set(id, await library.describe(id));
    } catch {
      /* a descriptor that will not fetch is that asset's problem alone */
    }
  }

  const joints = described.get(OUTFIT)?.render.joints ?? 23;
  const plan = planClipAtlas(described, joints);
  say(`plan .................... ${String(plan.rows)} rows, ${String(plan.width)} texels, ${String(plan.bakes.length)} bakes`);

  const sources = new Map<string, BakeSource>();
  for (const id of new Set(plan.bakes.map((b) => b.source))) {
    await library.acquire(id);
    sources.set(id, { instance: await library.instantiate(id, placement([0, 0, 0], 0), null, `bake:${id}`) });
  }

  const rig = bakeClipAtlas(scene, plan, sources);
  for (const s of sources.values()) {
    s.instance.dispose();
  }

  const size = rig.texture.getSize();
  say(`atlas ................... ${String(size.width)} x ${String(size.height)}, ready=${String(rig.texture.isReady())}`);

  const { material, skin } = createCrowdMaterial(scene, rig);
  await library.acquire(OUTFIT);
  const instance = await library.instantiate(OUTFIT, placement([0, 0, 0], 0), null, `build:${OUTFIT}`);
  const height = described.get(OUTFIT)?.bounds.render[1][1] ?? 1.7;
  const outfit = buildCrowdOutfit(scene, instance, rig, material, height, OUTFIT);
  instance.dispose();
  say(`outfit .................. ${String(outfit.lods.length)} lods, ${outfit.lods.map((l) => String(l.triangles)).join('/')} tris, h=${height.toFixed(2)}`);

  const mesh = outfit.lods[0].mesh;
  const CAP = 4;
  const matrices = new Float32Array(CAP * 16);
  const clipA = new Float32Array(CAP * 4);
  const clipB = new Float32Array(CAP * 4);
  const fade = new Float32Array(CAP * 2);
  Matrix.Identity().copyToArray(matrices, 0);

  const idle = plan.slotOf(OUTFIT, 'idle');
  say(
    `idle slot ............... ${idle === null ? 'NONE' : `row ${String(idle.startRow)} x ${String(idle.frames)} from ${idle.source}/${idle.clip}`}`,
  );
  const a = idle === null ? [0, 1, 0, 1] : [idle.startRow, idle.frames, 0, 1];
  clipA.set(a, 0);
  clipB.set(a, 0);
  fade.set([-1e6, 4], 0);

  mesh.thinInstanceSetBuffer('matrix', matrices, 16, false);
  mesh.thinInstanceSetBuffer('crowdClipA', clipA, 4, false);
  mesh.thinInstanceSetBuffer('crowdClipB', clipB, 4, false);
  mesh.thinInstanceSetBuffer('crowdFade', fade, 2, false);
  mesh.thinInstanceCount = 1;
  mesh.isVisible = true;

  let frames = 0;
  scene.onBeforeRenderObservable.add(() => {
    skin.time = (performance.now() - start) / 1000;
    const count = mesh.thinInstanceCount;
    mesh.thinInstancePartialBufferUpdate('matrix', count, 0);
    mesh.thinInstancePartialBufferUpdate('crowdClipA', count, 0);
    mesh.thinInstancePartialBufferUpdate('crowdClipB', count, 0);
    mesh.thinInstancePartialBufferUpdate('crowdFade', count, 0);
  });
  scene.onAfterRenderObservable.add(() => {
    frames++;
  });

  engine.runRenderLoop(() => {
    scene.render();
  });

  setTimeout(() => {
    const effect = mesh.subMeshes[0]?.effect ?? null;
    const gl = engine._gl as WebGL2RenderingContext;
    say(`frames .................. ${String(frames)}`);
    say(`mesh .................... verts=${String(mesh.getTotalVertices())} idx=${String(mesh.getTotalIndices())}`);
    say(`effect ready ............ ${String(effect?.isReady() ?? false)}`);
    say(`compile error ........... ${effect?.getCompilationError?.() || '(none)'}`);
    scene.onAfterRenderObservable.addOnce(() => {
      const px = new Uint8Array(4);
      gl.readPixels(canvas.width >> 1, canvas.height >> 1, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, px);
      say(`centre pixel ............ ${String(px[0])},${String(px[1])},${String(px[2])}`);
      say(px[0] > 40 || px[1] > 40 ? '>>> SOMETHING IS DRAWN' : '>>> NOTHING IS DRAWN');
    });
    (window as unknown as Record<string, unknown>).__probe = { scene, mesh, rig, plan, outfit, skin, engine };
  }, 2500);
}

void main().catch((e: unknown) => {
  say(`FAILED: ${String(e)}`);
});
