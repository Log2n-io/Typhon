import { describe, expect, it } from 'vitest';

import type { Descriptor } from '../src/assets/descriptor';
import { BAKE_FPS, planClipAtlas } from '../src/character/clip-atlas';
import { CROWD_ROLES, DIVERGENT_CLIPS, RIG_OUTFIT, wearableOutfits } from '../src/character/outfits';

/**
 * Where each outfit's clips live in the one bone texture.
 *
 * <b>This is the file that decides whether the crowd animates CORRECTLY, so it is tested against the real catalogue's
 * shape rather than a convenient one.</b> The durations below are the published ones, byte-identical across all
 * nineteen characters: idle 4.1667 s, walk 1.25 s, run 0.875 s — 125, 38 and 26 rows at 30 Hz.
 *
 * The crowd spike baked one outfit's seventeen clips and played them for everyone. It plays three. The arithmetic of
 * that trade is asserted here, because "we made it correct AND smaller" is a claim, and claims get checked.
 */

const IDLE_ROWS = 125;
const WALK_ROWS = 38;
const RUN_ROWS = 26;

/** The published characters, with the real clip durations and the real role maps. */
function catalogue(): Map<string, Descriptor> {
  const ids = [
    'base_character',
    'blue_soldier_female',
    'blue_soldier_male',
    'casual2_female',
    'casual2_male',
    'casual3_female',
    'casual3_male',
    'casual_bald',
    'casual_female',
    'casual_male',
    'kimono_female',
    'kimono_male',
    'ninja_female',
    'ninja_male',
    'ninja_male_hair',
    'ninja_sand',
    'ninja_sand_female',
    'soldier_female',
    'soldier_male',
  ];

  const map = new Map<string, Descriptor>();
  for (const id of ids) {
    // `casual_bald` publishes no `Run` and fills the role with `Run_Carry` — the one naming quirk in the pack, and
    // the reason roles are resolved rather than assumed.
    const bald = id === 'casual_bald';
    const clips = [
      { name: 'Idle', duration: 4.1667, loop: true },
      { name: 'Walk', duration: 1.25, loop: true },
      { name: bald ? 'Run_Carry' : 'Run', duration: 0.875, loop: true },
    ];
    map.set(id, {
      schema: 1,
      id,
      kind: 'character',
      frame: 'glTF: metres, +Y up, right-handed',
      render: { glb: `${id}.glb`, triangles: 6492, materials: ['Skin'], joints: 23 },
      bounds: {
        render: [
          [-0.8, 0, -0.3],
          [0.8, 1.64, 0.3],
        ],
        collision: null,
      },
      collision: null,
      portals: [],
      spawns: [],
      interactions: [],
      children: [],
      animations: { clips, roles: { idle: 'Idle', walk: 'Walk', run: bald ? 'Run_Carry' : 'Run' } },
    } as unknown as Descriptor);
  }

  // A building, to prove non-characters are not laid out.
  map.set('tat_house_large', {
    schema: 1,
    id: 'tat_house_large',
    kind: 'building',
    frame: 'glTF: metres, +Y up, right-handed',
    render: { glb: 'h.glb', triangles: 5675, materials: [] },
    bounds: {
      render: [
        [0, 0, 0],
        [1, 1, 1],
      ],
      collision: null,
    },
    collision: null,
    portals: [],
    spawns: [],
    interactions: [],
    children: [],
  } as unknown as Descriptor);
  return map;
}

describe('the clip atlas', () => {
  it('bakes the rig once and shares it with every outfit that matches it', () => {
    const plan = planClipAtlas(catalogue(), 23);
    const rig = plan.slotOf(RIG_OUTFIT, 'idle');
    expect(rig).not.toBeNull();
    expect(rig?.source).toBe(RIG_OUTFIT);
    expect(rig?.startRow).toBe(0);

    // Thirteen outfits measured identical to the rig. They must point at the RIG's rows, not copies of them: that is
    // the whole reason one bake serves a crowd.
    for (const id of ['casual2_male', 'ninja_female', 'soldier_male', 'kimono_female']) {
      for (const role of CROWD_ROLES) {
        expect(plan.slotOf(id, role), `${id}/${role}`).toEqual(plan.slotOf(RIG_OUTFIT, role));
      }
    }
  });

  it('gives each measured divergence its OWN rows, which is the correctness fix', () => {
    const plan = planClipAtlas(catalogue(), 23);
    for (const [outfit, roles] of Object.entries(DIVERGENT_CLIPS)) {
      for (const role of roles ?? []) {
        const own = plan.slotOf(outfit, role);
        const rig = plan.slotOf(RIG_OUTFIT, role);
        expect(own, `${outfit}/${role} has no slot`).not.toBeNull();
        // Baked from its own GLB, at rows of its own. Sharing here is the 414 mm defect.
        expect(own?.source, `${outfit}/${role} was baked from the wrong outfit`).toBe(outfit);
        expect(own?.startRow, `${outfit}/${role} shares the rig's rows`).not.toBe(rig?.startRow);
      }
    }
  });

  it('shares the rig for roles an outfit was NOT measured to diverge on', () => {
    const plan = planClipAtlas(catalogue(), 23);
    // blue_soldier_male diverges on run (413.9 mm) and on nothing else. Its idle and walk are the rig's.
    expect(plan.slotOf('blue_soldier_male', 'run')?.source).toBe('blue_soldier_male');
    expect(plan.slotOf('blue_soldier_male', 'idle')).toEqual(plan.slotOf(RIG_OUTFIT, 'idle'));
    expect(plan.slotOf('blue_soldier_male', 'walk')).toEqual(plan.slotOf(RIG_OUTFIT, 'walk'));
  });

  it("resolves a role to the clip the rig actually has, not to the role's name", () => {
    const plan = planClipAtlas(catalogue(), 23);
    // `casual_bald` has no `Run`. Its run slot must be its own `Run_Carry` — and it must still BE a run slot.
    const slot = plan.slotOf('casual_bald', 'run');
    expect(slot?.clip).toBe('Run_Carry');
    expect(slot?.source).toBe('casual_bald');
    expect(slot?.frames).toBe(RUN_ROWS);
  });

  it('lays rows out without overlap, and covers exactly what it claims', () => {
    const plan = planClipAtlas(catalogue(), 23);
    let next = 0;
    for (const bake of plan.bakes) {
      expect(bake.startRow, 'a bake step overlaps or leaves a hole').toBe(next);
      expect(bake.frames).toBeGreaterThan(0);
      next += bake.frames;
    }

    expect(plan.rows).toBe(next);

    // Every slot addresses rows inside the texture.
    for (const id of catalogue().keys()) {
      for (const role of CROWD_ROLES) {
        const slot = plan.slotOf(id, role);
        if (slot === null) {
          continue;
        }

        expect(slot.startRow).toBeGreaterThanOrEqual(0);
        expect(slot.startRow + slot.frames).toBeLessThanOrEqual(plan.rows);
      }
    }
  });

  it('bakes each clip once, however many outfits point at it', () => {
    const plan = planClipAtlas(catalogue(), 23);
    const seen = plan.bakes.map((b) => `${b.source}/${b.clip}`);
    expect(new Set(seen).size, 'a clip is baked twice').toBe(seen.length);
  });

  it('costs fewer rows and fewer bytes than the spike it replaces', () => {
    const plan = planClipAtlas(catalogue(), 23);

    // The rig's three roles, plus one copy per measured divergence.
    const rigRows = IDLE_ROWS + WALK_ROWS + RUN_ROWS;
    const variants =
      WALK_ROWS + // base_character walk
      IDLE_ROWS + // blue_soldier_female idle
      RUN_ROWS + // blue_soldier_male run
      (IDLE_ROWS + RUN_ROWS) + // casual3_male
      (IDLE_ROWS + RUN_ROWS); // casual_bald
    expect(rigRows).toBe(189);
    expect(variants).toBe(491);
    expect(plan.rows).toBe(680);

    // A row is bones * 4 texels of RGBA32F. The spike reserved (bones + 1) * 4 and never wrote the last matrix.
    expect(plan.width).toBe(23 * 4);
    expect(plan.bytes).toBe(23 * 4 * 680 * 16);

    // <b>The claim, checked.</b> The spike baked all 17 clips of one outfit into rows of 24 matrices: 713 rows at
    // 1 536 B. This is correct where that was wrong, and still smaller on both axes.
    const spikeRows = 713;
    const spikeBytes = 24 * 4 * spikeRows * 16;
    expect(plan.rows).toBeLessThan(spikeRows);
    expect(plan.bytes).toBeLessThan(spikeBytes);
    expect(plan.bytes / (1024 * 1024)).toBeLessThan(1);
  });

  it('lays out no rows for a building', () => {
    const plan = planClipAtlas(catalogue(), 23);
    expect(plan.slotOf('tat_house_large', 'idle')).toBeNull();
    expect(plan.bakes.some((b) => b.source === 'tat_house_large')).toBe(false);
  });

  it('serves every wearable outfit a slot for every role the crowd drives', () => {
    // The property that matters at run time: whatever the packer picks, there is something to play.
    const described = catalogue();
    const plan = planClipAtlas(described, 23);
    for (const id of wearableOutfits(described)) {
      for (const role of CROWD_ROLES) {
        expect(plan.slotOf(id, role), `${id} cannot play ${role}`).not.toBeNull();
      }
    }
  });

  it('survives a catalogue with no rig in it rather than laying out nothing addressable', () => {
    // A catalogue that does not publish the pinned rig is a deployment fault. It must not produce slots pointing at
    // rows that were never baked.
    const described = catalogue();
    described.delete(RIG_OUTFIT);
    const plan = planClipAtlas(described, 23);
    for (const bake of plan.bakes) {
      expect(bake.source).not.toBe(RIG_OUTFIT);
    }

    for (const id of wearableOutfits(described)) {
      for (const role of CROWD_ROLES) {
        const slot = plan.slotOf(id, role);
        if (slot !== null) {
          expect(slot.startRow + slot.frames).toBeLessThanOrEqual(plan.rows);
        }
      }
    }
  });

  it('bakes at the rate it says it does', () => {
    const plan = planClipAtlas(catalogue(), 23);
    expect(plan.fps).toBe(BAKE_FPS);
    expect(plan.slotOf(RIG_OUTFIT, 'idle')?.frames).toBe(Math.round(4.1667 * BAKE_FPS));
    expect(plan.slotOf(RIG_OUTFIT, 'run')?.frames).toBe(Math.round(0.875 * BAKE_FPS));
  });
});
