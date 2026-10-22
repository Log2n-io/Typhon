import type { AbstractMesh } from '@babylonjs/core/Meshes/abstractMesh';
import { describe, expect, it } from 'vitest';

import { groupLevels } from '../src/app/static-assets';
import { LEVEL_HYSTERESIS, NO_LEVEL, levelForError, lodLevelOf } from '../src/render/asset-lod';

/**
 * Choosing a published asset's detail level by what the viewer would lose.
 *
 * <b>These are written against the real published figures</b> (`tat_house_large`: 0, 0.10 m, 0.25 m — catalogue
 * `f131e3e086ab`), because the arithmetic only means anything at the scale the pack actually ships. A test with
 * round numbers would have passed just as happily with the error in centimetres.
 */

/** The errors the pack publishes for a large house, finest first. */
const HOUSE = [0, 0.1, 0.25];

/** The shuttleport's, which are not the house's — a level is the ASSET's measurement, never a shared ladder. */
const SHUTTLEPORT = [0, 0.12, 0.31];

/** 1080p at a 1.1 rad vertical field of view, which is what the client renders at. */
const PPM = 775;

describe('reading a level from a node name', () => {
  it('calls an unsuffixed node level 0', () => {
    expect(lodLevelOf('_x_tat_house_large_primitive0')).toBe(0);
  });

  it('reads the level through a primitive suffix, which is where Babylon puts it', () => {
    expect(lodLevelOf('_x_tat_house_large_LOD1_primitive3')).toBe(1);
    expect(lodLevelOf('_x_tat_house_large_LOD2')).toBe(2);
  });

  it('reads a placement label without being confused by it', () => {
    expect(lodLevelOf('tat_house_large#199550/_x_tat_house_large_LOD2')).toBe(2);
  });

  it('takes the LAST level in the name, so an id that contains one cannot win', () => {
    // Babylon prefixes every placed mesh with the renderer's label, and the label contains the asset id.
    expect(lodLevelOf('bunker_LOD3_kit#4/_x_bunker_LOD3_kit')).toBe(0);
    expect(lodLevelOf('bunker_LOD3_kit#4/_x_bunker_LOD3_kit_LOD1_primitive0')).toBe(1);
  });
});

describe('choosing a level', () => {
  it('draws the finest level up close, however coarse the ladder is', () => {
    expect(levelForError(HOUSE, PPM, 10, 1.5)).toBe(0);
  });

  it('takes the coarsest level the tolerance still affords', () => {
    // LOD2's 0.25 m is 0.25 * 775 / 200 = 0.97 px at 200 m, inside a 1.5 px budget.
    expect(levelForError(HOUSE, PPM, 200, 1.5)).toBe(2);
  });

  it('is driven by the ASSET own errors, so two assets swap at different distances', () => {
    // A first choice is judged against the strict side of the band, 1.2 px. At 100 m the house's LOD2 costs 1.94 px
    // and the shuttleport's 2.40: both too much, both on LOD1.
    expect(levelForError(HOUSE, PPM, 100, 1.5)).toBe(1);
    expect(levelForError(SHUTTLEPORT, PPM, 100, 1.5)).toBe(1);
    // …and by 180 m the house's LOD2 has come down to 1.08 px and is taken, while the shuttleport's coarser LOD2 is
    // still 1.33 px and is not. One distance, two answers, because the two assets measured themselves.
    expect(levelForError(HOUSE, PPM, 180, 1.5)).toBe(2);
    expect(levelForError(SHUTTLEPORT, PPM, 180, 1.5)).toBe(1);
  });

  it('spends the tolerance, so a tighter budget keeps more detail at the same distance', () => {
    expect(levelForError(HOUSE, PPM, 120, 3)).toBe(2);
    expect(levelForError(HOUSE, PPM, 120, 1.5)).toBe(1);
    expect(levelForError(HOUSE, PPM, 120, 0.1)).toBe(0);
  });

  it('scales with the camera, so the same distance at a narrower field of view keeps more detail', () => {
    // The threshold is in SCREEN pixels, so zooming four times in is four times the error for the same building at
    // the same distance: the house that was happy on LOD2 at 200 m goes back to its finest mesh without moving.
    expect(levelForError(HOUSE, PPM, 200, 1.5)).toBe(2);
    expect(levelForError(HOUSE, PPM * 4, 200, 1.5)).toBe(0);
  });

  it('never leaves the ladder, whatever the distance', () => {
    expect(levelForError(HOUSE, PPM, 1e6, 1.5)).toBe(HOUSE.length - 1);
    expect(levelForError(HOUSE, PPM, 0, 1.5)).toBe(0);
    expect(levelForError(HOUSE, PPM, -5, 1.5)).toBe(0);
  });

  it('draws level 0 for an asset that publishes one level', () => {
    expect(levelForError([0], PPM, 5000, 1.5)).toBe(0);
    expect(levelForError([], PPM, 5000, 1.5)).toBe(0);
  });

  describe('hysteresis, so a building on the boundary does not swap mesh every frame', () => {
    /** The distance at which LOD2's error is exactly the tolerance. */
    const boundary = (0.25 * PPM) / 1.5;

    it('enters a coarser level later than it leaves it', () => {
      const entering = levelForError(HOUSE, PPM, boundary, 1.5, 1);
      const leaving = levelForError(HOUSE, PPM, boundary, 1.5, 2);
      expect(entering).toBe(1);
      expect(leaving).toBe(2);
    });

    it('still enters once the error is clear of the band', () => {
      expect(levelForError(HOUSE, PPM, boundary / (1 - LEVEL_HYSTERESIS), 1.5, 1)).toBe(2);
    });

    it('still leaves once the error is clear of the band', () => {
      expect(levelForError(HOUSE, PPM, boundary * (1 - LEVEL_HYSTERESIS), 1.5, 2)).toBe(1);
    });

    it('does not apply to a placement with no level yet, which has nothing to be sticky about', () => {
      expect(levelForError(HOUSE, PPM, boundary, 1.5, NO_LEVEL)).toBe(1);
    });
  });
});

describe('sorting a placement meshes into levels', () => {
  /** A mesh as `groupLevels` reads one: a name and whether it has any geometry. */
  const mesh = (name: string, vertices = 100): AbstractMesh =>
    ({ name, getTotalVertices: () => vertices }) as unknown as AbstractMesh;

  const lods = [
    { node: 'Body', triangles: 5675, error_m: 0 },
    { node: 'Body_LOD1', triangles: 3032, error_m: 0.1 },
    { node: 'Body_LOD2', triangles: 721, error_m: 0.25 },
  ];

  it('groups every primitive of a level together and reads that level error', () => {
    const { levels, errors } = groupLevels(
      [
        mesh('h/_x_house_primitive0'),
        mesh('h/_x_house_primitive1'),
        mesh('h/_x_house_LOD1_primitive0'),
        mesh('h/_x_house_LOD2'),
      ],
      lods,
    );
    expect(levels.map((l) => l.length)).toEqual([2, 1, 1]);
    expect(errors).toEqual([0, 0.1, 0.25]);
  });

  it('ignores the empty decoy nodes the exporter leaves behind, which share the level names', () => {
    const { levels } = groupLevels(
      [mesh('h/_x_house_primitive0'), mesh('h/Body_LOD1', 0), mesh('h/_x_house_LOD1'), mesh('h/_x_house_LOD2')],
      lods,
    );
    expect(levels.map((l) => l.length)).toEqual([1, 1, 1]);
  });

  it('says an asset with one published level has no levels to choose between', () => {
    expect(groupLevels([mesh('t/_x_terminal_primitive0')], null).levels).toEqual([]);
    expect(groupLevels([mesh('t/_x_terminal_primitive0')], [lods[0]]).levels).toEqual([]);
  });

  it('refuses a ladder whose coarse level publishes no error, which would otherwise win at every distance', () => {
    // The whole rule is `error_m * ppm / d <= tol`. A missing error reads as 0, 0 clears any tolerance, and the
    // coarsest level is then chosen in the doorway you are standing in — silently, and at every distance.
    const unmeasured = [lods[0], { node: 'Body_LOD1', triangles: 3032 }, lods[2]];
    expect(groupLevels([mesh('h/_x_house_primitive0'), mesh('h/_x_house_LOD1'), mesh('h/_x_house_LOD2')], unmeasured))
      .toEqual({ levels: [], errors: [] });
  });

  it('refuses a ladder that does not get coarser, because the search assumes it does', () => {
    const scrambled = [lods[0], { node: 'Body_LOD1', triangles: 3032, error_m: 0.4 }, lods[2]];
    expect(groupLevels([mesh('h/_x_house_primitive0'), mesh('h/_x_house_LOD1'), mesh('h/_x_house_LOD2')], scrambled))
      .toEqual({ levels: [], errors: [] });
  });

  it('keeps a mesh past the declared ladder, with the finest level rather than nowhere', () => {
    const { levels } = groupLevels(
      [mesh('h/_x_house_primitive0'), mesh('h/_x_house_LOD1'), mesh('h/_x_house_LOD2'), mesh('h/_x_house_LOD5')],
      lods,
    );
    expect(levels.map((l) => l.length)).toEqual([2, 1, 1]);
  });

  it('falls back to drawing everything when a declared level has no mesh, rather than to an invisible building', () => {
    // A house that declares three levels and ships two: collapsing to "no levels" draws all of them, which is
    // visibly wrong. Enabling only the level that exists would have hidden the building outright.
    const { levels, errors } = groupLevels([mesh('h/_x_house_primitive0'), mesh('h/_x_house_LOD1')], lods);
    expect(levels).toEqual([]);
    expect(errors).toEqual([]);
  });
});
