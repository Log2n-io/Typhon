import { describe, expect, it } from 'vitest';
import { archetypeOf, CatalogPlan, FrameApplier, isNumericKind, parseCatalog, slotOf } from '../src/index.js';
import type { Catalog } from '../src/index.js';
import { goldenBin } from './golden-support.js';

/*
 * Collections in the TypeScript store (W34), from the committed tick-coll vector: an entity's and the controlled
 * entity's lists overwrite whole, keep their total when truncated, and keep their columns between sends — the same
 * assertions as the .NET CollectionStoreTests.
 */
describe('collections', () => {
  it('the tick-coll vector lands in the store', () => {
    const plan = CatalogPlan.compile(parseCatalog(goldenBin('catalog-coll')));
    const applier = new FrameApplier(plan);
    applier.apply(goldenBin('tick-coll'));

    const one = applier.world.locate(1);
    const two = applier.world.locate(2);
    const store = applier.world.archetypeStore(archetypeOf(one));

    // netId 1 entered with no item and one tag, then a state sent one item: the list is that one.
    const first = store.collection('items', slotOf(one))!;
    expect([first.total, first.count, first.truncated]).toEqual([1, 1, false]);
    expect(first.number(0, 'id')).toBe(65535);
    expect(first.number(0, 'owner')).toBe(4_000_000_000);
    expect(first.text(0, 'name')).toBe('');
    expect(store.collection('tags', slotOf(one))!.number(0, 'tag')).toBe(9);

    // netId 2: four of nine sent, in the state's order — the enter's list is overwritten whole.
    const second = store.collection('items', slotOf(two))!;
    expect([second.total, second.count, second.truncated]).toEqual([9, 4, true]);
    expect([0, 1, 2, 3].map((i) => second.text(i, 'name'))).toEqual(['bouclier ø', 'sword', 'bouclier ø', '']);
    expect([
      second.number(0, 'stack'),
      second.number(1, 'stack'),
      second.number(0, 'lit'),
      second.number(1, 'lit'),
    ]).toEqual([31, 3, 0, 1]);
    expect(store.collection('tags', slotOf(two))!.count).toBe(3);

    // SELF: the owner collection of the controlled entity.
    const locker = plan.archetypes.find((a) => a.name === 'Locker')!;
    const keys = applier.selfState.collections[locker.ownerFields.findIndex((f) => f.name === 'keys')]!;
    expect([keys.total, keys.count]).toEqual([2, 2]);
    expect(keys.integer64(0, 'code')).toBe(0xffff_ffff_ffff_ffffn);
    expect(keys.number(0, 'where')).toBe(1.5);
    expect(applier.world.anomalies).toBe(0);
  });

  it('a plan refuses bytes in an element whether or not the catalog was validated', () => {
    const catalog = parseCatalog(goldenBin('catalog-coll'));
    for (const t of ['bytes', 'blob']) {
      const locker = catalog.archetypes.find((a) => a.name === 'Locker')!;
      const items = locker.fields.find((f) => f.name === 'items')!;
      const element = items.codec.element!.fields;
      const forged: Catalog = {
        ...catalog,
        archetypes: catalog.archetypes.map((a) =>
          a !== locker
            ? a
            : {
                ...a,
                fields: a.fields.map((f) =>
                  f !== items
                    ? f
                    : {
                        ...f,
                        codec: {
                          ...f.codec,
                          element: { fields: [{ name: 'raw', codec: { t, n: 4, maxBytes: 4 } }, ...element] },
                        },
                      },
                ),
              },
        ),
      };
      expect(() => CatalogPlan.compile(forged)).toThrow(/cannot be/);
    }
  });

  it('a collection is not a numeric column', () => {
    expect(isNumericKind('coll')).toBe(false);
  });
});
