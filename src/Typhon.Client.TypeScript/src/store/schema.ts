/**
 * The shape of the client-side world, as a store needs it.
 *
 * This is what the catalog (sent in `WELCOME`, see `claude/design/Subscriptions/03-wire-protocol.md` § 4) resolves to
 * on the client — `worldSchemaFromCatalog` builds it: which archetypes exist, how each is positioned, and which decoded
 * fields it carries. It holds no codec information — decoding is the protocol layer's job — only the storage of each
 * dequantized value.
 */

/** Storage of one decoded numeric value: selects the typed array that holds it. */
export type NumericFieldKind = 'u8' | 'i8' | 'u16' | 'i16' | 'u32' | 'i32' | 'f32' | 'f64';

/** Storage of one decoded field: a numeric typed array, or one string or byte array per slot. */
export type FieldKind = NumericFieldKind | 'text' | 'bytes';

const FIELD_KINDS: ReadonlySet<string> = new Set<FieldKind>([
  'u8',
  'i8',
  'u16',
  'i16',
  'u32',
  'i32',
  'f32',
  'f64',
  'text',
  'bytes',
]);

export interface FieldSchema {
  readonly name: string;
  readonly kind: FieldKind;
  /**
   * Numbers per value of a numeric field: 1 for a scalar, up to 4 for a quaternion. Default 1; ignored for text and
   * bytes.
   */
  readonly components?: number;
  /**
   * Index into {@link ArchetypeSchema.groups}. Absent for an `onEnter` field (W15): enter records carry it, state
   * records never do.
   */
  readonly group?: number;
}

/** How an archetype is placed in the world (W16). An archetype without one is not spatial. */
export interface PositionSchema {
  /** `motion`: segments `(p0, v, t0, epoch)` are replicated. `static`: a position is sent once, on enter. */
  readonly kind: 'motion' | 'static';
  /**
   * For `motion`: `linear` segments carry a velocity and are extrapolated; `none` segments are position samples,
   * interpolated between. Default `linear`.
   */
  readonly model?: 'linear' | 'none';
  /** 2 or 3. */
  readonly dims: 2 | 3;
}

export interface ArchetypeSchema {
  /** The archetype index used on the wire. Must equal the position in {@link WorldSchema.archetypes}. */
  readonly index: number;
  readonly name: string;
  readonly position?: PositionSchema;
  /** Change groups, at most {@link MAX_GROUPS}: bit i of an update mask is groups[i] (W14). */
  readonly groups: readonly string[];
  readonly fields: readonly FieldSchema[];
}

export interface WorldSchema {
  /** The server's nominal tick period (the catalog's `tick.periodUs`): it sizes the motion ring each slot keeps. */
  readonly tickPeriodUs: number;
  readonly archetypes: readonly ArchetypeSchema[];
}

/** Change groups per archetype: the width of a state record's `u8` mask. */
export const MAX_GROUPS = 8;

/**
 * The update-mask bit a motion segment sets. Motion is not a change group on the wire (W15), so it sits just above the
 * eight group bits: a renderer tests it to re-upload motion rows, and it can never collide with a group.
 */
export const MOTION_CHANGE_BIT = 1 << MAX_GROUPS;

export type FieldArray =
  Uint8Array | Int8Array | Uint16Array | Int16Array | Uint32Array | Int32Array | Float32Array | Float64Array;

export function allocateField(kind: NumericFieldKind, length: number): FieldArray {
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

export function isNumericKind(kind: FieldKind): kind is NumericFieldKind {
  return kind !== 'text' && kind !== 'bytes';
}

/** Throws when a schema is internally inconsistent; a store built on it would silently misfile data otherwise. */
export function validateSchema(schema: WorldSchema): void {
  if (!(Number.isInteger(schema.tickPeriodUs) && schema.tickPeriodUs > 0)) {
    throw new Error(`A world needs a positive integer tick period in microseconds, got ${schema.tickPeriodUs}`);
  }

  if (schema.archetypes.length > 255) {
    throw new Error(`A world holds at most 255 archetypes, got ${schema.archetypes.length}`);
  }

  schema.archetypes.forEach((archetype, i) => {
    if (archetype.index !== i) {
      throw new Error(`Archetype '${archetype.name}' has index ${archetype.index} at position ${i}`);
    }

    if (archetype.groups.length > MAX_GROUPS) {
      throw new Error(
        `Archetype '${archetype.name}' declares ${archetype.groups.length} groups; the update mask holds ${MAX_GROUPS}`,
      );
    }

    // Typed, checked anyway: a schema can be built from untyped data.
    const position = archetype.position;
    if (position !== undefined) {
      const kind: string = position.kind;
      const model: string | undefined = position.model;
      const dims: number = position.dims;
      if (kind !== 'motion' && kind !== 'static') {
        throw new Error(`Archetype '${archetype.name}': position kind '${kind}'; 'motion' or 'static' expected`);
      }

      if (model !== undefined && (kind !== 'motion' || (model !== 'linear' && model !== 'none'))) {
        throw new Error(`Archetype '${archetype.name}': position model '${model}'; 'linear' or 'none', on motion only`);
      }

      if (dims !== 2 && dims !== 3) {
        throw new Error(`Archetype '${archetype.name}' has a position of ${dims} dimensions; 2 or 3 expected`);
      }
    }

    const names = new Set<string>();
    for (const field of archetype.fields) {
      if (names.has(field.name)) {
        throw new Error(`Archetype '${archetype.name}' declares field '${field.name}' twice`);
      }

      names.add(field.name);
      // Typed, checked anyway: a schema can be built from untyped data.
      const kind: string = field.kind;
      if (!FIELD_KINDS.has(kind)) {
        throw new Error(`Field '${archetype.name}.${field.name}' has kind '${kind}', which the store does not know`);
      }

      if (
        field.group !== undefined &&
        !(Number.isInteger(field.group) && field.group >= 0 && field.group < archetype.groups.length)
      ) {
        throw new Error(`Field '${archetype.name}.${field.name}' names group ${field.group}, which does not exist`);
      }

      const components = field.components ?? 1;
      if (isNumericKind(field.kind) && !(Number.isInteger(components) && components >= 1 && components <= 4)) {
        throw new Error(`Field '${archetype.name}.${field.name}' has ${components} components; 1 to 4 expected`);
      }
    }
  });
}
