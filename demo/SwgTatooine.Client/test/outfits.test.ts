import { describe, expect, it } from 'vitest';

import type { Descriptor } from '../src/assets/descriptor';
import {
  CROWD_ROLES,
  DIVERGENT_CLIPS,
  RIG_OUTFIT,
  needsOwnClips,
  outfitFor,
  wearableOutfits,
} from '../src/character/outfits';

/**
 * Which character a body wears, and whose animation the crowd is baked from.
 *
 * <b>The rig choice is a correctness property, not a preference, and that is what most of this file is about.</b> The
 * crowd spike chose its rig as "the outfit with the most clips". Seventeen of the nineteen characters have seventeen
 * clips, `Array.sort` is stable and the catalogue is alphabetical, so that resolved to `blue_soldier_female` — which
 * is one of the handful of models the pack gives its own animation keys, and on `Idle` is the only one that disagrees
 * with every other. The result was eighteen of nineteen agents idling with the wrong arm.
 *
 * A rule that depends on load order cannot be tested by its output, so these test the rule.
 */

/** A character descriptor with just the fields {@link wearableOutfits} reads. */
function character(id: string, roles: Record<string, string> = { idle: 'Idle', walk: 'Walk', run: 'Run' }): Descriptor {
  return {
    schema: 1,
    id,
    kind: 'character',
    frame: 'glTF: metres, +Y up, right-handed',
    render: { glb: `${id}.glb`, triangles: 3216, materials: ['Skin'] },
    bounds: { render: [[-0.8, 0, -0.3], [0.8, 1.64, 0.3]], collision: null },
    collision: null,
    portals: [],
    spawns: [],
    interactions: [],
    children: [],
    animations: { clips: [], roles },
  } as unknown as Descriptor;
}

/** Anything that is not a character. */
function building(id: string): Descriptor {
  return { ...character(id), kind: 'building', animations: undefined } as unknown as Descriptor;
}

describe('the rig the crowd is baked from', () => {
  it('is a named constant, so it cannot move when the catalogue is reordered', () => {
    // The whole defect in one assertion: the rig must be a value in the source, not a function of what loaded first.
    expect(RIG_OUTFIT).toBe('casual_male');
  });

  it('is not itself one of the outfits that carry their own keys', () => {
    // A deviant rig is the trap that was fallen into. Pinning a conformer is also what keeps the bake small: against
    // `casual_male` only five outfits diverge at all, against `blue_soldier_female` eighteen would need their own idle.
    expect(Object.keys(DIVERGENT_CLIPS)).not.toContain(RIG_OUTFIT);
    expect(needsOwnClips(RIG_OUTFIT, 'idle')).toBe(false);
    expect(needsOwnClips(RIG_OUTFIT, 'walk')).toBe(false);
    expect(needsOwnClips(RIG_OUTFIT, 'run')).toBe(false);
  });

  it('carries the MEASURED divergences, each one a visible error if it is dropped', () => {
    // Forward kinematics and four-influence skinning over the pack sources, worst vertex displacement at publish
    // scale, against `casual_male`. These five are the ones that are not 0.0 mm; the other thirteen outfits match the
    // rig exactly on every driven role, which is what makes one shared bake viable.
    //
    // The figures are in the table in `outfits.ts`. The two at ~45 mm — `casual3_male` and `casual_bald` on IDLE —
    // are the reason this is measured rather than reasoned: the pack's own notes mention only legs and fists on those
    // two, so an idle divergence is exactly what a careful reader would have left out.
    expect(DIVERGENT_CLIPS).toEqual({
      base_character: ['walk'], //        137.5 mm, 47.0 deg on UpperLeg.R
      blue_soldier_female: ['idle'], //   124.6 mm, 28.6 deg on Fist.L
      blue_soldier_male: ['run'], //      413.9 mm, 22.8 deg on Abdomen — a quarter of body height
      casual3_male: ['idle', 'run'], //    44.8 / 80.0 mm
      casual_bald: ['idle', 'run'], //     45.5 / 79.9 mm, the run through Run_Carry
    });
  });

  it('names only roles the crowd actually drives', () => {
    // A divergence on a clip nothing plays costs texture for nothing. The graph supports jump/attack/hit/death and is
    // tested, but the server replicates no events to trigger them, so they are not baked.
    for (const [outfit, roles] of Object.entries(DIVERGENT_CLIPS)) {
      for (const role of roles ?? []) {
        expect(CROWD_ROLES, `${outfit} diverges on a role the crowd never plays: ${role}`).toContain(role);
      }
    }
  });

  it('asks for an outfit-specific clip only where one was measured', () => {
    expect(needsOwnClips('blue_soldier_male', 'run')).toBe(true);
    expect(needsOwnClips('blue_soldier_male', 'idle')).toBe(false);
    expect(needsOwnClips('casual_bald', 'idle')).toBe(true);
    // An outfit nobody measured is assumed to match the rig; it is one of the thirteen.
    expect(needsOwnClips('ninja_female', 'idle')).toBe(false);
    expect(needsOwnClips('not_an_outfit', 'idle')).toBe(false);
  });
});

describe('which characters a crowd may wear', () => {
  it('selects by kind, never by the name convention', () => {
    // `casual_bald`, `ninja_male_hair` and `ninja_sand` all break the `<outfit>_<sex>` convention, so a rule written
    // against the ids drops three real characters and would do it silently.
    const described = new Map<string, Descriptor>([
      ['casual_male', character('casual_male')],
      ['casual_bald', character('casual_bald')],
      ['ninja_sand', character('ninja_sand')],
      ['ninja_male_hair', character('ninja_male_hair')],
      ['tat_house_large', building('tat_house_large')],
      ['bazaar_terminal', building('bazaar_terminal')],
    ]);
    expect(wearableOutfits(described)).toEqual(['casual_male', 'casual_bald', 'ninja_sand', 'ninja_male_hair']);
  });

  it('leaves out the bare reference body', () => {
    // `base_character` is the pack's untextured reference — skin and face, no clothes — and the only one missing a
    // clip. It is a thing to measure against, not a costume to put a citizen in.
    const described = new Map([['base_character', character('base_character')], ['casual_male', character('casual_male')]]);
    expect(wearableOutfits(described)).toEqual(['casual_male']);
  });

  it('leaves out a character that cannot be driven', () => {
    // A catalogue fault, but a crowd is not where it should be discovered. Missing locomotion means the graph would
    // throw where the body is mounted.
    const noAnim = { ...character('broken'), animations: undefined } as unknown as Descriptor;
    const noWalk = character('halting', { idle: 'Idle' });
    const described = new Map([
      ['broken', noAnim],
      ['halting', noWalk],
      ['casual_male', character('casual_male')],
    ]);
    expect(wearableOutfits(described)).toEqual(['casual_male']);
  });
});

describe('which outfit a given body wears', () => {
  it('is the same every time it is asked, because a body must not change clothes between frames', () => {
    for (const netId of [1, 2, 7, 4242, 99999]) {
      expect(outfitFor(netId, 18)).toBe(outfitFor(netId, 18));
    }
  });

  it('always lands inside the wearable list', () => {
    for (let netId = 0; netId < 2000; netId++) {
      const i = outfitFor(netId, 18);
      expect(Number.isInteger(i)).toBe(true);
      expect(i).toBeGreaterThanOrEqual(0);
      expect(i).toBeLessThan(18);
    }
  });

  it('scatters a RUN of netIds, which is how the server hands them out', () => {
    // <b>The reason this is a hash and not a modulo.</b> netIds arrive in runs, so `netId % count` dresses a whole
    // spawn wave in a repeating stripe — visible as a pattern across a street. Over 180 consecutive ids and 18
    // outfits, a modulo would use each exactly 10 times with perfect periodicity; what matters here is that no single
    // outfit dominates and that neighbours differ.
    const counts = new Array<number>(18).fill(0);
    for (let netId = 1000; netId < 1180; netId++) {
      counts[outfitFor(netId, 18)]++;
    }

    expect(Math.max(...counts), 'one outfit dominates a spawn wave').toBeLessThanOrEqual(22);
    expect(Math.min(...counts), 'an outfit never appears in a spawn wave').toBeGreaterThan(0);

    let sameAsNeighbour = 0;
    for (let netId = 1000; netId < 1179; netId++) {
      if (outfitFor(netId, 18) === outfitFor(netId + 1, 18)) {
        sameAsNeighbour++;
      }
    }

    // Independent draws would pair about 1 time in 18 (≈10 of 179). A modulo would pair 0 and stripe instead.
    expect(sameAsNeighbour).toBeLessThan(30);
  });

  it('survives a count of zero rather than returning NaN into a buffer', () => {
    // A catalogue that publishes no character is a working run, as a catalogue that publishes no assets already is.
    expect(outfitFor(1234, 0)).toBe(0);
  });
});
