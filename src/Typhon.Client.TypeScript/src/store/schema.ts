/**
 * The shape of the client-side world, as a store needs it.
 *
 * This is what the catalog (sent in `WELCOME`, see `claude/design/Subscriptions/V2/03-wire-protocol.md` § 4) resolves to on
 * the client: which archetypes exist, how each is positioned, and which decoded fields it carries. It holds no codec
 * information — decoding is the protocol layer's job — only the storage kind of each dequantized value.
 */

/** Storage kind of one decoded field: selects the typed array that holds it. */
export type FieldKind = 'u8' | 'i8' | 'u16' | 'i16' | 'u32' | 'i32' | 'f32' | 'f64';

/** How an archetype is placed in the world. */
export type PositionKind =
  /** Moves: motion segments `(p0, v, t0, epoch)` are replicated. */
  | 'motion'
  /** Never moves: a position is sent once, on enter. */
  | 'static'
  /** Not spatial (a source-only archetype): no position at all. */
  | 'none';

export interface FieldSchema {
  readonly name: string;
  readonly kind: FieldKind;
  /** Index into {@link ArchetypeSchema.groups}. */
  readonly group: number;
}

export interface ArchetypeSchema {
  /** The archetype index used on the wire. Must equal the position in {@link WorldSchema.archetypes}. */
  readonly index: number;
  readonly name: string;
  readonly position: PositionKind;
  /** Change groups, at most 32. A motion archetype must name a `motion` group; segments mark it. */
  readonly groups: readonly string[];
  readonly fields: readonly FieldSchema[];
}

export interface WorldSchema {
  readonly archetypes: readonly ArchetypeSchema[];
}

/** Name of the change group a motion segment marks. */
export const MOTION_GROUP = 'motion';

export type FieldArray =
  Uint8Array | Int8Array | Uint16Array | Int16Array | Uint32Array | Int32Array | Float32Array | Float64Array;

export function allocateField(kind: FieldKind, length: number): FieldArray {
  switch (kind) {
    case 'u8':
      return new Uint8Array(length);
    case 'i8':
      return new Int8Array(length);
    case 'u16':
      return new Uint16Array(length);
    case 'i16':
      return new Int16Array(length);
    case 'u32':
      return new Uint32Array(length);
    case 'i32':
      return new Int32Array(length);
    case 'f32':
      return new Float32Array(length);
    case 'f64':
      return new Float64Array(length);
  }
}

/** Throws when a schema is internally inconsistent; a store built on it would silently misfile data otherwise. */
export function validateSchema(schema: WorldSchema): void {
  if (schema.archetypes.length > 255) {
    throw new Error(`A world holds at most 255 archetypes, got ${schema.archetypes.length}`);
  }

  schema.archetypes.forEach((archetype, i) => {
    if (archetype.index !== i) {
      throw new Error(`Archetype '${archetype.name}' has index ${archetype.index} at position ${i}`);
    }

    if (archetype.groups.length > 32) {
      throw new Error(
        `Archetype '${archetype.name}' declares ${archetype.groups.length} groups; the change mask holds 32`,
      );
    }

    if (archetype.position === 'motion' && !archetype.groups.includes(MOTION_GROUP)) {
      throw new Error(`Motion archetype '${archetype.name}' declares no '${MOTION_GROUP}' group`);
    }

    const names = new Set<string>();
    for (const field of archetype.fields) {
      if (names.has(field.name)) {
        throw new Error(`Archetype '${archetype.name}' declares field '${field.name}' twice`);
      }

      names.add(field.name);
      if (field.group < 0 || field.group >= archetype.groups.length) {
        throw new Error(`Field '${archetype.name}.${field.name}' names group ${field.group}, which does not exist`);
      }
    }
  });
}
