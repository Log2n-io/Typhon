import type { GridSchema } from '../aggregates/aggregate-grid.js';
import { CatalogPlan, type Catalog, type CatalogGrid, type FieldPlan } from '../protocol/catalog.js';
import { CodecKind } from '../protocol/codec-kinds.js';
import type { ArchetypeSchema, FieldKind, FieldSchema, WorldSchema } from '../store/schema.js';

/**
 * The storage a decoded field needs, from its codec, or `null` for a codec newer than this library (skipped by
 * `fixedBytes`, so there is nothing to store).
 *
 * - Integers keep their own typed array; a `bits` field the narrowest unsigned one that holds it; `varu`, `entityRef`
 *   and `tickLo` a `u32`, `vari` an `i32`.
 * - `f32` and `f16` a `f32`: every half and every single is exact in a float32.
 * - Every dequantized value (`quant`, `pos`, `vec`, `unorm`, `snorm`, `angle`, `quat3`) a `f64`: decoded values are
 *   bit-exact binary64 (W1), and narrowing them is a renderer's choice, not the store's.
 */
export function fieldKindOf(field: FieldPlan): FieldKind | null {
  switch (field.kind) {
    case CodecKind.Bool:
    case CodecKind.U8:
      return 'u8';
    case CodecKind.Bits:
      return field.bitCount <= 8 ? 'u8' : field.bitCount <= 16 ? 'u16' : 'u32';
    case CodecKind.I8:
      return 'i8';
    case CodecKind.U16:
      return 'u16';
    case CodecKind.I16:
      return 'i16';
    case CodecKind.U32:
    case CodecKind.Varu:
    case CodecKind.EntityRef:
    case CodecKind.TickLo:
      return 'u32';
    case CodecKind.I32:
    case CodecKind.Vari:
      return 'i32';
    case CodecKind.F32:
    case CodecKind.F16:
      return 'f32';
    case CodecKind.Str:
      return 'text';
    case CodecKind.Bytes:
    case CodecKind.Blob:
      return 'bytes';
    case CodecKind.Unknown:
      return null;
    default:
      return 'f64';
  }
}

/**
 * The store schema a catalog describes: one archetype per catalog archetype, in index order, with its position and its
 * public fields in wire order. Groups are the catalog's change groups (W14); `onEnter` fields carry no group (W15).
 * Owner fields are not stored per slot: they belong to the one controlled entity (see `SelfState`).
 */
export function worldSchemaFromCatalog(catalog: Catalog | CatalogPlan): WorldSchema {
  const plan = catalog instanceof CatalogPlan ? catalog : CatalogPlan.compile(catalog);
  return {
    tickPeriodUs: plan.catalog.tick.periodUs,
    archetypes: plan.archetypes.map((a): ArchetypeSchema => {
      const fields: FieldSchema[] = [];
      for (const f of a.fields) {
        const kind = fieldKindOf(f);
        if (kind === null) {
          continue;
        }

        const group = f.field?.onEnter === true ? -1 : a.groups.indexOf(f.field?.group ?? '');
        fields.push({
          name: f.name,
          kind,
          ...(kind !== 'text' && kind !== 'bytes' && f.components !== 1 ? { components: f.components } : {}),
          ...(group >= 0 ? { group } : {}),
        });
      }

      const position = a.position;
      return {
        index: a.idx,
        name: a.name,
        groups: a.groups,
        fields,
        ...(position === null
          ? {}
          : {
              position: {
                kind: position.moving ? 'motion' : 'static',
                ...(position.moving ? { model: position.linear ? 'linear' : 'none' } : {}),
                dims: position.dims === 3 ? 3 : 2,
              },
            }),
      };
    }),
  };
}

/** The geometry of a catalog grid, as `AggregateGrid` takes it. */
export function gridSchemaFromCatalog(grid: CatalogGrid): GridSchema {
  return { index: grid.idx, origin: grid.origin, cell: grid.cell, dims: grid.dims, archetypes: grid.archetypes };
}
