import { Constants } from '@babylonjs/core/Engines/constants';
import { StandardMaterial } from '@babylonjs/core/Materials/standardMaterial';
import { Color3 } from '@babylonjs/core/Maths/math.color';
import { Matrix, Quaternion, Vector3 } from '@babylonjs/core/Maths/math.vector';
import { CreateCylinder } from '@babylonjs/core/Meshes/Builders/cylinderBuilder';
import type { Mesh } from '@babylonjs/core/Meshes/mesh';
import '@babylonjs/core/Meshes/thinInstanceMesh';
import type { Scene } from '@babylonjs/core/scene';
import { archetypeOf, evaluateSlot, MAX_MOTION_STRIDE, NOT_FOUND, slotOf, type WorldStore } from '@typhondb/client';

import { altitudeField, altitudeOf, Placement } from '../data/placement';
import type { FrameView } from './entity-layer';
import { SCENE_GROUP } from './render-groups';

/**
 * What a fight looks like: every blow the server streams (`Attack`), played when render time reaches its tick — the
 * attacker's swing, then, for a shot, a blaster bolt flying to the target, and the impact: the target's hit reaction and
 * the damage over its head.
 *
 * <b>Played at render time, not on arrival.</b> Bodies are drawn a render delay behind the newest tick; a swing played when
 * its event ARRIVES would land before the bodies it belongs to are where the server had them. A queue ordered by tick,
 * drained as render time passes each, keeps the blow with the bodies.
 *
 * <b>Everyone's fights</b>: the server routes a blow to every session that knows either end, so a viewer sees the
 * creatures, the crowd and the other players fighting as well as itself.
 */
export interface CombatFxHooks {
  /** The attacker's swing, played now. */
  strike(attackerNetId: number): void;
  /** The blow arrived: the target's hit reaction and the damage number. Called once per blow, the bolt's at its impact. */
  impact(targetNetId: number, amount: number): void;
  /** Whether this attacker shoots — a bolt flies — rather than strikes in reach. */
  ranged(attackerNetId: number): boolean;
  /** How high above its feet a body is aimed at, metres. */
  aim(netId: number): number;
}

/** A bolt's speed, metres per second: slow enough to be seen crossing a street, as the films' are. */
export const BOLT_SPEED_MPS = 45;

/** A bolt's longest, metres: it grows to this leaving the muzzle and shrinks into what it hits. */
export const BOLT_LENGTH_M = 2.2;

/** How high a shooter's muzzle is above its feet, metres: a pistol held at the hip of a body 1.1 to 1.8 m tall. */
const MUZZLE_HEIGHT_M = 1.0;

/** How far forward of the shooter's centre a bolt leaves, metres, so it is not born inside the body. */
const MUZZLE_FORWARD_M = 0.45;

const QUEUE = 1024;
const BOLTS = 256;

/**
 * Where a bolt is along its flight: its head and tail, in metres from the muzzle.
 *
 * The head travels at {@link BOLT_SPEED_MPS} until it reaches the target; the tail follows {@link BOLT_LENGTH_M} behind and
 * does not stop — so the bolt grows out of the muzzle, flies at full length and shrinks into what it hits.
 *
 * @param t Seconds since it was fired.
 * @param distance Muzzle to target, metres.
 * @returns The span, whether the head has reached the target, and whether nothing of the bolt is left.
 */
export function boltSpan(
  t: number,
  distance: number,
  speed = BOLT_SPEED_MPS,
  length = BOLT_LENGTH_M,
): { head: number; tail: number; arrived: boolean; done: boolean } {
  const travelled = Math.max(0, t) * speed;
  const head = Math.min(distance, travelled);
  const tail = Math.min(distance, Math.max(0, travelled - length));
  return { head, tail, arrived: travelled >= distance, done: tail >= distance };
}

interface Bolt {
  /** Fired at, seconds on the caller's clock. */
  at: number;
  /** Muzzle, render space. */
  x: number;
  y: number;
  z: number;
  /** Unit direction. */
  dx: number;
  dy: number;
  dz: number;
  distance: number;
  target: number;
  amount: number;
  /** Whether its impact has been played. */
  hit: boolean;
}

export class CombatFx {
  /** Bolts drawn last frame. */
  bolts = 0;

  private readonly ticks = new Float64Array(QUEUE);
  private readonly attackers = new Uint32Array(QUEUE);
  private readonly targets = new Uint32Array(QUEUE);
  private readonly amounts = new Uint32Array(QUEUE);
  private head = 0;
  private count = 0;

  private readonly flying: Bolt[] = [];
  private readonly core: Mesh;
  private readonly glow: Mesh;
  private readonly coreMatrices = new Float32Array(BOLTS * 16);
  private readonly glowMatrices = new Float32Array(BOLTS * 16);
  private readonly placement = new Placement();
  private readonly endpoint = new Float64Array(MAX_MOTION_STRIDE);
  private readonly from = new Vector3();
  private readonly to = new Vector3();
  private readonly scratch = { matrix: new Matrix(), quat: new Quaternion(), pos: new Vector3(), scale: new Vector3() };
  private world: WorldStore | null = null;

  hooks: CombatFxHooks | null = null;

  constructor(scene: Scene) {
    // A red bolt with a hot core: two unit cylinders along +Y, unlit, placed and stretched per bolt. The core adds light; the
    // sheath is blended, not added — added red over bright sand washes out to a pale orange, which is not a blaster bolt.
    this.core = bolt(scene, 'bolt-core', new Color3(1, 0.92, 0.88), 1, Constants.ALPHA_ADD, this.coreMatrices);
    this.glow = bolt(scene, 'bolt-glow', new Color3(0.95, 0.04, 0.02), 0.8, Constants.ALPHA_COMBINE, this.glowMatrices);
  }

  bind(world: WorldStore): void {
    this.world = world;
    this.head = 0;
    this.count = 0;
    this.flying.length = 0;
    this.draw(0);
  }

  /** A blow the server landed, from the event stream; played once render time reaches `tick`. */
  push(tick: number, attackerNetId: number, targetNetId: number, amount: number): void {
    const at = (this.head + this.count) % QUEUE;
    this.ticks[at] = tick;
    this.attackers[at] = attackerNetId;
    this.targets[at] = targetNetId;
    this.amounts[at] = amount;
    if (this.count < QUEUE) {
      this.count++;
    } else {
      this.head = (this.head + 1) % QUEUE;
    }
  }

  /**
   * Plays the blows render time has reached and moves the bolts.
   *
   * @param view This frame's view.
   * @param nowS A monotonic clock, seconds: what a bolt's flight is timed on.
   */
  update(view: FrameView, nowS: number): void {
    const hooks = this.hooks;
    const renderTime = view.renderTick + view.renderFrac;
    while (this.count > 0 && this.ticks[this.head] <= renderTime) {
      const attacker = this.attackers[this.head];
      const target = this.targets[this.head];
      const amount = this.amounts[this.head];
      this.head = (this.head + 1) % QUEUE;
      this.count--;
      if (hooks === null) {
        continue;
      }

      if (attacker !== 0) {
        hooks.strike(attacker);
      }

      if (
        attacker === 0 ||
        target === 0 ||
        !hooks.ranged(attacker) ||
        !this.fire(view, attacker, target, amount, nowS)
      ) {
        if (target !== 0) {
          hooks.impact(target, amount);
        }
      }
    }

    let n = 0;
    for (let i = this.flying.length - 1; i >= 0; i--) {
      const b = this.flying[i];
      const span = boltSpan(nowS - b.at, b.distance);
      if (span.arrived && !b.hit) {
        b.hit = true;
        hooks?.impact(b.target, b.amount);
      }

      if (span.done) {
        this.flying.splice(i, 1);
        continue;
      }

      if (n < BOLTS && span.head > span.tail) {
        this.place(n++, b, span.head, span.tail);
      }
    }

    this.draw(n);
  }

  dispose(): void {
    this.core.dispose(false, true);
    this.glow.dispose(false, true);
  }

  /** Fires a bolt from the shooter's muzzle at the target's aim point. False when either end is not held. */
  private fire(view: FrameView, attacker: number, target: number, amount: number, nowS: number): boolean {
    const world = this.world;
    const hooks = this.hooks;
    if (
      world === null ||
      hooks === null ||
      !this.resolve(world, attacker, view, this.from) ||
      !this.resolve(world, target, view, this.to)
    ) {
      return false;
    }

    this.to.y += hooks.aim(target);
    this.from.y += MUZZLE_HEIGHT_M;
    const dx = this.to.x - this.from.x;
    const dz = this.to.z - this.from.z;
    const flat = Math.hypot(dx, dz);
    if (flat > 1e-3) {
      this.from.x += (dx / flat) * MUZZLE_FORWARD_M;
      this.from.z += (dz / flat) * MUZZLE_FORWARD_M;
    }

    const ex = this.to.x - this.from.x;
    const ey = this.to.y - this.from.y;
    const ez = this.to.z - this.from.z;
    const distance = Math.hypot(ex, ey, ez);
    if (distance < 0.05) {
      return false;
    }

    this.flying.push({
      at: nowS,
      x: this.from.x,
      y: this.from.y,
      z: this.from.z,
      dx: ex / distance,
      dy: ey / distance,
      dz: ez / distance,
      distance,
      target,
      amount,
      hit: false,
    });
    return true;
  }

  /** Writes bolt `i`'s two instances: centred on its span, stretched along it. */
  private place(i: number, b: Bolt, head: number, tail: number): void {
    const s = this.scratch;
    const mid = (head + tail) * 0.5;
    s.pos.set(b.x + b.dx * mid, b.y + b.dy * mid, b.z + b.dz * mid);
    this.to.set(b.dx, b.dy, b.dz);
    Quaternion.FromUnitVectorsToRef(Vector3.UpReadOnly, this.to, s.quat);
    const length = head - tail;
    s.scale.set(0.05, length, 0.05);
    Matrix.ComposeToRef(s.scale, s.quat, s.pos, s.matrix);
    s.matrix.copyToArray(this.coreMatrices, i * 16);
    s.scale.set(0.15, length + 0.1, 0.15);
    Matrix.ComposeToRef(s.scale, s.quat, s.pos, s.matrix);
    s.matrix.copyToArray(this.glowMatrices, i * 16);
  }

  private draw(n: number): void {
    this.bolts = n;
    for (const mesh of [this.core, this.glow]) {
      mesh.thinInstanceCount = n;
      mesh.isVisible = n > 0;
      if (n > 0) {
        mesh.thinInstancePartialBufferUpdate('matrix', n, 0);
      }
    }
  }

  /** A held entity's feet in render space, or false. */
  private resolve(world: WorldStore, netId: number, view: FrameView, out: Vector3): boolean {
    const location = world.locate(netId);
    if (location === NOT_FOUND) {
      return false;
    }

    const store = world.archetypeStore(archetypeOf(location));
    evaluateSlot(store, slotOf(location), view.renderTick, view.renderFrac, this.endpoint, 0);
    const at = this.placement.read(store.dims, this.endpoint, 0);
    out.set(
      at.x - view.originX,
      altitudeOf(altitudeField(store), slotOf(location), at, view.ground),
      at.z - view.originZ,
    );
    return true;
  }
}

function bolt(
  scene: Scene,
  name: string,
  color: Color3,
  alpha: number,
  alphaMode: number,
  matrices: Float32Array,
): Mesh {
  const mesh = CreateCylinder(name, { height: 1, diameter: 1, tessellation: 8 }, scene);
  const material = new StandardMaterial(name, scene);
  material.disableLighting = true;
  material.emissiveColor = color;
  material.alpha = alpha;
  material.alphaMode = alphaMode;
  material.backFaceCulling = false;
  material.disableDepthWrite = true;
  mesh.material = material;
  mesh.renderingGroupId = SCENE_GROUP;
  mesh.isPickable = false;
  mesh.alwaysSelectAsActiveMesh = true;
  mesh.doNotSyncBoundingInfo = true;
  mesh.thinInstanceSetBuffer('matrix', matrices, 16, false);
  mesh.thinInstanceCount = 0;
  mesh.isVisible = false;
  return mesh;
}
