import '@babylonjs/core/Meshes/thinInstanceMesh';
import { ShaderMaterial } from '@babylonjs/core/Materials/shaderMaterial';
import { Vector2 } from '@babylonjs/core/Maths/math.vector';
import { Mesh } from '@babylonjs/core/Meshes/mesh';
import { VertexData } from '@babylonjs/core/Meshes/mesh.vertexData';
import type { Scene } from '@babylonjs/core/scene';
import { archetypeOf, evaluateSlot, MOTION_STRIDE, NOT_FOUND, slotOf, type WorldStore } from '@typhondb/client';
import type { EventSink } from '../data/source';
import type { FrameView } from './entity-layer';
import { PrefixUploader } from './prefix-upload';
import { SCENE_GROUP } from './render-groups';
import { LINE_FRAGMENT, LINE_VERTEX } from './shaders';

const CAPACITY = 4096;
/** An attack line lives this many ticks of render time (1 s at 10 Hz). */
const LIFETIME_TICKS = 10;
const LINE_HEIGHT_M = 1.2;
const LINE_WIDTH_PX = 2;
/** Half-diagonal of a hit marker's cross, CSS pixels. */
const MARKER_PX = 5;
/** Instances: a line takes one, a marker two. */
const INSTANCES = CAPACITY * 2;

/**
 * Attack lines: each `Attack` event is drawn from attacker to target once render time reaches the event's tick, and fades
 * over a second. Both ends are evaluated from the store at render time, so a line follows the motion. When the client holds
 * only one end — the other is outside its view, or arrived as 0 — the event draws a hit marker, a small cross, at the end it
 * holds (`05-client.md` § 7).
 */
export class AttackLines implements EventSink {
  visible = true;
  /** Events drawn in the last frame: lines plus markers. */
  drawn = 0;
  /** Instances drawn in the last frame: one per line, two per marker. */
  instances = 0;

  private readonly ticks = new Float64Array(CAPACITY);
  private readonly attackers = new Uint32Array(CAPACITY);
  private readonly targets = new Uint32Array(CAPACITY);
  private head = 0;
  private count = 0;

  private readonly mesh: Mesh;
  private readonly material: ShaderMaterial;
  private readonly ends = new Float32Array(INSTANCES * 4);
  /** Per instance: alpha, and 0 for a line or ±MARKER_PX for one diagonal of a marker. */
  private readonly styles = new Float32Array(INSTANCES * 2);
  private readonly endsUploader = new PrefixUploader(this.ends, 4);
  private readonly stylesUploader = new PrefixUploader(this.styles, 2);
  private readonly endpoint = new Float64Array(MOTION_STRIDE);
  private readonly viewport = new Vector2(1, 1);
  private world: WorldStore | null = null;

  constructor(scene: Scene) {
    const mesh = new Mesh('attack-lines', scene);
    const data = new VertexData();
    data.positions = [0, -1, 0, 0, 1, 0, 1, 1, 0, 1, -1, 0];
    data.indices = [0, 1, 2, 0, 2, 3];
    data.applyToMesh(mesh, false);
    mesh.alwaysSelectAsActiveMesh = true;
    mesh.doNotSyncBoundingInfo = true;
    mesh.isPickable = false;
    mesh.renderingGroupId = SCENE_GROUP;
    mesh.thinInstanceSetBuffer('lineEnds', this.ends, 4, false);
    mesh.thinInstanceSetBuffer('lineStyle', this.styles, 2, false);
    this.mesh = mesh;

    const material = new ShaderMaterial(
      'attack-lines',
      scene,
      { vertexSource: LINE_VERTEX, fragmentSource: LINE_FRAGMENT },
      {
        attributes: ['position', 'lineEnds', 'lineStyle'],
        uniforms: ['viewProjection', 'uViewport', 'uWidth', 'uHeight'],
        needAlphaBlending: true,
      },
    );
    material.backFaceCulling = false;
    material.disableDepthWrite = true;
    material.setFloat('uWidth', LINE_WIDTH_PX);
    material.setFloat('uHeight', LINE_HEIGHT_M);
    mesh.material = material;
    this.material = material;
    this.setCount(0, 0);
  }

  bind(world: WorldStore): void {
    this.world = world;
    this.count = 0;
    this.head = 0;
    this.setCount(0, 0);
  }

  onAttack(tick: number, attackerNetId: number, targetNetId: number): void {
    if (attackerNetId === 0 && targetNetId === 0) {
      return;
    }

    const at = (this.head + this.count) % CAPACITY;
    this.ticks[at] = tick;
    this.attackers[at] = attackerNetId;
    this.targets[at] = targetNetId;
    if (this.count < CAPACITY) {
      this.count++;
    } else {
      this.head = (this.head + 1) % CAPACITY;
    }
  }

  update(view: FrameView): void {
    const world = this.world;
    const renderTime = view.renderTick + view.renderFrac;
    while (this.count > 0 && renderTime - this.ticks[this.head] > LIFETIME_TICKS) {
      this.head = (this.head + 1) % CAPACITY;
      this.count--;
    }

    if (world === null || !this.visible) {
      this.setCount(0, 0);
      return;
    }

    let n = 0;
    let events = 0;
    for (let k = 0; k < this.count; k++) {
      const e = (this.head + k) % CAPACITY;
      const age = renderTime - this.ticks[e];
      if (age < 0) {
        break;
      }

      const alpha = 1 - age / LIFETIME_TICKS;
      const hasAttacker = this.resolve(world, this.attackers[e], view);
      const ax = this.endpoint[0];
      const az = this.endpoint[1];
      const hasTarget = this.resolve(world, this.targets[e], view);
      if (hasAttacker && hasTarget) {
        this.write(n++, ax, az, this.endpoint[0], this.endpoint[1], alpha, 0);
      } else if (hasAttacker || hasTarget) {
        const x = hasTarget ? this.endpoint[0] : ax;
        const z = hasTarget ? this.endpoint[1] : az;
        this.write(n++, x, z, x, z, alpha, MARKER_PX);
        this.write(n++, x, z, x, z, alpha, -MARKER_PX);
      } else {
        continue;
      }

      events++;
    }

    this.endsUploader.upload(this.mesh.getVertexBuffer('lineEnds')?.getWrapperBuffer() ?? null, n);
    this.stylesUploader.upload(this.mesh.getVertexBuffer('lineStyle')?.getWrapperBuffer() ?? null, n);

    this.viewport.set(view.viewportWidth, view.viewportHeight);
    this.material.setVector2('uViewport', this.viewport);
    this.setCount(n, events);
  }

  dispose(): void {
    this.material.dispose();
    this.mesh.dispose();
  }

  private write(i: number, ax: number, az: number, bx: number, bz: number, alpha: number, marker: number): void {
    const b = i * 4;
    this.ends[b] = ax;
    this.ends[b + 1] = az;
    this.ends[b + 2] = bx;
    this.ends[b + 3] = bz;
    this.styles[i * 2] = alpha;
    this.styles[i * 2 + 1] = marker;
  }

  /** Evaluates a held entity's render-space position into `endpoint[0..1]`; false when the netId is 0 or not held. */
  private resolve(world: WorldStore, netId: number, view: FrameView): boolean {
    if (netId === 0) {
      return false;
    }

    const location = world.locate(netId);
    if (location === NOT_FOUND) {
      return false;
    }

    evaluateSlot(
      world.archetypeStore(archetypeOf(location)),
      slotOf(location),
      view.renderTick,
      view.renderFrac,
      this.endpoint,
      0,
    );
    this.endpoint[0] = this.endpoint[0] - view.originX;
    this.endpoint[1] = this.endpoint[1] - view.originZ;
    return true;
  }

  private setCount(n: number, events: number): void {
    this.drawn = events;
    this.instances = n;
    this.mesh.forcedInstanceCount = n;
    this.mesh.isVisible = n > 0;
  }
}
