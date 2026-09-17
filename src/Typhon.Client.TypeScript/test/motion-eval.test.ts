import { describe, expect, it } from 'vitest';
import {
  archetypeOf,
  CatalogPlan,
  epochAt,
  evaluateLive,
  evaluateSlot,
  forEachMessage,
  FrameApplier,
  MAX_MOTION_STRIDE,
  NOT_FOUND,
  parseCatalog,
  slotOf,
  type ArchetypeStore,
} from '../src/index.js';
import { bits, fromBits, goldenBin, goldenJson } from './golden-support.js';

/*
 * motion-eval and motion-eval-wrap: the cross-language pin of § 6's evaluation. The .NET SDK generated them by replaying
 * a committed stream and evaluating each case; this side replays the same bytes and must produce the same IEEE bits —
 * per slot and in batch.
 *
 * - `motion-eval` runs over `stream-kitchen-sink`: a static archetype, a render time exactly at a segment start, two
 *   `none` samples interpolated, and time before the oldest segment and past the newest.
 * - `motion-eval-wrap` runs over `stream-motion`, which turns the depth-8 ring twice and changes epoch on both an
 *   interpolating (`none`) and an extrapolating (`linear`) entity: the ring walk and the epoch gate, which no other
 *   committed stream reaches.
 *
 * Bits, not numbers: `-0 === 0`, and a sign that survives one implementation and not the other is exactly the kind of
 * divergence a golden vector exists to catch.
 */

interface MotionEntity {
  readonly archetype: string;
  readonly netId: number;
  readonly epoch: number;
  readonly position: string[];
  readonly velocity: string[];
}

interface MotionCase {
  readonly name: string;
  /** The stream message after which the store is evaluated, 0-based. */
  readonly afterFrame: number;
  readonly renderTick: number;
  /** A double in [0, 1), as IEEE bits. */
  readonly renderFrac: string;
  /** The archetypes this case covers: its entity list is exactly their live entities. */
  readonly archetypes: string[];
  readonly entities: MotionEntity[];
}

interface MotionVector {
  readonly catalog: string;
  readonly stream: string;
  /** The ring depth the vector was generated with: a store that sizes its ring differently evaluates differently. */
  readonly segmentHistory: number;
  readonly cases: MotionCase[];
}

/** The vectors, and the cases each is expected to carry: a vector that loses a case must fail, not quietly shrink. */
const VECTORS: Record<string, string[]> = {
  'motion-eval': [
    'a-static-archetype',
    'exactly-at-a-segment-start',
    'between-two-none-samples',
    'before-the-oldest-segment-held',
    'past-the-newest-segment',
  ],
  'motion-eval-wrap': [
    'before-the-wrap',
    'inside-the-wrap',
    'across-the-ring-index-wrap',
    'after-the-wrap',
    'older-than-the-oldest-held',
    'just-before-the-epoch-change',
    'between-the-two-epochs-samples',
    'just-after-the-epoch-change',
  ],
};

function motionBits(
  store: ArchetypeStore,
  out: Float64Array,
  offset: number,
): { position: string[]; velocity: string[] } {
  const dims = store.dims;
  const position: string[] = [];
  const velocity: string[] = [];
  for (let a = 0; a < dims; a++) {
    position.push(bits(out[offset + a]!));
    velocity.push(bits(out[offset + dims + a]!));
  }

  return { position, velocity };
}

describe.each(Object.keys(VECTORS))('golden %s', (name) => {
  const vector = goldenJson(name) as MotionVector;
  const plan = CatalogPlan.compile(parseCatalog(goldenBin(vector.catalog)));
  const frames: Uint8Array[] = [];
  forEachMessage(goldenBin(vector.stream), (message) => frames.push(message.slice()));
  const indexOfArchetype = (archetype: string) => plan.archetypeByName(archetype)!.idx;

  /** A store replayed to just after `afterFrame`. */
  function replayTo(afterFrame: number): FrameApplier {
    const applier = new FrameApplier(plan);
    for (let i = 0; i <= afterFrame; i++) {
      applier.apply(frames[i]!, 1000 + i * 50);
    }

    return applier;
  }

  it('was generated for the ring this store builds', () => {
    // The kitchen sink's 50 ms period under the default 300 ms render-delay cap: 8 segments per slot.
    const applier = new FrameApplier(plan);
    for (const a of plan.archetypes) {
      expect(applier.world.archetypeStore(a.idx).segmentHistory, a.name).toBe(vector.segmentHistory);
    }
  });

  it('carries the cases it is meant to', () => {
    expect(vector.cases.map((c) => c.name)).toEqual(VECTORS[name]);
    expect(frames.length).toBeGreaterThan(Math.max(...vector.cases.map((c) => c.afterFrame)));
  });

  for (const testCase of vector.cases) {
    it(testCase.name, () => {
      const applier = replayTo(testCase.afterFrame);
      const world = applier.world;
      const frac = fromBits(testCase.renderFrac);
      const out = new Float64Array(MAX_MOTION_STRIDE);

      // Per slot: every entity the case names, found by netId — slots are this implementation's business.
      const perSlot: MotionEntity[] = [];
      for (const entity of testCase.entities) {
        const archetype = plan.archetypeByName(entity.archetype)!;
        const location = world.locate(entity.netId);
        expect(location, `${entity.archetype} ${entity.netId} is held`).not.toBe(NOT_FOUND);
        expect(archetypeOf(location)).toBe(archetype.idx);
        const store = world.archetypeStore(archetype.idx);
        const slot = slotOf(location);
        evaluateSlot(store, slot, testCase.renderTick, frac, out, 0);
        perSlot.push({
          archetype: entity.archetype,
          netId: entity.netId,
          epoch: epochAt(store, slot, testCase.renderTick),
          ...motionBits(store, out, 0),
        });
      }

      expect(perSlot).toEqual(testCase.entities);

      // In batch, and over exactly the live entities of the archetypes the case names: a missed enter or a stray leave
      // shows up as a different list, not as a value that happens to match.
      const perLive: MotionEntity[] = [];
      for (const archetypeName of testCase.archetypes) {
        const archetype = plan.archetypeByName(archetypeName)!;
        const store = world.archetypeStore(archetype.idx);
        const stride = store.motionStride;
        const all = new Float64Array(store.liveCount * stride);
        evaluateLive(store, testCase.renderTick, frac, all);
        for (let i = 0; i < store.liveCount; i++) {
          const slot = store.live[i]!;
          perLive.push({
            archetype: archetypeName,
            netId: store.netIds[slot]!,
            epoch: epochAt(store, slot, testCase.renderTick),
            ...motionBits(store, all, i * stride),
          });
        }
      }

      perLive.sort((x, y) =>
        x.archetype === y.archetype ? x.netId - y.netId : indexOfArchetype(x.archetype) - indexOfArchetype(y.archetype),
      );
      expect(perLive).toEqual(testCase.entities);
    });
  }
});
