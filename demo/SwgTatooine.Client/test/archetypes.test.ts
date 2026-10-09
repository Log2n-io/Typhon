import { FLAT_GROUND } from '../src/terrain/ground-sampler';
import { WorldStore, type WorldSchema } from '@typhondb/client';
import { describe, expect, it } from 'vitest';
import { DEFAULT_ARCHETYPE_INFOS, KNOWN_ARCHETYPES, resolveArchetypes } from '../src/data/archetypes';
import { formatField } from '../src/data/swg-format';
import { StructureKind } from '../src/data/swg-schema';
import { LayerPacker } from '../src/render/layer-packer';
import { styleFor } from '../src/render/styles';
import { extractFrustumPlanes, packedMode, packedStyle } from '../src/render/view-math';

/**
 * Archetype identity is a NAME, never an index (`data/archetypes.ts`). The regression this guards is the one C4 found:
 * the client's own constant ordered the archetypes `WorldObject, CreatureLair, Creature, CityNpc, Player` and the
 * server's catalog orders them alphabetically, so every entity would have been drawn in another archetype's shape,
 * reading another archetype's fields, with nothing failing.
 *
 * The schema below is the SERVER's order, taken from a live catalog (hash 61BE9335369D0B9F).
 */

const MOVING = { kind: 'motion', model: 'linear', dims: 2 } as const;
const STATIC = { kind: 'static', dims: 2 } as const;

const LIVE_ORDER: WorldSchema = {
  tickPeriodUs: 100_000,
  archetypes: [
    {
      index: 0,
      name: 'CityNpc',
      groups: ['state'],
      position: MOVING,
      fields: [{ name: 'mode', kind: 'u8', group: 0 }],
    },
    {
      index: 1,
      name: 'Creature',
      groups: ['state', 'vitals'],
      position: MOVING,
      fields: [
        { name: 'aggro', kind: 'f32' },
        { name: 'mode', kind: 'u8', group: 0 },
        { name: 'hp', kind: 'f64', group: 1 },
      ],
    },
    { index: 2, name: 'CreatureLair', groups: [], position: STATIC, fields: [{ name: 'template', kind: 'u16' }] },
    {
      index: 3,
      name: 'Player',
      groups: ['state', 'vitals'],
      position: MOVING,
      fields: [
        { name: 'activity', kind: 'u8', group: 0 },
        { name: 'hp', kind: 'f64', group: 1 },
      ],
    },
    {
      index: 4,
      name: 'WorldObject',
      groups: [],
      position: STATIC,
      fields: [
        { name: 'kind', kind: 'u8' },
        { name: 'region', kind: 'i16' },
      ],
    },
  ],
};

describe('archetype identity', () => {
  it('resolves every known archetype from a schema that numbers them in the server order', () => {
    const view = resolveArchetypes(LIVE_ORDER);
    expect(view.count).toBe(5);
    for (const name of KNOWN_ARCHETYPES) {
      const index = view.indexOf(name);
      expect(index, name).toBeGreaterThanOrEqual(0);
      expect(view.name(index)).toBe(name);
    }

    // The very permutation that made this necessary.
    expect(view.indexOf('WorldObject')).toBe(4);
    expect(view.indexOf('CityNpc')).toBe(0);
    expect(view.label(4)).toBe('Structure');
    expect(view.label(0)).toBe('City NPC');
  });

  it('gives an archetype the schema does not declare no index, and an unknown one its own name', () => {
    const view = resolveArchetypes({
      tickPeriodUs: 100_000,
      archetypes: [{ index: 0, name: 'Starship', groups: [], position: STATIC, fields: [] }],
    });
    expect(view.indexOf('Player')).toBe(-1);
    expect(view.label(0)).toBe('Starship');
    // Drawn, in the fallback style, rather than skipped: nothing drawn looks like nothing sent.
    expect(styleFor('Starship').shape).toBe('box');
  });

  it('styles each archetype by name, so the shapes follow the catalog and not our own order', () => {
    const view = resolveArchetypes(LIVE_ORDER);
    const shapeAt = (index: number): string => styleFor(view.name(index)).shape;
    expect(shapeAt(view.indexOf('WorldObject'))).toBe('box');
    expect(shapeAt(view.indexOf('CreatureLair'))).toBe('mound');
    expect(shapeAt(view.indexOf('Creature'))).toBe('arrow');
    expect(shapeAt(view.indexOf('Player'))).toBe('prism');
  });

  it("picks the style field from the store's own name, at whatever index the layer sits", () => {
    // WorldObject is index 4 here and 0 in the client's old constant. A packer that keyed on its index would take
    // CityNpc's readers, which have no style field, and every structure on the planet would be style 0 — a building.
    const view = resolveArchetypes(LIVE_ORDER);
    const index = view.indexOf('WorldObject');
    const world = new WorldStore(LIVE_ORDER, { maxNetId: 256 });
    const store = world.archetypeStore(index);
    const packer = new LayerPacker(index, styleFor('WorldObject'));
    packer.bind(store);
    world.beginFrame(1);
    const slot = world.enter(index, 7);
    store.resetMotion(slot, [0, 100], null, 1, 0);
    store.field('kind')[slot] = StructureKind.Shuttleport;

    const planes = new Float64Array(24);
    const m = new Float32Array(16);
    m[0] = 1;
    m[5] = 1;
    m[10] = 1001 / 999;
    m[11] = 1;
    m[14] = -2000 / 999;
    extractFrustumPlanes(m, planes);
    packer.pack({
      renderTick: 1,
      renderFrac: 0,
      originX: 0,
      originZ: 0,
      eyeX: 0,
      eyeY: 0,
      eyeZ: 0,
      planes,
      pixelsPerMetre: 400,
      viewportWidth: 800,
      viewportHeight: 800,
      selectedNetId: 0,
      ground: FLAT_GROUND,
    });

    expect(packer.nearCount + packer.farCount).toBe(1);
    const packed = packer.nearCount === 1 ? packer.nearData[3] : packer.farData[3];
    expect(packedStyle(packed)).toBe(StructureKind.Shuttleport);
  });

  it('packs an archetype whose catalog omits the field the style is read from', () => {
    // The live catalog gives a Creature `aggro`, `mode` and `hp` and NO `template`, which is what the client's mock had
    // and what the packer asked for. `ArchetypeStore.field` throws on a name the schema does not declare, so the first
    // live frame died in the render loop with "Archetype 'Creature' has no field 'template'".
    const view = resolveArchetypes(LIVE_ORDER);
    const index = view.indexOf('Creature');
    const world = new WorldStore(LIVE_ORDER, { maxNetId: 256 });
    const store = world.archetypeStore(index);
    expect(store.fieldIndex('template')).toBe(-1);

    const packer = new LayerPacker(index, styleFor('Creature'));
    packer.bind(store);
    world.beginFrame(1);
    const slot = world.enter(index, 11);
    store.resetMotion(slot, [0, 100], [0, 0], 1, 0);
    store.field('mode')[slot] = 3;

    const planes = new Float64Array(24);
    const m = new Float32Array(16);
    m[0] = 1;
    m[5] = 1;
    m[10] = 1001 / 999;
    m[11] = 1;
    m[14] = -2000 / 999;
    extractFrustumPlanes(m, planes);
    expect(() => {
      packer.pack({
        renderTick: 1,
        renderFrac: 0,
        originX: 0,
        originZ: 0,
        eyeX: 0,
        eyeY: 0,
        eyeZ: 0,
        planes,
        pixelsPerMetre: 400,
        viewportWidth: 800,
        viewportHeight: 800,
        selectedNetId: 0,
        ground: FLAT_GROUND,
      });
    }).not.toThrow();
    expect(packer.nearCount + packer.farCount).toBe(1);

    // Style 0 for want of a template, but the mode it DOES have still reaches the packing.
    const packed = packer.nearCount === 1 ? packer.nearData[3] : packer.farData[3];
    expect(packedStyle(packed)).toBe(0);
    expect(packedMode(packed)).toBe(3);
  });

  it('formats a field by the name the catalog gives it, and by the archetype name where that matters', () => {
    expect(formatField('CreatureLair', 'missionId', 3)).toBe('mission, difficulty 3');
    expect(formatField('Creature', 'missionId', 3)).toBe('dormant');
    expect(formatField('WorldObject', 'region', -1)).toBe('wilderness');
    expect(formatField('Creature', 'aggro', 32.5)).toBe('33 m');
  });

  it('offers the known archetypes before any source has a schema', () => {
    expect(DEFAULT_ARCHETYPE_INFOS.map((i) => i.name)).toEqual([...KNOWN_ARCHETYPES]);
  });
});
