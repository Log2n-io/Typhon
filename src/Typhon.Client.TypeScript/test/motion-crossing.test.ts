import { describe, expect, it } from 'vitest';
import { evaluateLive, evaluateSlot, MAX_MOTION_STRIDE, WorldStore, type WorldSchema } from '../src/index.js';

/*
 * The `linear` crossing rule of § 6, over a gap the committed vectors cannot reach.
 *
 * `stream-motion` gives every entity a segment on every frame, so its pairs are one tick apart and the crossing is the whole
 * segment. The half that needs a gap — the velocity carrying every tick but the last — is pinned here and, with the SAME
 * numbers, by `MotionEvaluatorTests.AGapIsExtrapolatedExceptForItsLastTickWhichCrossesToTheNextOrigin` on the .NET side: the
 * two SDKs are ports of one another, and a hand-written pair of identical expectations is what binds them where no vector does.
 *
 * Segments are pushed into the store directly rather than decoded from a frame, because the position codec would quantise the
 * figures and the point is exact arithmetic.
 */

const PERIOD_MS = 100;

/** One 2D `linear` archetype, the smallest thing that holds a segment ring. */
const SCHEMA: WorldSchema = {
  tickPeriodUs: PERIOD_MS * 1000,
  archetypes: [{ index: 0, name: 'M', position: { kind: 'motion', dims: 2 }, groups: [], fields: [] }],
};

/** A mover at (100, 50) from tick 1000, going 10 m per tick along +X — the .NET fixture's `MakeMover`. */
function mover(): { store: WorldStore['archetypes'][number]; slot: number; world: WorldStore } {
  const world = new WorldStore(SCHEMA);
  const store = world.archetypes[0]!;
  world.beginFrame(1000);
  const slot = world.enter(0, 1);
  store.resetMotion(slot, [100, 50], [10, 0], 1000, 0);
  return { store, slot, world };
}

describe('a linear gap', () => {
  /** Position and velocity along +X, asserting the batch path agrees with the per-slot one to the bit. */
  const at = (fixture: ReturnType<typeof mover>, tick: number, frac: number): [number, number] => {
    const { store, slot } = fixture;
    const out = new Float64Array(MAX_MOTION_STRIDE);
    const batch = new Float64Array(store.liveCount * store.motionStride);
    evaluateSlot(store, slot, tick, frac, out, 0);
    evaluateLive(store, tick, frac, batch);
    const offset = store.live.indexOf(slot) * store.motionStride;
    expect(Array.from(batch.subarray(offset, offset + store.motionStride))).toEqual(
      Array.from(out.subarray(0, store.motionStride)),
    );
    return [out[0]!, out[2]!];
  };

  /**
   * The emit rule is what splits this: the server sends nothing while `p0 + v·(τ − t0)` holds within the declared tolerance,
   * so eight ticks of silence are eight ticks of velocity to trust — and the tick the next segment was emitted FOR is the one
   * whose error triggered it, so it is exactly the tick not to trust.
   *
   * Measured live at 10 Hz before this rule: an eight-tick gap ended with the entity's final step 0.135 m instead of the 0.5 m
   * its velocity implied, and the client drew the 0.5 and then dropped 336 mm in one frame at the join.
   */
  it('is carried by the velocity except for its last tick, which crosses to the next origin', () => {
    const fixture = mover();
    // Eight ticks on the entity is at 178 — 2 m short of the 180 the velocity implies.
    fixture.store.pushSegment(fixture.slot, [178, 50], [8, 0], 1008, 0);

    expect(at(fixture, 1006, 0.5)[0]).toBeCloseTo(165, 9);
    expect(at(fixture, 1007, 0)[0]).toBeCloseTo(170, 9);
    expect(at(fixture, 1007, 0.5)).toEqual([expect.closeTo(174, 9), expect.closeTo(8, 9)]);
    expect(at(fixture, 1008, 0)[0]).toBeCloseTo(178, 9);
  });

  it('never steps backwards through the join, which is the whole visible claim', () => {
    const fixture = mover();
    fixture.store.pushSegment(fixture.slot, [178, 50], [8, 0], 1008, 0);

    let previous = -Infinity;
    let worst = 0;
    for (let k = 0; k <= 100; k++) {
      const t = 1000 + k * 0.09;
      const tick = Math.floor(t);
      const here = at(fixture, tick, t - tick)[0];
      worst = Math.min(worst, here - previous);
      previous = here;
    }

    expect(worst).toBeGreaterThanOrEqual(0);
  });

  it('is pure extrapolation past the newest segment, which is what the velocity is for', () => {
    const fixture = mover();
    expect(at(fixture, 1012, 0.5)[0]).toBeCloseTo(100 + 10 * 12.5, 9);
    expect(at(fixture, 1012, 0.5)[1]).toBeCloseTo(10, 9);
  });

  /** A teleport is not crossed into: the epoch gate holds for `linear` as it does for `none`. */
  it('is not crossed when the next origin is another epoch', () => {
    const fixture = mover();
    fixture.store.pushSegment(fixture.slot, [-500, -500], [0, 0], 1008, 1);

    // Still the old velocity through the final tick, because the far side is a different world.
    expect(at(fixture, 1007, 0.5)[0]).toBeCloseTo(175, 9);
    expect(at(fixture, 1008, 0)[0]).toBeCloseTo(-500, 9);
  });
});
