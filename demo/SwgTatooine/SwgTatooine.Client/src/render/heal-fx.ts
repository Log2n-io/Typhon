import { Constants } from '@babylonjs/core/Engines/constants';
import { StandardMaterial } from '@babylonjs/core/Materials/standardMaterial';
import { Color3 } from '@babylonjs/core/Maths/math.color';
import { Matrix, Quaternion, Vector3 } from '@babylonjs/core/Maths/math.vector';
import { CreateSphere } from '@babylonjs/core/Meshes/Builders/sphereBuilder';
import { CreateTorus } from '@babylonjs/core/Meshes/Builders/torusBuilder';
import type { Mesh } from '@babylonjs/core/Meshes/mesh';
import '@babylonjs/core/Meshes/thinInstanceMesh';
import type { Scene } from '@babylonjs/core/scene';
import { archetypeOf, evaluateSlot, MAX_MOTION_STRIDE, NOT_FOUND, slotOf, type WorldStore } from '@typhondb/client';

import { altitudeField, altitudeOf, Placement } from '../data/placement';
import type { FrameView } from './entity-layer';
import { SCENE_GROUP } from './render-groups';

/**
 * A potion drunk (`Healed`, crafting-catalog.md § 5.4; Loïc, 2026-11-05): green motes spiralling up round the body that drank, out of a ring of light that
 * spreads at its feet — seen by everyone who knows the body, as a blow is. It follows the body for its {@link HEAL_FX_MS}.
 *
 * <b>Played at render time</b>, as {@link CombatFx}'s blows are: a heal queued by its tick plays when the bodies drawn a render delay behind reach it, so
 * the green rises as the health bar fills.
 *
 * <b>Two draw calls for every heal</b>: the motes are thin instances of one small sphere, the rings of one flat torus, both unlit and added to what is
 * behind them; each is placed per frame from its age, and shrinks to nothing at its end rather than fading — instances carry no alpha of their own.
 */
export interface HealFxHooks {
  /** The heal lands, at render time: the number over the body, and — for this player's own body — the slot's cooldown. */
  healed(netId: number, amount: number): void;
}

/** How long one heal shows, ms. */
export const HEAL_FX_MS = 1_800;

/** Motes a heal rises in. */
export const HEAL_MOTES = 14;

/** How high the motes climb over the heal, metres: a body's height and a little over. */
const RISE_M = 2.2;

/** The spiral's radius round the body's axis, metres. */
const RADIUS_M = 0.55;

/** Turns the spiral makes over its climb. */
const TURNS = 1.25;

/** A mote's diameter at its fullest, metres. */
const MOTE_M = 0.13;

/** The ring at the feet: its starting and widest diameter, metres, and when it is gone, as a share of the heal. */
const RING_FROM_M = 0.5;
const RING_TO_M = 2.4;
const RING_SHARE = 0.55;

const QUEUE = 256;
const LIVE = 64;

/** A mote's place, relative to the body's feet, metres, and its size (0 once it is gone). Written in place: the frame keeps one. */
export interface Mote {
  x: number;
  y: number;
  z: number;
  size: number;
}

/** The ring's diameter, metres, and its thickness scale (0 once it is gone). Written in place. */
export interface Ring {
  diameter: number;
  scale: number;
}

/**
 * Where one mote is `t` seconds into its heal: staggered so they leave the ground one after the other, rising fast and slowing, turning round the body,
 * shrinking out at the top.
 *
 * @param i The mote, 0..{@link HEAL_MOTES} − 1.
 * @param t Seconds since the heal landed.
 * @param out Written and returned when given; a new one otherwise.
 */
export function moteAt(i: number, t: number, out: Mote = { x: 0, y: 0, z: 0, size: 0 }): Mote {
  const span = HEAL_FX_MS / 1000;
  const delay = (i / HEAL_MOTES) * span * 0.45;
  const life = span - delay;
  const u = (t - delay) / life;
  if (u < 0 || u >= 1) {
    out.x = out.y = out.z = out.size = 0;
    return out;
  }

  const rise = 1 - (1 - u) * (1 - u);
  const angle = i * Math.PI * 2 * 0.618 + u * TURNS * Math.PI * 2;
  const radius = RADIUS_M * (0.85 + 0.15 * Math.sin(i * 1.7));
  // In at the start, out over the last third.
  out.x = Math.cos(angle) * radius;
  out.y = 0.1 + rise * RISE_M;
  out.z = Math.sin(angle) * radius;
  out.size = MOTE_M * Math.min(1, u * 6) * Math.min(1, (1 - u) * 3);
  return out;
}

/** The ring `t` seconds into the heal. */
export function ringAt(t: number, out: Ring = { diameter: 0, scale: 0 }): Ring {
  const u = t / ((HEAL_FX_MS / 1000) * RING_SHARE);
  if (u < 0 || u >= 1) {
    out.diameter = out.scale = 0;
    return out;
  }

  const spread = 1 - (1 - u) * (1 - u);
  out.diameter = RING_FROM_M + (RING_TO_M - RING_FROM_M) * spread;
  out.scale = 1 - u;
  return out;
}

interface Live {
  netId: number;
  /** Landed at, seconds on the caller's clock. */
  at: number;
  /** Where the body was last seen, render space: a body that leaves the view keeps its heal where it was. */
  readonly feet: Vector3;
  seen: boolean;
}

export class HealFx {
  /** Heals shown last frame. */
  shown = 0;

  private readonly ticks = new Float64Array(QUEUE);
  private readonly bodies = new Uint32Array(QUEUE);
  private readonly amounts = new Uint32Array(QUEUE);
  private head = 0;
  private count = 0;

  private readonly live: Live[] = [];
  private readonly motes: Mesh;
  private readonly rings: Mesh;
  private readonly moteMatrices = new Float32Array(LIVE * HEAL_MOTES * 16);
  private readonly ringMatrices = new Float32Array(LIVE * 16);
  private readonly placement = new Placement();
  private readonly endpoint = new Float64Array(MAX_MOTION_STRIDE);
  private readonly feet = new Vector3();
  private readonly scratch = {
    matrix: new Matrix(),
    quat: Quaternion.Identity(),
    pos: new Vector3(),
    scale: new Vector3(),
  };
  private readonly mote: Mote = { x: 0, y: 0, z: 0, size: 0 };
  private readonly ring: Ring = { diameter: 0, scale: 0 };
  private world: WorldStore | null = null;

  hooks: HealFxHooks | null = null;

  constructor(scene: Scene) {
    this.motes = glow(
      CreateSphere('heal-mote', { diameter: 1, segments: 4 }, scene),
      scene,
      'heal-mote',
      new Color3(0.35, 1, 0.5),
    );
    this.motes.thinInstanceSetBuffer('matrix', this.moteMatrices, 16, false);
    this.rings = glow(
      CreateTorus('heal-ring', { diameter: 1, thickness: 0.06, tessellation: 40 }, scene),
      scene,
      'heal-ring',
      new Color3(0.2, 0.85, 0.4),
    );
    this.rings.thinInstanceSetBuffer('matrix', this.ringMatrices, 16, false);
    this.draw(0, 0);
  }

  bind(world: WorldStore): void {
    this.world = world;
    this.head = 0;
    this.count = 0;
    this.live.length = 0;
    this.draw(0, 0);
  }

  /** A potion drunk, from the event stream; played once render time reaches `tick`. */
  push(tick: number, netId: number, amount: number): void {
    const at = (this.head + this.count) % QUEUE;
    this.ticks[at] = tick;
    this.bodies[at] = netId;
    this.amounts[at] = amount;
    if (this.count < QUEUE) {
      this.count++;
    } else {
      this.head = (this.head + 1) % QUEUE;
    }
  }

  /**
   * Starts the heals render time has reached and places every one showing.
   *
   * @param nowS A monotonic clock, seconds: what a heal's age is timed on.
   */
  update(view: FrameView, nowS: number): void {
    const renderTime = view.renderTick + view.renderFrac;
    while (this.count > 0 && this.ticks[this.head] <= renderTime) {
      const netId = this.bodies[this.head];
      const amount = this.amounts[this.head];
      this.head = (this.head + 1) % QUEUE;
      this.count--;
      if (netId === 0) {
        continue;
      }

      this.hooks?.healed(netId, amount);
      if (this.live.length >= LIVE) {
        this.live.shift();
      }

      this.live.push({ netId, at: nowS, feet: new Vector3(), seen: false });
    }

    const span = HEAL_FX_MS / 1000;
    let motes = 0;
    let rings = 0;
    for (let k = this.live.length - 1; k >= 0; k--) {
      const h = this.live[k];
      const t = nowS - h.at;
      if (t >= span) {
        this.live.splice(k, 1);
        continue;
      }

      if (this.world !== null && this.resolve(this.world, h.netId, view, this.feet)) {
        h.feet.copyFrom(this.feet);
        h.seen = true;
      }

      if (!h.seen) {
        continue;
      }

      for (let i = 0; i < HEAL_MOTES; i++) {
        const m = moteAt(i, t, this.mote);
        if (m.size > 0) {
          this.place(
            this.moteMatrices,
            motes++,
            h.feet.x + m.x,
            h.feet.y + m.y,
            h.feet.z + m.z,
            m.size,
            m.size,
            m.size,
          );
        }
      }

      const ring = ringAt(t, this.ring);
      if (ring.scale > 0) {
        this.place(
          this.ringMatrices,
          rings++,
          h.feet.x,
          h.feet.y + 0.05,
          h.feet.z,
          ring.diameter,
          ring.scale,
          ring.diameter,
        );
      }
    }

    this.draw(motes, rings);
  }

  dispose(): void {
    this.motes.dispose(false, true);
    this.rings.dispose(false, true);
  }

  private place(
    into: Float32Array,
    i: number,
    x: number,
    y: number,
    z: number,
    sx: number,
    sy: number,
    sz: number,
  ): void {
    const s = this.scratch;
    s.pos.set(x, y, z);
    s.scale.set(sx, sy, sz);
    Matrix.ComposeToRef(s.scale, s.quat, s.pos, s.matrix);
    s.matrix.copyToArray(into, i * 16);
  }

  private draw(motes: number, rings: number): void {
    this.shown = this.live.length;
    show(this.motes, motes);
    show(this.rings, rings);
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

/** Draws a mesh's first `n` instances, or hides it with none. */
function show(mesh: Mesh, n: number): void {
  mesh.thinInstanceCount = n;
  mesh.isVisible = n > 0;
  if (n > 0) {
    mesh.thinInstancePartialBufferUpdate('matrix', n, 0);
  }
}

/** An unlit green mesh, added to what is behind it, never written to depth: light, not a solid. */
function glow(mesh: Mesh, scene: Scene, name: string, color: Color3): Mesh {
  const material = new StandardMaterial(name, scene);
  material.disableLighting = true;
  material.emissiveColor = color;
  material.alphaMode = Constants.ALPHA_ADD;
  material.alpha = 0.9;
  material.backFaceCulling = false;
  material.disableDepthWrite = true;
  mesh.material = material;
  mesh.isPickable = false;
  mesh.doNotSyncBoundingInfo = true;
  mesh.alwaysSelectAsActiveMesh = true;
  mesh.renderingGroupId = SCENE_GROUP;
  return mesh;
}
