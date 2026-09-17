import { NullEngine } from '@babylonjs/core/Engines/nullEngine';
import { Scene } from '@babylonjs/core/scene';
import { WorldStore } from '@typhondb/client';
import { afterEach, describe, expect, it } from 'vitest';
import { Archetype, SWG_SCHEMA } from '../src/data/swg-schema';
import { AttackLines } from '../src/render/attack-lines';
import type { FrameView } from '../src/render/layer-packer';

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
  selectedNetId: 0,
};

describe('AttackLines (NullEngine)', () => {
  const engine = new NullEngine();
  const scene = new Scene(engine);
  afterEach(() => {
    scene.meshes.slice().forEach((m) => {
      m.dispose();
    });
  });

  const setup = () => {
    const world = new WorldStore(SWG_SCHEMA, { maxNetId: 64 });
    world.beginFrame(1);
    const store = world.archetypeStore(Archetype.Creature);
    for (const netId of [1, 2]) {
      store.resetMotion(world.enter(Archetype.Creature, netId), netId * 10, 0, 0, 0, 1, 0);
    }

    const lines = new AttackLines(scene);
    lines.bind(world);
    return lines;
  };

  it('draws a line when both ends are held', () => {
    const lines = setup();
    lines.onAttack(4, 1, 2);
    lines.update(view);
    expect([lines.drawn, lines.instances]).toEqual([1, 1]);
  });

  it('draws a hit marker, two crossed instances, when only one end is held', () => {
    const lines = setup();
    lines.onAttack(4, 1, 0); // the target is outside the view: it travels as 0
    lines.onAttack(4, 9, 2); // the attacker is known to the server but not held here
    lines.update(view);
    expect([lines.drawn, lines.instances]).toEqual([2, 4]);
  });

  it('draws nothing when neither end is held, before the event is due, or after it has faded', () => {
    const lines = setup();
    lines.onAttack(4, 8, 9);
    lines.onAttack(6, 1, 2); // render time has not reached tick 6
    lines.update(view);
    expect(lines.instances).toBe(0);
    lines.update({ ...view, renderTick: 20 });
    expect(lines.instances).toBe(0);
  });
});
