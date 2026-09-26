import { checkCanonical, validateCatalog } from './catalog-validator.js';
import { codecKindOf, CodecKind, isListElement, isPacked } from './codec-kinds.js';
import { BuiltInCommand, ProtocolConstants } from './constants.js';
import { malformed } from './errors.js';
import { quantStep, symmetricLimit, unsignedTop } from './math.js';
import { decodeUtf8 } from './utf8.js';

/*
 * The catalog: the canonical JSON a server sends in `WELCOME` (`03-wire-protocol.md` § 4), typed exactly as the C#
 * model (`Typhon.Protocol/Catalog/*.cs`) serializes it, and compiled into the plans every decoder and encoder works
 * from.
 *
 * The catalog arrives canonical: every array is already in wire order and every `idx` assigned. A client never sorts —
 * re-sorting would be a second source of truth for the wire order, and the only one that could disagree with the
 * server.
 */

export interface CatalogProtocolVersion {
  readonly major: number;
  readonly minor: number;
}

export interface CatalogApp {
  readonly name: string;
  readonly revision: number;
}

export interface CatalogTick {
  /** The nominal tick period in microseconds; `PERIOD` in a frame reports the actual one under time dilation. */
  readonly periodUs: number;
  readonly pingHz: number;
}

/** Server-wide ceilings a client acts on (W30). */
export interface CatalogLimits {
  readonly frameBytes: number;
  readonly clientMessageBytes: number;
  readonly resumeGraceMs: number;
}

/** A codec: its wire token `t` and the parameters that kind reads. Parameters a kind does not read are absent. */
export interface CatalogCodec {
  readonly t: string;
  readonly bits?: number;
  readonly min?: readonly number[];
  readonly max?: readonly number[];
  readonly scale?: number;
  /** A velocity's unit exponent: one code is 2^unitExp metres per tick (W5, `typhon.3`). */
  readonly unitExp?: number;
  readonly n?: number;
  readonly maxBytes?: number;
  readonly of?: CatalogCodec;
  readonly minCount?: number;
  readonly maxCount?: number;
  /** Declared only by a codec newer than the client may know, so the field can be skipped instead of refused. */
  readonly fixedBytes?: number;
}

export interface CatalogField {
  readonly name: string;
  readonly codec: CatalogCodec;
  /**
   * The change group; exactly one of this and {@link onEnter} on an archetype field, neither on an event or command
   * field.
   */
  readonly group?: string;
  readonly onEnter?: boolean;
  /** A key of {@link Catalog.enums}; allowed on `bits`, `u8`, `u16` and `varu` (W13). */
  readonly enum?: string;
  readonly smoothing?: string;
}

/** An archetype's position (W15, W16). */
export interface CatalogPosition {
  /** `motion` or `static`. */
  readonly kind: string;
  /** `linear` or `none`, for a moving archetype only. */
  readonly model?: string;
  /** `pos2` or `pos3`. */
  readonly pos: CatalogCodec;
  /** `vel2` or `vel3` matching {@link pos}, for the linear model. */
  readonly vel?: CatalogCodec;
}

/** The owner-only section (W17): values only the controlling session receives, in `SELF`. */
export interface CatalogOwner {
  readonly groups: readonly string[];
  readonly fields: readonly CatalogField[];
}

export interface CatalogArchetype {
  readonly idx: number;
  readonly name: string;
  /** Change groups in canonical order: bit i of a state record's mask is groups[i] (W14). */
  readonly groups: readonly string[];
  readonly position?: CatalogPosition;
  /** Wire order: the onEnter section, then each group's; packed fields first within a section (W11). */
  readonly fields: readonly CatalogField[];
  readonly owner?: CatalogOwner;
}

export interface CatalogEvent {
  readonly idx: number;
  readonly name: string;
  readonly scope: string;
  readonly fields: readonly CatalogField[];
}

export interface CatalogCommandRate {
  readonly perSec: number;
  readonly burst: number;
}

export interface CatalogCommand {
  readonly idx: number;
  readonly name: string;
  /** `queued` or `latest`. */
  readonly delivery: string;
  readonly rate?: CatalogCommandRate;
  readonly fields: readonly CatalogField[];
}

export interface CatalogGrid {
  readonly idx: number;
  /**
   * The tile, in the realm's replication cells (`typhon.3`, 12-realms § 5.4): origin and dimensions are the realm
   * frame's, so one grid is valid in every realm.
   */
  readonly tileCells: number;
  /** The archetype indices counted, in the order an `AGG` cell lists their counts. */
  readonly archetypes: readonly number[];
}

export interface CatalogMetric {
  readonly idx: number;
  readonly name: string;
  readonly unit: string;
  readonly codec: CatalogCodec;
  /** `session`, or absent for `server`. */
  readonly scope?: string;
  /** `counter`, or absent for `gauge`. */
  readonly kind?: string;
  /** One label per value of a vector metric, in wire order; absent for a scalar. */
  readonly labels?: readonly string[];
}

export interface Catalog {
  readonly protocol: CatalogProtocolVersion;
  readonly app: CatalogApp;
  readonly tick: CatalogTick;
  readonly limits: CatalogLimits;
  readonly sessionKinds: readonly string[];
  /** The realm kinds, in canonical order: a `REALM` block names its kind by index. Absent means the default `""` alone. */
  readonly realmKinds?: readonly string[];
  readonly archetypes: readonly CatalogArchetype[];
  readonly enums: Readonly<Record<string, readonly string[]>>;
  readonly events: readonly CatalogEvent[];
  readonly commands: readonly CatalogCommand[];
  readonly grids: readonly CatalogGrid[];
  readonly metrics: readonly CatalogMetric[];
}

/** A catalog breaks a wire rule, or is not a catalog at all. {@link problems} lists every problem found. */
export class CatalogError extends Error {
  readonly problems: readonly string[];

  constructor(problems: readonly string[]) {
    super(`the catalog breaks ${problems.length} rule(s):\n  - ${problems.join('\n  - ')}`);
    this.name = 'CatalogError';
    this.problems = problems;
  }
}

// ---- parsing --------------------------------------------------------------------------------------------------------

type JsonObject = Readonly<Record<string, unknown>>;
type Mutable<T> = { -readonly [K in keyof T]: T[K] };

/**
 * Parses and validates catalog JSON received in `WELCOME` (or served at `/typhon/catalog.json`), and refuses it unless
 * it is canonical (see `checkCanonical`). Throws
 * {@link CatalogError} listing every problem: a client refuses a catalog it cannot decode at `WELCOME`, never
 * mid-stream.
 */
export function parseCatalog(json: Uint8Array | string): Catalog {
  let text: string | null;
  if (typeof json === 'string') {
    text = json;
  } else {
    text = decodeUtf8(json);
    if (text === null) {
      throw new CatalogError(['catalog JSON is not valid UTF-8']);
    }
  }

  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch (e) {
    throw new CatalogError([`catalog JSON does not parse: ${e instanceof Error ? e.message : String(e)}`]);
  }

  const problems: string[] = [];
  const catalog = readCatalog(raw, problems);
  if (problems.length === 0) {
    validateCatalog(catalog, problems);
  }

  if (problems.length === 0) {
    checkCanonical(catalog, problems);
  }

  if (problems.length > 0) {
    throw new CatalogError(problems);
  }

  return catalog;
}

/**
 * Reads the JSON into the typed model, checking every value's JSON type as the C# model would deserialize it: a required
 * array or object that is missing or null is refused (canonical form writes `[]`), an absent integer reads as 0 (the C#
 * default), and every integer must fit a C# `int`.
 */
function readCatalog(raw: unknown, p: string[]): Catalog {
  const o = asObject(raw, 'catalog', p);
  const protocol = asObject(o.protocol, 'protocol', p);
  const app = asObject(o.app, 'app', p);
  const tick = asObject(o.tick, 'tick', p);
  const limits = asObject(o.limits, 'limits', p);
  // A null-prototype object, so an enum named `__proto__` is an ordinary key.
  const enums = Object.create(null) as Record<string, readonly string[]>;
  const rawEnums = asObject(o.enums, 'enums', p);
  for (const key of Object.keys(rawEnums)) {
    enums[key] = stringArray(rawEnums[key], `enums.${key}`, p);
  }

  return {
    protocol: { major: int(protocol, 'major', 'protocol', p), minor: int(protocol, 'minor', 'protocol', p) },
    app: { name: str(app, 'name', 'app', p), revision: int(app, 'revision', 'app', p) },
    tick: { periodUs: int(tick, 'periodUs', 'tick', p), pingHz: int(tick, 'pingHz', 'tick', p) },
    limits: {
      frameBytes: int(limits, 'frameBytes', 'limits', p),
      clientMessageBytes: int(limits, 'clientMessageBytes', 'limits', p),
      resumeGraceMs: int(limits, 'resumeGraceMs', 'limits', p),
    },
    sessionKinds: stringArray(o.sessionKinds, 'sessionKinds', p),
    ...(o.realmKinds === undefined ? {} : { realmKinds: stringArray(o.realmKinds, 'realmKinds', p) }),
    archetypes: list(o.archetypes, 'archetypes', p, (a, where) => readArchetype(a, where, p)),
    enums,
    events: list(o.events, 'events', p, (e, where) => ({
      idx: int(e, 'idx', where, p),
      name: str(e, 'name', where, p),
      scope: str(e, 'scope', where, p),
      fields: list(e.fields, `${where}.fields`, p, (f, at) => readField(f, at, p)),
    })),
    commands: list(o.commands, 'commands', p, (c, where) => readCommand(c, where, p)),
    grids: list(o.grids, 'grids', p, (g, where) => ({
      idx: int(g, 'idx', where, p),
      tileCells: int(g, 'tileCells', where, p),
      archetypes: intArray(g.archetypes, `${where}.archetypes`, p),
    })),
    metrics: list(o.metrics, 'metrics', p, (m, where) => readMetric(m, where, p)),
  };
}

function readArchetype(a: JsonObject, where: string, p: string[]): CatalogArchetype {
  const archetype: Mutable<CatalogArchetype> = {
    idx: int(a, 'idx', where, p),
    name: str(a, 'name', where, p),
    groups: stringArray(a.groups, `${where}.groups`, p),
    fields: list(a.fields, `${where}.fields`, p, (f, at) => readField(f, at, p)),
  };

  if (present(a.position)) {
    const at = `${where}.position`;
    const o = asObject(a.position, at, p);
    const position: Mutable<CatalogPosition> = { kind: str(o, 'kind', at, p), pos: readCodec(o.pos, `${at}.pos`, p) };
    const model = optionalString(o, 'model', at, p);
    if (model !== undefined) {
      position.model = model;
    }

    if (present(o.vel)) {
      position.vel = readCodec(o.vel, `${at}.vel`, p);
    }

    archetype.position = position;
  }

  if (present(a.owner)) {
    const at = `${where}.owner`;
    const o = asObject(a.owner, at, p);
    archetype.owner = {
      groups: stringArray(o.groups, `${at}.groups`, p),
      fields: list(o.fields, `${at}.fields`, p, (f, fieldAt) => readField(f, fieldAt, p)),
    };
  }

  return archetype;
}

function readCommand(c: JsonObject, where: string, p: string[]): CatalogCommand {
  const command: Mutable<CatalogCommand> = {
    idx: int(c, 'idx', where, p),
    name: str(c, 'name', where, p),
    delivery: str(c, 'delivery', where, p),
    fields: list(c.fields, `${where}.fields`, p, (f, at) => readField(f, at, p)),
  };

  if (present(c.rate)) {
    const rate = asObject(c.rate, `${where}.rate`, p);
    command.rate = { perSec: int(rate, 'perSec', `${where}.rate`, p), burst: int(rate, 'burst', `${where}.rate`, p) };
  }

  return command;
}

function readMetric(m: JsonObject, where: string, p: string[]): CatalogMetric {
  const metric: Mutable<CatalogMetric> = {
    idx: int(m, 'idx', where, p),
    name: str(m, 'name', where, p),
    unit: str(m, 'unit', where, p),
    codec: readCodec(m.codec, `${where}.codec`, p),
  };

  const scope = optionalString(m, 'scope', where, p);
  if (scope !== undefined) {
    metric.scope = scope;
  }

  const kind = optionalString(m, 'kind', where, p);
  if (kind !== undefined) {
    metric.kind = kind;
  }

  if (present(m.labels)) {
    metric.labels = stringArray(m.labels, `${where}.labels`, p);
  }

  return metric;
}

function readField(f: JsonObject, where: string, p: string[]): CatalogField {
  const field: Mutable<CatalogField> = {
    name: str(f, 'name', where, p),
    codec: readCodec(f.codec, `${where}.codec`, p),
  };
  const group = optionalString(f, 'group', where, p);
  if (group !== undefined) {
    field.group = group;
  }

  if (present(f.onEnter)) {
    if (typeof f.onEnter === 'boolean') {
      field.onEnter = f.onEnter;
    } else {
      p.push(`${where}.onEnter must be a boolean`);
    }
  }

  const enumName = optionalString(f, 'enum', where, p);
  if (enumName !== undefined) {
    field.enum = enumName;
  }

  const smoothing = optionalString(f, 'smoothing', where, p);
  if (smoothing !== undefined) {
    field.smoothing = smoothing;
  }

  return field;
}

const CODEC_INTEGERS = ['bits', 'unitExp', 'n', 'maxBytes', 'minCount', 'maxCount', 'fixedBytes'] as const;

/**
 * A codec, and for a list its element. An element's own `of` is refused rather than read, so the recursion is one level
 * deep however far the JSON nests: a hostile catalog cannot exhaust the stack here.
 */
function readCodec(raw: unknown, where: string, p: string[], element = false): CatalogCodec {
  const c = asObject(raw, where, p);
  const codec: Mutable<CatalogCodec> = { t: str(c, 't', where, p) };
  for (const key of CODEC_INTEGERS) {
    if (c[key] !== undefined) {
      codec[key] = int(c, key, where, p);
    }
  }

  if (c.scale !== undefined) {
    codec.scale = num(c, 'scale', where, p);
  }

  if (present(c.min)) {
    codec.min = numberArray(c.min, `${where}.min`, p);
  }

  if (present(c.max)) {
    codec.max = numberArray(c.max, `${where}.max`, p);
  }

  if (present(c.of)) {
    if (element) {
      p.push(`${where}.of: a list element cannot itself be a list`);
    } else {
      codec.of = readCodec(c.of, `${where}.of`, p, true);
    }
  }

  return codec;
}

function present(value: unknown): boolean {
  return value !== undefined && value !== null;
}

const EMPTY_OBJECT: JsonObject = {};

function asObject(value: unknown, where: string, p: string[]): JsonObject {
  if (typeof value === 'object' && value !== null && !Array.isArray(value)) {
    return value as JsonObject;
  }

  p.push(`${where} must be an object`);
  return EMPTY_OBJECT;
}

/** A required array of objects: missing or null is refused, as C# refuses what its canonical form writes as `[]`. */
function list<T>(value: unknown, where: string, p: string[], item: (o: JsonObject, where: string) => T): T[] {
  if (!Array.isArray(value)) {
    p.push(`${where} must be an array`);
    return [];
  }

  return value.map((entry: unknown, i) => item(asObject(entry, `${where}[${i}]`, p), `${where}[${i}]`));
}

function str(o: JsonObject, key: string, where: string, p: string[]): string {
  const value = o[key];
  if (typeof value === 'string') {
    return value;
  }

  p.push(`${where}.${key} must be a string`);
  return '';
}

function optionalString(o: JsonObject, key: string, where: string, p: string[]): string | undefined {
  const value = o[key];
  if (typeof value === 'string') {
    return value;
  }

  if (present(value)) {
    p.push(`${where}.${key} must be a string`);
  }

  return undefined;
}

/** A number; absent reads as 0, the C# default. Null is refused: C# does not deserialize it into a non-nullable one. */
function num(o: JsonObject, key: string, where: string, p: string[]): number {
  const value = o[key];
  if (typeof value === 'number') {
    return value;
  }

  if (value !== undefined) {
    p.push(`${where}.${key} must be a number`);
  }

  return 0;
}

function int(o: JsonObject, key: string, where: string, p: string[]): number {
  const value = num(o, key, where, p);
  if (!isInt32(value)) {
    p.push(`${where}.${key} must be an integer in [-2^31, 2^31 - 1]`);
    return 0;
  }

  return value;
}

/**
 * Whether a value fits the C# `int` every integer catalog property is. After `JSON.parse`, the tokens `1.0` and `1` are
 * the same number, so a spelling C# refuses for an `int` is accepted here — harmless, since the value is identical.
 */
function isInt32(value: number): boolean {
  return Number.isInteger(value) && value >= -0x80000000 && value <= 0x7fffffff;
}

function stringArray(value: unknown, where: string, p: string[]): string[] {
  if (!Array.isArray(value) || !value.every((v) => typeof v === 'string')) {
    p.push(`${where} must be an array of strings`);
    return [];
  }

  return value;
}

function numberArray(value: unknown, where: string, p: string[]): number[] {
  if (!Array.isArray(value) || !value.every((v) => typeof v === 'number')) {
    p.push(`${where} must be an array of numbers`);
    return [];
  }

  return value;
}

function intArray(value: unknown, where: string, p: string[]): number[] {
  if (!Array.isArray(value) || !value.every((v) => typeof v === 'number' && isInt32(v))) {
    p.push(`${where} must be an array of integers in [-2^31, 2^31 - 1]`);
    return [];
  }

  return value as number[];
}

// ---- plans ----------------------------------------------------------------------------------------------------------

/*
 * Compilation is the decoder's trust boundary, as in C#: a plan is built only from a catalog whose shape keeps decoding
 * bounded and well-defined — widths the readers accept, list buffers that fit, groups a `u8` mask addresses, indices
 * that size small arrays — whether or not the catalog went through `parseCatalog`. Everything else (canonical order,
 * built-in shapes, names) is the validator's.
 */

/** What a decoded field value is made of. */
export const ValueKind = {
  /** One to four numbers ({@link FieldPlan.components}): integers, quantized scalars, vectors, a quaternion. */
  Number: 0,
  Text: 1,
  Bytes: 2,
  /** A counted sequence of numeric elements. */
  List: 3,
  /** A codec this library does not know, skipped by its declared width. */
  Skipped: 4,
} as const;

export type ValueKind = (typeof ValueKind)[keyof typeof ValueKind];

const MAX_MESSAGE_INDEX = ProtocolConstants.maxMessageIndex;

const NO_AXES = new Float64Array(0);

function refuse(problem: string): CatalogError {
  return new CatalogError([problem]);
}

function isByteWidth(bits: number): boolean {
  return bits === 8 || bits === 16 || bits === 24 || bits === 32;
}

/**
 * One field compiled for encoding and decoding: its codec's parameters resolved into the numbers the arithmetic needs,
 * and its place in its section. Every property is set in the constructor, so every plan shares one shape and the
 * interpreter's property reads stay monomorphic.
 */
export class FieldPlan {
  readonly name: string;
  /** Position in its record's field list (archetype public fields, owner fields, or a message body); −1 otherwise. */
  readonly index: number;
  /** The catalog field; `null` for a position codec, a list element or a metric value. */
  readonly field: CatalogField | null;
  readonly codec: CatalogCodec;
  readonly kind: CodecKind;
  readonly packed: boolean;
  /** For a packed field, its first bit within the pack. */
  bitOffset = 0;
  /** For a packed field, its width in bits. */
  readonly bitCount: number;
  readonly valueKind: ValueKind;
  /** Numbers per value (per element, for a list); 0 for text and bytes. */
  readonly components: number;
  /** For a list, the element's plan. */
  readonly element: FieldPlan | null;
  /** Byte-aligned width for the quantizing kinds: 8, 16, 24 or 32. */
  readonly bits: number;
  /** Per axis: lower bound (quant, pos). */
  readonly min: Float64Array;
  /** Per axis: (max − min) / 2^bits (quant, pos). */
  readonly step: Float64Array;
  /** 2^bits − 1 (quant, pos, unorm). */
  readonly top: number;
  /** 2^(bits−1) − 1 (vec, vel, snorm). */
  readonly limit: number;
  readonly scale: number;
  /** For a `vel` codec, its absolute unit 2^unitExp metres per tick (W5, `typhon.3`); 0 otherwise. */
  readonly velocityUnit: number;
  readonly n: number;
  readonly maxBytes: number;
  readonly fixedBytes: number;
  readonly minCount: number;
  readonly maxCount: number;
  /** The enum's value names, or `null`. */
  readonly enumNames: readonly string[] | null;

  constructor(name: string, index: number, field: CatalogField | null, codec: CatalogCodec, enums: Catalog['enums']) {
    this.name = name;
    this.index = index;
    this.field = field;
    this.codec = codec;
    const kind = codecKindOf(codec.t);
    this.kind = kind;
    this.packed = isPacked(kind);
    this.bitCount = kind === CodecKind.Bool ? 1 : kind === CodecKind.Bits ? (codec.n ?? 0) : 0;
    this.bits = codec.bits ?? 0;
    this.scale = codec.scale ?? 0;
    this.n = codec.n ?? 0;
    this.maxBytes = codec.maxBytes ?? 0;
    this.fixedBytes = codec.fixedBytes ?? 0;
    this.minCount = codec.minCount ?? 0;
    this.maxCount = codec.maxCount ?? 0;
    const enumName = field?.enum;
    this.enumNames =
      enumName !== undefined && Object.prototype.hasOwnProperty.call(enums, enumName) ? enums[enumName]! : null;

    let valueKind: ValueKind = ValueKind.Number;
    let components = 1;
    switch (kind) {
      case CodecKind.Pos2:
      case CodecKind.Vec2:
      case CodecKind.Vel2:
        components = 2;
        break;
      case CodecKind.Pos3:
      case CodecKind.Vec3:
      case CodecKind.Vel3:
        components = 3;
        break;
      case CodecKind.Quat3:
        components = 4;
        break;
      case CodecKind.Str:
        valueKind = ValueKind.Text;
        components = 0;
        break;
      case CodecKind.Bytes:
      case CodecKind.Blob:
        valueKind = ValueKind.Bytes;
        components = 0;
        break;
      case CodecKind.List:
        valueKind = ValueKind.List;
        components = 0;
        break;
      case CodecKind.Unknown:
        valueKind = ValueKind.Skipped;
        components = 0;
        break;
      default:
        break;
    }

    this.checkWidths(kind);
    if (kind === CodecKind.List) {
      // A list decodes into a buffer sized for 255 elements of at most 4 numbers: its element must be numeric and its
      // count capped, whether or not the catalog was validated.
      const of = codec.of;
      if (of === undefined || !isListElement(codecKindOf(of.t)) || !(this.maxCount >= 0 && this.maxCount <= 255)) {
        throw refuse(`list '${name}' needs a numeric element codec and at most 255 elements`);
      }

      this.element = new FieldPlan(name, -1, null, of, enums);
      components = this.element.components;
    } else {
      this.element = null;
    }

    this.valueKind = valueKind;
    this.components = components;

    // A position's quantum is the realm frame's (typhon.3, SUB-30): RealmFrame.step, per frame.
    if (kind === CodecKind.Quant) {
      const min = codec.min;
      const max = codec.max;
      if (min?.length !== components || max?.length !== components) {
        throw refuse(`field '${name}': ${codec.t} needs ${components} min and max value(s)`);
      }

      this.min = Float64Array.from(min);
      this.step = new Float64Array(components);
      for (let i = 0; i < components; i++) {
        this.step[i] = quantStep(min[i]!, max[i]!, this.bits);
      }
    } else {
      this.min = NO_AXES;
      this.step = NO_AXES;
    }

    if (kind === CodecKind.Vel2 || kind === CodecKind.Vel3) {
      const e = codec.unitExp;
      if (
        e === undefined ||
        !Number.isSafeInteger(e) ||
        e < ProtocolConstants.minVelocityUnitExp ||
        e > ProtocolConstants.maxVelocityUnitExp
      ) {
        throw refuse(
          `field '${name}': a velocity needs unitExp in [${ProtocolConstants.minVelocityUnitExp}, ${ProtocolConstants.maxVelocityUnitExp}]`,
        );
      }

      this.velocityUnit = 2 ** e;
    } else {
      this.velocityUnit = 0;
    }

    const byteAligned = isByteWidth(this.bits);
    this.top = byteAligned ? unsignedTop(this.bits) : 0;
    this.limit = byteAligned ? symmetricLimit(this.bits) : 0;
  }

  /** Refuses a width no reader accepts, so a decode never meets one. */
  private checkWidths(kind: CodecKind): void {
    switch (kind) {
      case CodecKind.Quant:
      case CodecKind.Vec2:
      case CodecKind.Vec3:
      case CodecKind.Vel2:
      case CodecKind.Vel3:
      case CodecKind.Unorm:
      case CodecKind.Snorm:
      case CodecKind.Angle:
        if (!isByteWidth(this.bits)) {
          throw refuse(`field '${this.name}': ${this.codec.t} needs bits of 8, 16, 24 or 32`);
        }

        break;
      case CodecKind.Bits:
        if (!(Number.isInteger(this.n) && this.n >= 1 && this.n <= 24)) {
          throw refuse(`field '${this.name}': bits.n must be in [1, 24]`);
        }

        break;
      case CodecKind.Bytes:
        if (!(Number.isInteger(this.n) && this.n >= 0)) {
          throw refuse(`field '${this.name}': bytes.n must be a non-negative integer`);
        }

        break;
      case CodecKind.Str:
      case CodecKind.Blob:
        if (!(Number.isInteger(this.maxBytes) && this.maxBytes >= 0)) {
          throw refuse(`field '${this.name}': maxBytes must be a non-negative integer`);
        }

        break;
      case CodecKind.Unknown:
        if (!(Number.isInteger(this.fixedBytes) && this.fixedBytes > 0)) {
          throw refuse(`field '${this.name}': codec '${this.codec.t}' is unknown and declares no fixedBytes`);
        }

        break;
      default:
        break;
    }
  }
}

/** A section compiled: its fields in wire order (packed first) and the size of its leading pack. */
export class SectionPlan {
  readonly fields: readonly FieldPlan[];
  /** How many leading fields are packed. */
  readonly packedCount: number;
  /** ⌈Σn / 8⌉, 0 when no field is packed. */
  readonly packBytes: number;

  constructor(fields: readonly FieldPlan[]) {
    this.fields = fields;
    let bits = 0;
    let packed = 0;
    for (let i = 0; i < fields.length; i++) {
      const f = fields[i]!;
      if (f.packed) {
        if (packed !== i) {
          throw refuse(`field '${f.name}' is packed but follows a byte-aligned field: not in wire order (W11)`);
        }

        f.bitOffset = bits;
        bits += f.bitCount;
        packed++;
      }
    }

    this.packedCount = packed;
    this.packBytes = (bits + 7) >> 3;
  }
}

/** An archetype's position, compiled. */
export class PositionPlan {
  /** Segments are replicated (`motion`), rather than one position on enter (`static`). */
  readonly moving: boolean;
  /** A segment carries a velocity. */
  readonly linear: boolean;
  /** 2 or 3. */
  readonly dims: number;
  readonly pos: FieldPlan;
  readonly vel: FieldPlan | null;

  constructor(position: CatalogPosition, enums: Catalog['enums']) {
    this.moving = position.kind === 'motion';
    this.linear = this.moving && position.model === 'linear';
    this.pos = new FieldPlan('position', -1, null, position.pos, enums);
    if (this.pos.kind !== CodecKind.Pos2 && this.pos.kind !== CodecKind.Pos3) {
      throw refuse(`a position needs a pos2 or pos3 codec, not '${position.pos.t}'`);
    }

    this.dims = this.pos.components;
    if (this.linear) {
      const vel = position.vel;
      const velKind = vel === undefined ? CodecKind.Unknown : codecKindOf(vel.t);
      if (vel === undefined || (velKind !== CodecKind.Vel2 && velKind !== CodecKind.Vel3)) {
        throw refuse('the linear model needs a vel2 or vel3 codec');
      }

      this.vel = new FieldPlan('velocity', -1, null, vel, enums);
      if (this.vel.components !== this.dims) {
        throw refuse("the linear model needs a vel codec matching pos's dimensions");
      }
    } else {
      this.vel = null;
    }
  }
}

/** An archetype compiled for decoding and encoding its records. */
export class ArchetypePlan {
  readonly archetype: CatalogArchetype;
  readonly idx: number;
  readonly name: string;
  readonly position: PositionPlan | null;
  /** The public change groups, in bit order. */
  readonly groups: readonly string[];
  readonly onEnter: SectionPlan;
  /** One section per public group, in bit order. */
  readonly groupSections: readonly SectionPlan[];
  /** Every public field, in wire order; {@link FieldPlan.index} is the position here. */
  readonly fields: readonly FieldPlan[];
  readonly ownerGroups: readonly string[];
  readonly ownerSections: readonly SectionPlan[];
  /** Every owner field, in wire order; {@link FieldPlan.index} is the position here. */
  readonly ownerFields: readonly FieldPlan[];

  constructor(archetype: CatalogArchetype, enums: Catalog['enums']) {
    this.archetype = archetype;
    this.idx = archetype.idx;
    this.name = archetype.name;
    if (archetype.groups.length > 8 || (archetype.owner?.groups.length ?? 0) > 8) {
      throw refuse(`archetype '${archetype.name}' has more groups than a u8 mask addresses`);
    }

    this.position = archetype.position === undefined ? null : new PositionPlan(archetype.position, enums);
    this.groups = archetype.groups;

    const fields: FieldPlan[] = [];
    this.onEnter = buildSection(archetype.fields, (f) => f.onEnter === true, fields, enums);
    this.groupSections = archetype.groups.map((group) =>
      buildSection(archetype.fields, (f) => f.onEnter !== true && f.group === group, fields, enums),
    );
    this.fields = fields;

    const ownerFields: FieldPlan[] = [];
    this.ownerGroups = archetype.owner?.groups ?? [];
    this.ownerSections = this.ownerGroups.map((group) =>
      buildSection(archetype.owner?.fields ?? [], (f) => f.group === group, ownerFields, enums),
    );
    this.ownerFields = ownerFields;
  }
}

function buildSection(
  declared: readonly CatalogField[],
  belongs: (f: CatalogField) => boolean,
  all: FieldPlan[],
  enums: Catalog['enums'],
): SectionPlan {
  const fields: FieldPlan[] = [];
  for (const f of declared) {
    if (belongs(f)) {
      const plan = new FieldPlan(f.name, all.length, f, f.codec, enums);
      fields.push(plan);
      all.push(plan);
    }
  }

  return new SectionPlan(fields);
}

/** An event or command type compiled: its index and its single body section. */
export class MessagePlan {
  readonly idx: number;
  readonly name: string;
  readonly body: SectionPlan;

  constructor(idx: number, name: string, fields: readonly CatalogField[], enums: Catalog['enums']) {
    this.idx = idx;
    this.name = name;
    this.body = new SectionPlan(fields.map((f, i) => new FieldPlan(f.name, i, f, f.codec, enums)));
  }
}

/** A metric compiled: its codec, how many values it contributes, and where they sit in a flattened value table. */
export class MetricPlan {
  readonly metric: CatalogMetric;
  readonly idx: number;
  readonly name: string;
  /** Whether it belongs to the per-session segment of `STATS`. */
  readonly session: boolean;
  /** Its label count, or 1 for a scalar. */
  readonly valueCount: number;
  /** Where its first value sits among every metric's values, in catalog order. */
  readonly offset: number;
  readonly value: FieldPlan;

  constructor(metric: CatalogMetric, offset: number, enums: Catalog['enums']) {
    this.metric = metric;
    this.idx = metric.idx;
    this.name = metric.name;
    this.session = metric.scope === 'session';
    this.valueCount = metric.labels?.length ?? 1;
    this.offset = offset;
    this.value = new FieldPlan(metric.name, -1, null, metric.codec, enums);
    // A metric is a byte-aligned number: STATS has no bit pack, so `bool` and `bits` have nowhere to travel.
    if (
      (this.value.valueKind !== ValueKind.Number && this.value.valueKind !== ValueKind.Skipped) ||
      this.value.packed
    ) {
      throw refuse(`metric '${metric.name}': codec '${metric.codec.t}' is not a byte-aligned number`);
    }
  }
}

/**
 * A grid compiled: its tile and a reusable buffer for one cell's counts. Its origin and dimensions are the session's
 * realm frame's (`RealmFrame.aggregateDim`), not the catalog's.
 */
export class GridPlan {
  readonly grid: CatalogGrid;
  readonly idx: number;
  /** The tile, in replication cells. */
  readonly tileCells: number;
  /** One count per archetype of {@link CatalogGrid.archetypes}; reused for every cell a decoder reads. */
  readonly counts: Uint32Array;

  constructor(grid: CatalogGrid, archetypeCount: number) {
    this.grid = grid;
    this.idx = grid.idx;
    if (!(Number.isInteger(grid.tileCells) && grid.tileCells >= 1)) {
      throw refuse(`grid ${grid.idx} needs a tile of at least one replication cell`);
    }

    for (const a of grid.archetypes) {
      if (!(Number.isInteger(a) && a >= 0 && a < archetypeCount)) {
        throw refuse(`grid ${grid.idx} counts archetype ${a}, which does not exist`);
      }
    }

    this.tileCells = grid.tileCells;
    this.counts = new Uint32Array(grid.archetypes.length);
  }
}

/**
 * A canonical catalog compiled into the plans both directions work from. Compiled once per catalog, at `WELCOME`, so no
 * record decode ever looks a codec parameter up by name.
 */
export class CatalogPlan {
  readonly catalog: Catalog;
  readonly archetypes: readonly ArchetypePlan[];
  /** Server-scope metrics in index order: the first segment of `STATS`. */
  readonly serverMetrics: readonly MetricPlan[];
  /** Session-scope metrics in index order: the second segment. */
  readonly sessionMetrics: readonly MetricPlan[];
  /** Every metric in catalog order. */
  readonly metrics: readonly MetricPlan[];
  /** Values across every metric: the size of a flattened value table. */
  readonly metricValueCount: number;
  readonly grids: readonly GridPlan[];
  /** The realm kinds: a `REALM` block's `kindIdx` indexes this. `[""]` for a catalog that declares none. */
  readonly realmKinds: readonly string[];

  private readonly events: readonly (MessagePlan | undefined)[];
  private readonly commands: readonly (MessagePlan | undefined)[];

  private constructor(catalog: Catalog) {
    this.catalog = catalog;
    const enums = catalog.enums;
    // The store sizes each slot's motion ring from it.
    if (!(Number.isInteger(catalog.tick.periodUs) && catalog.tick.periodUs > 0)) {
      throw refuse(`tick period of ${catalog.tick.periodUs} µs; a positive integer expected`);
    }

    if (catalog.archetypes.length > 255) {
      throw refuse(`${catalog.archetypes.length} archetypes; a store addresses at most 255`);
    }

    this.archetypes = catalog.archetypes.map((a, i) => {
      if (a.idx !== i) {
        throw refuse(`archetype '${a.name}' has index ${a.idx} at position ${i}`);
      }

      return new ArchetypePlan(a, enums);
    });
    this.events = indexed(catalog.events.map((e) => new MessagePlan(e.idx, e.name, e.fields, enums)));
    this.commands = indexed(catalog.commands.map((c) => new MessagePlan(c.idx, c.name, c.fields, enums)));

    const all: MetricPlan[] = [];
    let offset = 0;
    let previousIdx = -1;
    for (const m of catalog.metrics) {
      if (!(Number.isInteger(m.idx) && m.idx > previousIdx && m.idx <= MAX_MESSAGE_INDEX)) {
        throw refuse(`metric '${m.name}' is out of index order: STATS segments are laid out in index order`);
      }

      previousIdx = m.idx;
      const plan = new MetricPlan(m, offset, enums);
      offset += plan.valueCount;
      all.push(plan);
    }

    this.metrics = all;
    this.metricValueCount = offset;
    this.serverMetrics = all.filter((m) => !m.session);
    this.sessionMetrics = all.filter((m) => m.session);
    this.realmKinds = catalog.realmKinds !== undefined && catalog.realmKinds.length > 0 ? catalog.realmKinds : [''];
    this.grids = catalog.grids.map((g, i) => {
      if (g.idx !== i) {
        throw refuse(`grid at position ${i} has index ${g.idx}`);
      }

      return new GridPlan(g, catalog.archetypes.length);
    });
  }

  /**
   * Compiles a catalog. A catalog from {@link parseCatalog} is valid and canonical; any other is checked here for
   * what keeps decoding bounded, and refused with {@link CatalogError}.
   */
  static compile(catalog: Catalog): CatalogPlan {
    return new CatalogPlan(catalog);
  }

  /** The archetype at a wire index read from a peer; 1007 when there is none. */
  archetype(idx: number): ArchetypePlan {
    const plan = this.archetypes[idx];
    if (plan === undefined) {
      throw malformed(`archetype index ${idx} does not exist`);
    }

    return plan;
  }

  /** The event type at a wire index read from a peer; 1007 when there is none. */
  event(idx: number): MessagePlan {
    const plan = this.events[idx];
    if (plan === undefined) {
      throw malformed(`event index ${idx} does not exist`);
    }

    return plan;
  }

  /** The command type at a wire index read from a peer; 1007 when there is none. */
  command(idx: number): MessagePlan {
    const plan = this.commands[idx];
    if (plan === undefined) {
      throw malformed(`command index ${idx} does not exist`);
    }

    return plan;
  }

  /** The grid at a wire index read from a peer; 1007 when there is none. */
  grid(idx: number): GridPlan {
    const plan = this.grids[idx];
    if (plan === undefined) {
      throw malformed(`grid index ${idx} does not exist`);
    }

    return plan;
  }

  archetypeByName(name: string): ArchetypePlan | null {
    return this.archetypes.find((a) => a.name === name) ?? null;
  }

  eventByName(name: string): MessagePlan | null {
    return this.events.find((e) => e?.name === name) ?? null;
  }

  commandByName(name: string): MessagePlan | null {
    return this.commands.find((c) => c?.name === name) ?? null;
  }

  metricByName(name: string): MetricPlan | null {
    return this.metrics.find((m) => m.name === name) ?? null;
  }

  /** The built-in `ClientRegion` command, when the server enables it. */
  get clientRegion(): MessagePlan | null {
    return this.commands[BuiltInCommand.ClientRegionIdx] ?? null;
  }
}

/** Sparse by index: event and command indices start at reserved bases (W27), within [0, 65535] and each used once. */
function indexed(plans: readonly MessagePlan[]): (MessagePlan | undefined)[] {
  let max = -1;
  for (const p of plans) {
    if (!(Number.isInteger(p.idx) && p.idx >= 0 && p.idx <= MAX_MESSAGE_INDEX)) {
      throw refuse(`wire index ${p.idx} is outside [0, ${MAX_MESSAGE_INDEX}]`);
    }

    max = Math.max(max, p.idx);
  }

  const result = new Array<MessagePlan | undefined>(max + 1).fill(undefined);
  for (const p of plans) {
    if (result[p.idx] !== undefined) {
      throw refuse(`wire index ${p.idx} is assigned twice`);
    }

    result[p.idx] = p;
  }

  return result;
}
