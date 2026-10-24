import type { Mesh } from '@babylonjs/core/Meshes/mesh';
import { describe, expect, it } from 'vitest';

import { CrowdRouter } from '../src/app/crowd-router';
import type { Descriptor } from '../src/assets/descriptor';
import { lairQuarter } from '../src/assets/structure-assets';
import { creatureAtlas, planClipAtlas, type AtlasPlan, type ClipSlot } from '../src/character/clip-atlas';
import { enemySkins, locomotionRoles } from '../src/character/outfits';
import { readCreatureKinds, type CreatureKind } from '../src/data/server-config';
import type { CrowdSkinPlugin } from '../src/render/crowd-material';
import type { CrowdOutfit } from '../src/render/crowd-mesh';
import { CrowdLayer } from '../src/render/crowd-layer';
import type { ViewParams } from '../src/render/crowd-view';
import type { CrowdBody } from '../src/render/layer-packer';

/**
 * Creatures and enemies drawn as themselves: a species' own atlas, the router that dresses each body, and the one-shots the
 * server's state and events drive — a death held until the corpse is destroyed, a blow per `Attack`.
 */

/** The published snake, as its descriptor has it: no run, and a one-shot attack and death. */
function snake(): Descriptor {
  return {
    schema: 1,
    id: 'snake',
    kind: 'creature',
    render: { glb: 'snake-1.glb', triangles: 1618, materials: [], skinned: true, joints: 15 },
    bounds: {
      render: [
        [-0.2, 0, -0.5],
        [0.2, 1, 0.5],
      ],
      collision: null,
    },
    animations: {
      clips: [
        { name: 'Attack', duration: 0.7, loop: false },
        { name: 'Death', duration: 1.1, loop: false },
        { name: 'Idle', duration: 2, loop: true },
        { name: 'Walk', duration: 1.3, loop: true },
      ],
      roles: { idle: 'Idle', walk: 'Walk', attack: 'Attack', death: 'Death' },
    },
  } as unknown as Descriptor;
}

describe('a creature atlas', () => {
  it('is its own rig, and plays its walk for the run it does not have from ONE copy of the rows', () => {
    const plan = planClipAtlas(new Map([['snake', snake()]]), 15, 30, creatureAtlas('snake'));
    const walk = plan.slotOf('snake', 'walk');
    expect(walk?.source).toBe('snake');
    expect(plan.slotOf('snake', 'run')).toEqual(walk);
    // Idle, walk, attack, death: four clips baked, the run sharing the walk's rows rather than duplicating them.
    expect(plan.bakes.map((b) => b.clip)).toEqual(['Idle', 'Walk', 'Attack', 'Death']);
    expect(plan.slotOf('snake', 'attack')?.loop).toBe(false);
    expect(plan.slotOf('snake', 'death')?.loop).toBe(false);
    // A creature never rides.
    expect(plan.slotOf('snake', 'ride')).toBeNull();
  });

  it('fills the locomotion a creature lacks, and leaves a full map alone', () => {
    const full = { idle: 'Idle', walk: 'Walk', run: 'Run' };
    expect(locomotionRoles(full)).toBe(full);
    expect(locomotionRoles({ idle: 'Flying', attack: 'Attack' })).toEqual({
      idle: 'Flying',
      walk: 'Flying',
      run: 'Flying',
      attack: 'Attack',
    });
  });
});

const KINDS: CreatureKind[] = [
  { name: 'rats', lair: 'creature_nest', bodies: ['rat'] },
  { name: 'ninja hideout', lair: 'ninja_hideout', bodies: ['ninja_male', 'ninja_female'] },
  { name: 'unbuilt', lair: 'creature_nest', bodies: ['dragon'] },
];

function record(netId: number, template = -1, skin = 0): CrowdBody {
  return {
    netId,
    x: 0,
    y: 0,
    z: 0,
    speed: 0,
    heading: 0,
    mounted: false,
    template,
    skin,
    dead: false,
    outfit: -1,
    strikes: 0,
  };
}

describe('the crowd router', () => {
  const router = (): CrowdRouter => new CrowdRouter(KINDS, ['casual_male', 'ninja_male', 'ninja_female'], ['rat']);

  it('dresses a civilian by netId, a camp in its outfit, and sends a beast to its species', () => {
    const r = router();
    const bodies = [record(1), record(2, 1, 1), record(3, 0)];
    r.route(bodies, 3);
    expect(r.layers[0].count).toBe(2);
    expect(r.layers[0].bodies[0].outfit).toBe(-1);
    expect(r.layers[0].bodies[1].outfit).toBe(2);
    expect(r.layers[1].count).toBe(1);
    expect(r.layers[1].bodies[0].netId).toBe(3);
  });

  it('drops a creature whose asset no crowd built, and a template the table does not name, and counts both', () => {
    const r = router();
    r.route([record(4, 2), record(5, 9)], 2);
    expect(r.unrouted).toBe(2);
    expect(r.layers[0].count + r.layers[1].count).toBe(0);
  });

  it('counts each Attack against its attacker, so two blows between frames are two', () => {
    const r = router();
    r.strike(3);
    r.strike(3);
    const bodies = [record(3, 0), record(1)];
    r.route(bodies, 2);
    expect(bodies[0].strikes).toBe(2);
    expect(bodies[1].strikes).toBe(0);
  });

  it('keeps enemy outfits off civilians', () => {
    expect([...enemySkins(KINDS)].sort()).toEqual(['dragon', 'ninja_female', 'ninja_male', 'rat']);
  });
});

describe('the published creature table', () => {
  it('keeps a malformed entry in its place, so the templates after it keep their numbers', () => {
    const kinds = readCreatureKinds([
      { name: 'rats', lair: 'creature_nest', bodies: ['rat'] },
      7,
      { name: 'frogs', bodies: ['frog', 3] },
    ]);
    expect(kinds.map((k) => k.name)).toEqual(['rats', '', 'frogs']);
    expect(kinds[2].bodies).toEqual(['frog']);
    expect(kinds[2].lair).toBe('');
  });

  it('is empty from a server that publishes none', () => {
    expect(readCreatureKinds(undefined)).toEqual([]);
  });
});

describe('a lair faces a quarter turn of its own', () => {
  it('is one of the four, and the same for the same place', () => {
    const seen = new Set<number>();
    for (let i = 0; i < 200; i++) {
      const q = lairQuarter(i * 37.5 - 4000, i * -91.25 + 1200);
      expect(q).toBeGreaterThanOrEqual(0);
      expect(q).toBeLessThanOrEqual(3);
      expect(lairQuarter(i * 37.5 - 4000, i * -91.25 + 1200)).toBe(q);
      seen.add(q);
    }

    expect(seen.size).toBe(4);
  });
});

// ---- the one-shots, on a layer with fake meshes (the same shape `crowd-layer.test.ts` uses) -------------------------

const ROWS: Record<string, ClipSlot> = {
  idle: { startRow: 0, frames: 60, loop: true, source: 'snake', clip: 'Idle' },
  walk: { startRow: 60, frames: 39, loop: true, source: 'snake', clip: 'Walk' },
  run: { startRow: 60, frames: 39, loop: true, source: 'snake', clip: 'Walk' },
  attack: { startRow: 99, frames: 21, loop: false, source: 'snake', clip: 'Attack' },
  death: { startRow: 120, frames: 33, loop: false, source: 'snake', clip: 'Death' },
};

function plan(): AtlasPlan {
  return { fps: 30, bones: 15, width: 60, rows: 153, bytes: 0, bakes: [], slotOf: (_o, role) => ROWS[role] ?? null };
}

function fakeMesh(): Mesh {
  return {
    thinInstanceCount: 0,
    isVisible: false,
    thinInstanceSetBuffer() {
      /* bound once */
    },
    thinInstancePartialBufferUpdate() {
      /* the upload is the engine's */
    },
  } as unknown as Mesh;
}

function outfit(id: string): CrowdOutfit {
  return {
    id,
    height: 1,
    lods: [{ mesh: fakeMesh(), triangles: 1000 }],
    dispose() {
      /* nothing to release */
    },
  };
}

function view(): ViewParams {
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

  return { eyeX: 0, eyeY: 1, eyeZ: -10, pixelsPerMetre: 900, planes };
}

function layer(ids: readonly string[], hashed?: readonly number[]): CrowdLayer {
  return new CrowdLayer({
    scene: null as never,
    plan: plan(),
    outfits: ids.map(outfit),
    skin: { time: 0 } as unknown as CrowdSkinPlugin,
    described: ids.map(() => snake()),
    capacity: 64,
    ...(hashed === undefined ? {} : { hashed }),
  });
}

/** The start row and frame count a body plays, and when its clip started. */
function clipOf(l: CrowdLayer, bucket = 0, i = 0): { row: number; frames: number; started: number } {
  const a = l.buckets[bucket].animator.clipA;
  return { row: a[i * 4], frames: a[i * 4 + 1], started: a[i * 4 + 2] };
}

describe('a creature on the crowd', () => {
  it('plays its death once and HOLDS it, frame after frame, until it is gone', () => {
    const l = layer(['snake']);
    const b = record(7, 0);
    l.update([b], 1, view(), 0, Number.NaN, Number.NaN);
    expect(clipOf(l).row).toBe(ROWS.idle.startRow);

    b.dead = true;
    l.update([b], 1, view(), 0.5, Number.NaN, Number.NaN);
    const death = clipOf(l);
    expect(death.row).toBe(ROWS.death.startRow);
    // Negative: a one-shot that holds its last frame.
    expect(death.frames).toBe(-ROWS.death.frames);

    // Asked again on later frames, the same death — not restarted, which would loop the fall for six seconds.
    l.update([b], 1, view(), 1.5, Number.NaN, Number.NaN);
    l.update([b], 1, view(), 4, Number.NaN, Number.NaN);
    expect(clipOf(l)).toEqual(death);
  });

  it('swings once per blow the server streams, and a second blow swings again', () => {
    const l = layer(['snake']);
    const b = record(8, 0);
    l.update([b], 1, view(), 0, Number.NaN, Number.NaN);

    b.strikes = 1;
    l.update([b], 1, view(), 0.1, Number.NaN, Number.NaN);
    const first = clipOf(l);
    expect(first.row).toBe(ROWS.attack.startRow);
    l.update([b], 1, view(), 0.2, Number.NaN, Number.NaN);
    expect(clipOf(l).started).toBe(first.started);

    // After the swing has ended, the next blow is a new one.
    b.strikes = 2;
    l.update([b], 1, view(), 1.5, Number.NaN, Number.NaN);
    expect(clipOf(l).row).toBe(ROWS.attack.startRow);
    expect(clipOf(l).started).toBeGreaterThan(first.started);
  });

  it('does not owe a body the blows it struck before it came into view', () => {
    const l = layer(['snake']);
    const b = record(9, 0);
    b.strikes = 5;
    l.update([b], 1, view(), 0, Number.NaN, Number.NaN);
    expect(clipOf(l).row).toBe(ROWS.idle.startRow);
  });

  it('wears the outfit it was dressed in, and a civilian only the wardrobe it was given', () => {
    const l = layer(['casual', 'ninja'], [0]);
    const ninja = record(10, 1);
    ninja.outfit = 1;
    const civilians = Array.from({ length: 30 }, (_, i) => record(100 + i));
    l.update([ninja, ...civilians], 31, view(), 0, Number.NaN, Number.NaN);
    expect(l.buckets[1].count).toBe(1);
    expect(l.buckets[0].count).toBe(30);
  });
});
