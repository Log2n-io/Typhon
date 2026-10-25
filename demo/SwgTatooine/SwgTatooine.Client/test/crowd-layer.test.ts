import type { Mesh } from '@babylonjs/core/Meshes/mesh';
import { describe, expect, it } from 'vitest';

import type { Descriptor } from '../src/assets/descriptor';
import type { AtlasPlan, ClipSlot } from '../src/character/clip-atlas';
import type { CrowdSkinPlugin } from '../src/render/crowd-material';
import type { CrowdOutfit } from '../src/render/crowd-mesh';
import type { CrowdWeapon } from '../src/render/crowd-weapons';
import { CrowdLayer, bucketCapacity } from '../src/render/crowd-layer';
import type { CrowdBody } from '../src/render/layer-packer';
import { CULLED, SPRITE_TIER, type ViewParams } from '../src/render/crowd-view';

/**
 * The layer that holds a body's identity across frames.
 *
 * <b>This file had four of the six defects a review found, and no test.</b> Three of them — a body's clip restarting
 * when the packer reshuffled its list, the tuning rebuild that ran every frame because `NaN !== NaN`, and the slot
 * reuse that handed one body another's animation graph — are all about state surviving between `update` calls, which
 * needs no scene at all. Only the meshes do, and a mesh here is four fields.
 */

const FPS = 30;

/** Rows for a role, so a carried body's clip can be told from a restarted one. */
const ROWS: Record<string, ClipSlot> = {
  idle: { startRow: 0, frames: 125, loop: true, source: 'a', clip: 'Idle' },
  walk: { startRow: 125, frames: 38, loop: true, source: 'a', clip: 'Walk' },
  run: { startRow: 163, frames: 26, loop: true, source: 'a', clip: 'Run' },
  ride: { startRow: 189, frames: 40, loop: false, source: 'a', clip: 'SitDown' },
  attack: { startRow: 229, frames: 22, loop: false, source: 'a', clip: 'Punch' },
  'clip:Shoot_OneHanded': { startRow: 251, frames: 16, loop: false, source: 'a', clip: 'Shoot_OneHanded' },
  hit: { startRow: 267, frames: 19, loop: false, source: 'a', clip: 'RecieveHit' },
  death: { startRow: 286, frames: 69, loop: false, source: 'a', clip: 'Death' },
  'clip:StandUp': { startRow: 430, frames: 39, loop: false, source: 'a', clip: 'StandUp' },
};

function plan(): AtlasPlan {
  return {
    fps: FPS,
    bones: 23,
    width: 92,
    rows: 189,
    bytes: 0,
    bakes: [],
    slotOf: (_outfit, role) => ROWS[role] ?? null,
  };
}

/** A mesh as the layer uses one: thin-instance count, visibility, and the four buffers it binds. */
function fakeMesh(): Mesh {
  const buffers = new Map<string, Float32Array>();
  return {
    thinInstanceCount: 0,
    isVisible: false,
    thinInstanceSetBuffer(kind: string, data: Float32Array) {
      buffers.set(kind, data);
    },
    thinInstancePartialBufferUpdate() {
      /* the upload is the engine's business; what it uploads is the buffer above */
    },
  } as unknown as Mesh;
}

function outfit(id: string, levels = 3, height = 1.67): CrowdOutfit {
  return {
    id,
    height,
    lods: Array.from({ length: levels }, (_, l) => ({ mesh: fakeMesh(), triangles: 6000 >> l })),
    dispose() {
      /* nothing to release in a fake */
    },
  };
}

function described(): Descriptor {
  return {
    animations: {
      clips: [],
      roles: { idle: 'Idle', walk: 'Walk', run: 'Run', attack: 'Punch', death: 'Death', hit: 'RecieveHit' },
    },
  } as unknown as Descriptor;
}

function weapon(clip = 'Shoot_OneHanded', capacity = 64): CrowdWeapon {
  return {
    id: 'blaster_dl44',
    clip,
    mesh: fakeMesh(),
    triangles: 120,
    targets: [
      new Float32Array(capacity * 16),
      new Float32Array(capacity * 4),
      new Float32Array(capacity * 4),
      new Float32Array(capacity * 2),
    ],
    count: 0,
    dispose() {
      /* nothing to release in a fake */
    },
  };
}

/** A frustum that accepts everything, with the eye far enough away to land every body in one tier. */
function openView(eyeZ = 0): ViewParams {
  const planes = new Float32Array(24);
  const dirs = [
    [1, 0, 0],
    [-1, 0, 0],
    [0, 1, 0],
    [0, -1, 0],
    [0, 0, 1],
    [0, 0, -1],
  ];
  for (const [i, d] of dirs.entries()) {
    planes[i * 4] = d[0];
    planes[i * 4 + 1] = d[1];
    planes[i * 4 + 2] = d[2];
    planes[i * 4 + 3] = 1e6;
  }

  return { eyeX: 0, eyeY: 1, eyeZ: eyeZ, pixelsPerMetre: 900, planes };
}

function layerOf(outfits: readonly CrowdOutfit[], capacity = 64): CrowdLayer {
  return new CrowdLayer({
    scene: null as never,
    plan: plan(),
    outfits,
    skin: { time: 0 } as unknown as CrowdSkinPlugin,
    described: outfits.map(() => described()),
    capacity,
  });
}

/** A body at a distance, moving at `speed`. */
function body(netId: number, z: number, speed = 0, mounted = false): CrowdBody {
  return {
    netId,
    x: 0,
    y: 0,
    z,
    speed,
    heading: 0,
    mounted,
    template: -1,
    skin: 0,
    dead: false,
    outfit: -1,
    strikes: 0,
    stance: 0,
    hits: 0,
    down: 0,
    look: 0,
  };
}

describe('a body keeps its animation when the packer reshuffles the list', () => {
  it('carries its clip across an index change instead of restarting it', () => {
    const layer = layerOf([outfit('a')]);
    const bucket = layer.buckets[0];

    // Two bodies, walking. The second is at index 1.
    layer.update([body(10, 20, 1.5), body(11, 24, 1.5)], 2, openView(), 0, 1.4, 4);
    const before = bucket.animator.clipA[1 * 4 + 2];
    expect(bucket.animator.clipA[1 * 4]).toBe(ROWS.walk.startRow);

    // The body ahead leaves the frustum, so 11 arrives at index 0 — the shift that restarted every clip.
    layer.update([body(11, 24, 1.5)], 1, openView(), 0.5, 1.4, 4);
    expect(bucket.byNetId.get(11)).toBe(0);
    expect(bucket.animator.clipA[0]).toBe(ROWS.walk.startRow);
    expect(bucket.animator.clipA[2]).toBe(before);
  });

  it('keeps two bodies apart when they swap index', () => {
    const layer = layerOf([outfit('a')]);
    const bucket = layer.buckets[0];
    layer.update([body(10, 20, 0), body(11, 24, 1.5)], 2, openView(), 0, 1.4, 4);
    expect(bucket.animator.clipA[0]).toBe(ROWS.idle.startRow);
    expect(bucket.animator.clipA[4]).toBe(ROWS.walk.startRow);

    layer.update([body(11, 24, 1.5), body(10, 20, 0)], 2, openView(), 0.5, 1.4, 4);
    expect(bucket.animator.clipA[0]).toBe(ROWS.walk.startRow);
    expect(bucket.animator.clipA[4]).toBe(ROWS.idle.startRow);
  });

  it('starts a body that was not here last frame from nothing, not from the stranger in its slot', () => {
    const layer = layerOf([outfit('a')]);
    const bucket = layer.buckets[0];
    layer.update([body(10, 20, 1.5)], 1, openView(), 0, 1.4, 4);
    // A different netId arriving at the same index: a RESET renumbers, and index 0 is not an identity.
    layer.update([body(99, 20, 0)], 1, openView(), 0.5, 1.4, 4);
    expect(bucket.animator.clipA[0]).toBe(ROWS.idle.startRow);
    expect(bucket.slots[0]?.netId).toBe(99);
  });

  it('carries the tier too, so hysteresis is not lost with the index', () => {
    const layer = layerOf([outfit('a')]);
    const bucket = layer.buckets[0];
    layer.update([body(10, 20, 0), body(11, 2000, 0)], 2, openView(), 0, 1.4, 4);
    const far = bucket.tiers[1];
    expect(far).not.toBe(CULLED);
    layer.update([body(11, 2000, 0)], 1, openView(), 0.5, 1.4, 4);
    expect(bucket.tiers[0]).toBe(far);
  });
});

describe('a rider', () => {
  it('sits — the seated clip held, not restarted — raised onto the seat, and is listed for its swoop', () => {
    const layer = layerOf([outfit('a')]);
    const bucket = layer.buckets[0];
    layer.update([body(10, 20, 40, true)], 1, openView(), 0, 1.4, 4);
    const started = bucket.animator.clipA[2];
    expect(bucket.animator.clipA[0]).toBe(ROWS.ride.startRow);
    expect(bucket.animator.clipA[1]).toBe(-ROWS.ride.frames);
    expect(bucket.matrices[13]).toBeCloseTo(layer.seatY, 6);
    expect(layer.riderCount).toBe(1);
    expect(layer.riders[0]).toMatchObject({ netId: 10, z: 20 });

    // A frame later it is still the same sit, from the same start: a one-shot played again would start it over.
    layer.update([body(10, 30, 40, true)], 1, openView(), 0.5, 1.4, 4);
    expect(bucket.animator.clipA[2]).toBe(started);
  });

  it('runs again, on the ground, once it gets off', () => {
    const layer = layerOf([outfit('a')]);
    const bucket = layer.buckets[0];
    layer.update([body(10, 20, 40, true)], 1, openView(), 0, 1.4, 4);
    layer.update([body(10, 20, 4, false)], 1, openView(), 1, 1.4, 4);
    expect(bucket.animator.clipA[0]).toBe(ROWS.run.startRow);
    expect(bucket.matrices[13]).toBe(0);
    expect(layer.riderCount).toBe(0);
  });
});

describe('the gait tuning', () => {
  it('is not rebuilt every frame while the server figures are still NaN', () => {
    const layer = layerOf([outfit('a')]);
    layer.update([body(10, 20, 0)], 1, openView(), 0, Number.NaN, Number.NaN);
    const tuning = layer.buckets[0].tuning;
    // `NaN !== NaN` made this test pass on every frame before the owner block landed, rebuilding every bucket's
    // tuning and walking the whole capacity, inside the render loop.
    layer.update([body(10, 20, 0)], 1, openView(), 0.5, Number.NaN, Number.NaN);
    expect(layer.buckets[0].tuning).toBe(tuning);
  });

  it('is rebuilt, and reaches a live body, when a figure actually moves', () => {
    const layer = layerOf([outfit('a')]);
    layer.update([body(10, 20, 0)], 1, openView(), 0, 1.4, 4);
    const first = layer.buckets[0].tuning;
    layer.update([body(10, 20, 0)], 1, openView(), 0.5, 1.4, 9);
    expect(layer.buckets[0].tuning).not.toBe(first);
    expect(layer.buckets[0].slots[0]?.graph.tuning).toBe(layer.buckets[0].tuning);
  });
});

describe('the clock the shader is given', () => {
  it('is seconds since the layer began, never the caller own zero', () => {
    // Epoch seconds are about 1.79e9, where a float32 steps by 128: `crowdTime - clipStart` becomes quantisation
    // noise, every frame index leaves the texture, and the whole crowd collapses to a point with every counter
    // still healthy. The layer subtracts its own origin so no caller can hand the shader that.
    const layer = layerOf([outfit('a')]);
    const epoch = 1.79e9;
    layer.update([body(10, 20, 0)], 1, openView(), epoch, 1.4, 4);
    expect(layer.time).toBe(0);
    layer.update([body(10, 20, 0)], 1, openView(), epoch + 2.5, 1.4, 4);
    expect(layer.time).toBeCloseTo(2.5, 6);
  });
});

describe('how much a crowd costs to hold', () => {
  it('gives each outfit a share of the capacity, not all of it', () => {
    // Every bucket at full capacity sized the client for 2 048 bodies in every costume at once, when the cap is
    // 2 048 bodies in total: about 16 MB of CPU arrays and 12 MB of GPU buffers for nineteen outfits.
    expect(bucketCapacity(2048, 19)).toBeLessThan(2048 / 4);
    expect(bucketCapacity(2048, 19) * 19).toBeGreaterThan(2048);
  });

  it('keeps headroom far beyond what a hash can skew to', () => {
    // 2 048 bodies over 19 buckets expects 108 with a standard deviation near 10.
    expect(bucketCapacity(2048, 19)).toBeGreaterThan(108 + 10 * 20);
  });

  it('gives one outfit everything, and a tiny crowd a floor', () => {
    expect(bucketCapacity(2048, 1)).toBe(2048);
    expect(bucketCapacity(8, 4)).toBe(8);
  });
});

describe('what is drawn', () => {
  it('puts a body on the level its tier chose, and leaves the other levels empty', () => {
    const layer = layerOf([outfit('a')]);
    const lods = layer.buckets[0];
    layer.update([body(10, 3, 0)], 1, openView(), 0, 1.4, 4);
    expect(lods.counts[0]).toBe(1);
    expect(lods.counts[1]).toBe(0);
    expect(lods.counts[2]).toBe(0);
  });

  it('draws nothing for a body the sprite band owns, and still reports it as the crowd', () => {
    const layer = layerOf([outfit('a')]);
    layer.update([body(10, 1e5, 0)], 1, openView(), 0, 1.4, 4);
    expect(layer.tierTotals[SPRITE_TIER]).toBe(1);
    expect(layer.stats.drawCalls).toBe(0);
    // It is still a body the crowd has taken ownership of, so the packer must not also draw its box.
    expect(layer.drawn.has(10)).toBe(true);
  });

  it('reads only the first `count` of the pooled hand-over array', () => {
    const layer = layerOf([outfit('a')]);
    const pooled = [body(10, 20, 0), body(11, 20, 0), body(12, 20, 0)];
    layer.update(pooled, 1, openView(), 0, 1.4, 4);
    expect(layer.drawn.size).toBe(1);
    expect(layer.drawn.has(10)).toBe(true);
  });
});

/**
 * Another player's stance, as everyone else sees it: the weapon in its hand, and its blows played with that weapon's clip.
 * The weapon is posed by the same placement and clip state as the body holding it, so the two are compared slot for slot.
 */
describe('a body in a combat stance', () => {
  const PISTOL = 3;

  function armed(netId: number, z: number, strikes = 0, mounted = false): CrowdBody {
    return { ...body(netId, z, 0, mounted), stance: PISTOL, strikes };
  }

  it("holds its stance's weapon, posed with its own placement and clip", () => {
    const layer = layerOf([outfit('a')]);
    const blaster = weapon();
    layer.setWeapon(PISTOL, blaster);
    layer.update([body(10, 20), armed(11, 24)], 2, openView(), 0, 1.4, 4);

    const bucket = layer.buckets[0];
    const i = bucket.byNetId.get(11)!;
    expect(blaster.count).toBe(1);
    expect([...blaster.targets[0].subarray(0, 16)]).toEqual([...bucket.matrices.subarray(i * 16, i * 16 + 16)]);
    expect([...blaster.targets[1].subarray(0, 4)]).toEqual([...bucket.animator.clipA.subarray(i * 4, i * 4 + 4)]);
    expect(blaster.mesh.thinInstanceCount).toBe(1);
  });

  it('holds nothing on its swoop, dead, out of a stance, or drawn as a sprite', () => {
    const layer = layerOf([outfit('a')]);
    const blaster = weapon();
    layer.setWeapon(PISTOL, blaster);
    const corpse = { ...armed(13, 28), dead: true };
    layer.update([armed(11, 24, 0, true), body(12, 26), corpse, armed(14, 1e5)], 4, openView(), 0, 1.4, 4);
    expect(blaster.count).toBe(0);
    expect(blaster.mesh.isVisible).toBe(false);
  });

  it("plays its weapon's clip on a blow, where a body with no stance plays the attack role", () => {
    const layer = layerOf([outfit('a')]);
    layer.setWeapon(PISTOL, weapon());
    layer.update([armed(11, 24, 0), body(12, 28)], 2, openView(), 0, 1.4, 4);
    const bucket = layer.buckets[0];
    layer.update([armed(11, 24, 1), { ...body(12, 28), strikes: 1 }], 2, openView(), 0.5, 1.4, 4);

    expect(bucket.animator.clipA[bucket.byNetId.get(11)! * 4]).toBe(ROWS['clip:Shoot_OneHanded'].startRow);
    expect(bucket.animator.clipA[bucket.byNetId.get(12)! * 4]).toBe(ROWS.attack.startRow);
  });
});

/**
 * Taking a blow, going down and getting up, as everyone watching sees them: a flinch per blow landed, the death held for a
 * knock-out as for a death, and `StandUp` out of a knock-out.
 */
describe('a body that is hit and goes down', () => {
  const row = (layer: CrowdLayer, netId: number): number => {
    const bucket = layer.buckets[0];
    return bucket.animator.clipA[bucket.byNetId.get(netId)! * 4];
  };

  it('flinches at each blow it takes', () => {
    const layer = layerOf([outfit('a')]);
    layer.update([body(11, 24)], 1, openView(), 0, 1.4, 4);
    layer.update([{ ...body(11, 24), hits: 1 }], 1, openView(), 0.5, 1.4, 4);
    expect(row(layer, 11)).toBe(ROWS.hit.startRow);
  });

  /** `Defeat` ends on its feet, so a knock-out falls as a death does and holds it (Loïc, 2026-10-24). */
  it('falls with its death when knocked out, and gets up with StandUp', () => {
    const layer = layerOf([outfit('a')]);
    layer.update([body(11, 24)], 1, openView(), 0, 1.4, 4);
    layer.update([{ ...body(11, 24), down: 1 }], 1, openView(), 0.5, 1.4, 4);
    expect(row(layer, 11)).toBe(ROWS.death.startRow);

    // Still down a while later: held, not replayed, and not interrupted by a blow.
    layer.update([{ ...body(11, 24), down: 1, hits: 3 }], 1, openView(), 5, 1.4, 4);
    expect(row(layer, 11)).toBe(ROWS.death.startRow);

    // Up again; the router's count of blows taken only ever grows.
    layer.update([{ ...body(11, 24), hits: 3 }], 1, openView(), 20, 1.4, 4);
    expect(row(layer, 11)).toBe(ROWS['clip:StandUp'].startRow);
  });

  it('dies with its death', () => {
    const layer = layerOf([outfit('a')]);
    layer.update([body(11, 24)], 1, openView(), 0, 1.4, 4);
    layer.update([{ ...body(11, 24), down: 2 }], 1, openView(), 0.5, 1.4, 4);
    expect(row(layer, 11)).toBe(ROWS.death.startRow);
  });

  /** A body first seen lying down falls at once, rather than standing about until something changes. */
  it('lies down at once when first seen down', () => {
    const layer = layerOf([outfit('a')]);
    layer.update([{ ...body(11, 24), down: 1 }], 1, openView(), 0, 1.4, 4);
    expect(row(layer, 11)).toBe(ROWS.death.startRow);
  });
});
