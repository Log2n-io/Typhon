import { NullEngine } from '@babylonjs/core/Engines/nullEngine';
import { Scene } from '@babylonjs/core/scene';
import { WorldStore } from '@typhondb/client';
import { afterEach, describe, expect, it } from 'vitest';

import { Archetype, SWG_SCHEMA } from '../src/data/swg-schema';
import { BOLT_LENGTH_M, BOLT_SPEED_MPS, boltSpan, CombatFx, type CombatFxHooks } from '../src/render/combat-fx';
import type { FrameView } from '../src/render/layer-packer';
import { FLAT_GROUND } from '../src/terrain/ground-sampler';

/** A bolt grows out of the muzzle, flies at full length and shrinks into what it hits (Loïc, 2026-10-24). */
describe("a bolt's flight", () => {
  const far = 30;

  it('grows from the muzzle', () => {
    const t = 0.5 * (BOLT_LENGTH_M / BOLT_SPEED_MPS);
    const span = boltSpan(t, far);
    expect(span.tail).toBe(0);
    expect(span.head).toBeCloseTo(BOLT_LENGTH_M / 2, 6);
  });

  it('flies at full length', () => {
    const span = boltSpan(10 / BOLT_SPEED_MPS, far);
    expect(span.head - span.tail).toBeCloseTo(BOLT_LENGTH_M, 6);
    expect(span.arrived).toBe(false);
  });

  it('arrives, then shrinks into the target and is gone', () => {
    const arrival = boltSpan(far / BOLT_SPEED_MPS, far);
    expect(arrival.arrived).toBe(true);
    expect(arrival.head).toBe(far);
    expect(arrival.done).toBe(false);

    const shrinking = boltSpan((far + BOLT_LENGTH_M / 2) / BOLT_SPEED_MPS, far);
    expect(shrinking.head).toBe(far);
    expect(shrinking.head - shrinking.tail).toBeCloseTo(BOLT_LENGTH_M / 2, 6);
    expect(boltSpan((far + BOLT_LENGTH_M) / BOLT_SPEED_MPS, far).done).toBe(true);
  });

  /** Closer than its own length: it never reaches full length, and still hits and goes. */
  it('hits a target nearer than its length', () => {
    expect(boltSpan(1 / BOLT_SPEED_MPS, 1).arrived).toBe(true);
    expect(boltSpan(1 / BOLT_SPEED_MPS, 1).head - boltSpan(1 / BOLT_SPEED_MPS, 1).tail).toBeLessThanOrEqual(1);
  });
});

const view: FrameView = {
  renderTick: 5,
  renderFrac: 0,
  originX: 0,
  originZ: 0,
  eyeX: 0,
  eyeY: 100,
  eyeZ: -100,
  planes: new Float64Array(24),
  pixelsPerMetre: 400,
  viewportWidth: 800,
  viewportHeight: 800,
  ground: FLAT_GROUND,
  predicted: null,
  hidden: new Set<number>(),
  drawnElsewhere: new Set<number>(),
  crowd: null,
  crowdDraws: false,
  crowdCount: 0,
  perSecond: 1,
};

describe('CombatFx (NullEngine)', () => {
  const engine = new NullEngine();
  const scene = new Scene(engine);
  afterEach(() => {
    scene.meshes.slice().forEach((m) => {
      m.dispose();
    });
  });

  /** Two bodies 20 m apart; netId 1 shoots when `shooter` says so. */
  function setup(shooter: boolean) {
    const world = new WorldStore(SWG_SCHEMA, { maxNetId: 64 });
    world.beginFrame(1);
    const store = world.archetypeStore(Archetype.Creature);
    for (const netId of [1, 2]) {
      store.resetMotion(world.enter(Archetype.Creature, netId), [netId * 20, 0], [0, 0], 1, 0);
    }

    const log: string[] = [];
    const hooks: CombatFxHooks = {
      strike: (n) => log.push(`strike ${String(n)}`),
      impact: (n, amount) => log.push(`impact ${String(n)} ${String(amount)}`),
      ranged: () => shooter,
      aim: () => 1,
    };
    const fx = new CombatFx(scene);
    fx.hooks = hooks;
    fx.bind(world);
    return { fx, log };
  }

  it('plays a blow when render time reaches its tick, not when it arrives', () => {
    const { fx, log } = setup(false);
    fx.push(6, 1, 2, 50);
    fx.update(view, 0);
    expect(log).toEqual([]);
    fx.update({ ...view, renderTick: 6 }, 0.1);
    expect(log).toEqual(['strike 1', 'impact 2 50']);
  });

  /** A strike in reach lands at once; a shot lands when its bolt does, about 20 m / 45 m/s later. */
  it('lands a shot when its bolt arrives, and draws the bolt meanwhile', () => {
    const { fx, log } = setup(true);
    fx.push(5, 1, 2, 30);
    fx.update(view, 0);
    expect(log).toEqual(['strike 1']);
    fx.update(view, 0.2);
    expect(fx.bolts).toBe(1);
    expect(log).toEqual(['strike 1']);
    fx.update(view, 0.6);
    expect(log).toEqual(['strike 1', 'impact 2 30']);
    fx.update(view, 2);
    expect(fx.bolts).toBe(0);
  });

  it('lands a blow whose shooter is not held at once, with no bolt', () => {
    const { fx, log } = setup(true);
    fx.push(5, 9, 2, 12);
    fx.update(view, 0);
    expect(log).toEqual(['strike 9', 'impact 2 12']);
    expect(fx.bolts).toBe(0);
  });
});
