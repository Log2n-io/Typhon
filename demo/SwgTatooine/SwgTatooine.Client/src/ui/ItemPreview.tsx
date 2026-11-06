import { useEffect, useRef, useState } from 'react';
import type { AssetContainer } from '@babylonjs/core/assetContainer';
import { ArcRotateCamera } from '@babylonjs/core/Cameras/arcRotateCamera';
import { Engine } from '@babylonjs/core/Engines/engine';
import { DirectionalLight } from '@babylonjs/core/Lights/directionalLight';
import { HemisphericLight } from '@babylonjs/core/Lights/hemisphericLight';
import { LoadAssetContainerAsync } from '@babylonjs/core/Loading/sceneLoader';
import { ImageProcessingConfiguration } from '@babylonjs/core/Materials/imageProcessingConfiguration';
import { PBRMaterial } from '@babylonjs/core/Materials/PBR/pbrMaterial';
import { CubeTexture } from '@babylonjs/core/Materials/Textures/cubeTexture';
import { Color3, Color4 } from '@babylonjs/core/Maths/math.color';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { TransformNode } from '@babylonjs/core/Meshes/transformNode';
import { Scene } from '@babylonjs/core/scene';
import { ASSET_LOAD_OPTIONS } from '../assets/asset-library';
import { configureDecoders } from '../assets/decoders';
import type { Descriptor } from '../assets/descriptor';
import { SPIN_START, spinDrag, spinHold, spinStep, type Spin } from '../data/inventory-grid';
import { SUN_DIRECTION, SUN_INTENSITY } from '../render/sun';
import { useItems } from '../state/items-store';

/** The sky the preview's model is lit by: the world's probe (`SceneLighting`). */
const ENVIRONMENT = 'environmentSpecular.env';

/** The camera's field of view, rad, and how much of it the model's bounding sphere fills. */
const FOV = 0.6;
const FILL = 0.8;

/** The preview's own engine and scene, made once per mount: the model is swapped in it, the WebGL context never made twice. */
interface Stage {
  readonly engine: Engine;
  readonly scene: Scene;
  readonly camera: ArcRotateCamera;
  readonly turn: TransformNode;
  container: AssetContainer | null;
}

/**
 * An item's model, turning (client.md C43): the inventory's preview panel. A scene and an engine of its own on its own canvas — the world's library binds
 * its containers to the world's scene — made once while the panel is open, its context given back when it closes (`loseContextOnDispose`: a context left
 * to the garbage collector counts against the browser's sixteen, and the one evicted past them is the world's). Another item swaps the model, not the
 * engine; nothing is drawn while there is none.
 *
 * The model spins on its own — not for a player who asked for reduced motion; dragged, it turns, keeps the speed it was flung at and eases back to the idle
 * spin (`spinStep`). A drag up or down tilts it. Its Mk shows as the asset's Mk material takes it (ASSET_FORMAT.md § 5.11).
 */
export function ItemPreview({ asset, mk }: { readonly asset: string; readonly mk: number }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const models = useItems((s) => s.models);
  const [state, setState] = useState<'loading' | 'shown' | 'none'>('loading');
  const spin = useRef<Spin>(SPIN_START);
  const drag = useRef<{ x: number; y: number; t: number } | null>(null);
  const stage = useRef<Stage | null>(null);

  // The engine, for as long as the panel shows a preview.
  useEffect(() => {
    const el = canvas.current;
    if (el === null) {
      return undefined;
    }

    const engine = new Engine(el, true, {
      stencil: false,
      preserveDrawingBuffer: false,
      alpha: true,
      powerPreference: 'low-power',
      loseContextOnDispose: true,
    });
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0, 0, 0, 0);
    scene.environmentTexture = CubeTexture.CreateFromPrefilteredData(configureDecoders() + ENVIRONMENT, scene);
    scene.environmentIntensity = 0.8;
    scene.imageProcessingConfiguration.toneMappingType = ImageProcessingConfiguration.TONEMAPPING_KHR_PBR_NEUTRAL;
    scene.imageProcessingConfiguration.toneMappingEnabled = true;
    const camera = new ArcRotateCamera('preview', -Math.PI / 2, Math.PI / 2, 1, Vector3.Zero(), scene);
    camera.fov = FOV;
    camera.minZ = 0.01;
    const sky = new HemisphericLight('sky', new Vector3(0, 1, 0), scene);
    sky.intensity = 0.4;
    const sun = new DirectionalLight('sun', SUN_DIRECTION.clone(), scene);
    sun.intensity = SUN_INTENSITY;

    // Pitch outside, yaw inside: the yaw turns the model about its own up, the pitch tilts the result towards the camera.
    const tilt = new TransformNode('tilt', scene);
    const turn = new TransformNode('turn', scene);
    turn.parent = tilt;
    const current: Stage = { engine, scene, camera, turn, container: null };
    stage.current = current;
    const still = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    let last = performance.now();
    engine.runRenderLoop(() => {
      const now = performance.now();
      const dt = Math.min(0.1, (now - last) / 1000);
      last = now;
      if (current.container === null) {
        return;
      }

      if (drag.current === null) {
        if (!still) {
          spin.current = spinStep(spin.current, dt);
        }
      } else if (now - drag.current.t > 40) {
        spin.current = spinHold(spin.current, dt);
      }

      turn.rotation.y = spin.current.yaw;
      tilt.rotation.x = spin.current.pitch;
      scene.render();
    });
    const resize = new ResizeObserver(() => {
      engine.resize();
    });
    resize.observe(el);
    return () => {
      resize.disconnect();
      engine.stopRenderLoop();
      current.container?.dispose();
      current.container = null;
      stage.current = null;
      scene.dispose();
      engine.dispose();
    };
  }, []);

  // The model: another item, or another Mk, swapped into the same scene.
  useEffect(() => {
    const current = stage.current;
    if (current === null || models === null) {
      setState('none');
      return undefined;
    }

    // An object, not a `let`: the async load reads it after the cleanup set it, which a narrowed local hides from the linter.
    const life = { gone: false };
    const gone = (): boolean => life.gone || stage.current !== current;
    setState('loading');
    spin.current = SPIN_START;
    void (async () => {
      try {
        const model = await models(asset);
        if (gone() || model === null) {
          if (!gone()) {
            setState('none');
          }

          return;
        }

        const container = await LoadAssetContainerAsync(model.glb, current.scene, ASSET_LOAD_OPTIONS);
        if (gone()) {
          container.dispose();
          return;
        }

        current.container?.dispose();
        current.container = container;
        container.addAllToScene();
        frame(container, current.turn, current.camera);
        tintMk(container, model.descriptor, mk);
        setState('shown');
      } catch {
        if (!gone()) {
          setState('none');
        }
      }
    })();

    return () => {
      life.gone = true;
    };
  }, [asset, mk, models]);

  return (
    <div className="item-preview">
      <canvas
        ref={canvas}
        className="item-preview-canvas"
        role="img"
        aria-label="The item, turning: drag to spin it"
        title="Drag to spin it, up or down to tilt it"
        onPointerDown={(e) => {
          if (e.button !== 0) {
            return;
          }

          e.currentTarget.setPointerCapture(e.pointerId);
          drag.current = { x: e.clientX, y: e.clientY, t: performance.now() };
        }}
        onPointerMove={(e) => {
          const d = drag.current;
          if (d === null) {
            return;
          }

          const now = performance.now();
          spin.current = spinDrag(spin.current, e.clientX - d.x, e.clientY - d.y, (now - d.t) / 1000);
          drag.current = { x: e.clientX, y: e.clientY, t: now };
        }}
        onPointerUp={() => {
          drag.current = null;
        }}
        onPointerCancel={() => {
          drag.current = null;
        }}
      />
      {state === 'shown' ? null : (
        <div className="item-preview-note">{state === 'loading' ? 'Loading…' : 'No model for this item'}</div>
      )}
    </div>
  );
}

/**
 * Centres the model on the turn's pivot and backs the camera off until its bounding sphere fills {@link FILL} of the view. Measured on the loaded meshes,
 * not the descriptor's bounds: the glTF root's handedness flip (asset-frame.ts) would have to be undone by hand to use those.
 */
function frame(container: AssetContainer, turn: TransformNode, camera: ArcRotateCamera): void {
  let min = new Vector3(Infinity, Infinity, Infinity);
  let max = new Vector3(-Infinity, -Infinity, -Infinity);
  for (const node of container.rootNodes) {
    node.parent = null;
    const box = node.getHierarchyBoundingVectors(true);
    min = Vector3.Minimize(min, box.min);
    max = Vector3.Maximize(max, box.max);
  }

  if (!Number.isFinite(min.x)) {
    return;
  }

  const centre = min.add(max).scale(0.5);
  for (const node of container.rootNodes) {
    if (node instanceof TransformNode) {
      node.position.subtractInPlace(centre);
    }

    node.parent = turn;
  }

  const radius = Math.max(0.01, Vector3.Distance(min, max) / 2);
  camera.radius = radius / Math.sin((FOV * FILL) / 2);
}

/** Gives the asset's Mk material the factors of `mk`'s level, Mk I first; an item without one keeps its look. */
function tintMk(container: AssetContainer, descriptor: Descriptor, mk: number): void {
  const look = descriptor.item?.mk;
  const level = look?.levels[mk - 1];
  if (look === undefined || level === undefined) {
    return;
  }

  const name = `${descriptor.id}:${look.material}`;
  for (const material of container.materials) {
    if (material.name === name && material instanceof PBRMaterial) {
      material.albedoColor = Color3.FromArray(level.base_color);
      material.emissiveColor = Color3.FromArray(level.emissive);
      material.emissiveIntensity = level.emissive_strength;
    }
  }
}
