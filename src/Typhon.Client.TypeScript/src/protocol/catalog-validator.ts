import type {
  Catalog,
  CatalogArchetype,
  CatalogCodec,
  CatalogCommand,
  CatalogField,
  CatalogGrid,
  CatalogMetric,
  CatalogPosition,
} from './catalog.js';
import { codecKindOf, CodecKind, isListElement, isPacked } from './codec-kinds.js';
import { BuiltInCommand, ProtocolConstants } from './constants.js';
import { pow2, quantStep } from './math.js';
import { encodeUtf8 } from './utf8.js';

/*
 * The rules a client checks at `WELCOME`, as `CatalogValidator.cs` and `CatalogSerializer.FromUtf8` state them
 * (`03-wire-protocol.md` § 10, "Precisions settled while building Phase 0"): a broken or hostile server fails at the
 * handshake, never mid-stream.
 */

const ENUM_CODECS: readonly string[] = ['bits', 'u8', 'u16', 'varu'];

const METRIC_CODECS: readonly CodecKind[] = [
  CodecKind.U8,
  CodecKind.U16,
  CodecKind.U32,
  CodecKind.I8,
  CodecKind.I16,
  CodecKind.I32,
  CodecKind.Varu,
  CodecKind.Vari,
  CodecKind.F16,
  CodecKind.F32,
  CodecKind.Unorm,
  CodecKind.Quant,
  CodecKind.Unknown,
];

/** The built-in metrics at their reserved indices (W25): name, unit, codec, kind, scope, and whether labelled. */
const BUILT_IN_METRICS: readonly (readonly [string, string, CodecKind, string, string, boolean])[] = [
  ['typhon.tick.p50', 'ms', CodecKind.F16, 'gauge', 'server', false],
  ['typhon.tick.p99', 'ms', CodecKind.F16, 'gauge', 'server', false],
  ['typhon.system.mean', 'ms', CodecKind.F16, 'gauge', 'server', true],
  ['typhon.archetype.entities', 'count', CodecKind.Varu, 'gauge', 'server', true],
  ['typhon.sessions', 'count', CodecKind.Varu, 'gauge', 'server', false],
  ['typhon.net.outBytesPerSec', 'B/s', CodecKind.Varu, 'gauge', 'server', false],
  ['typhon.subscriptions.track.p99', 'ms', CodecKind.F16, 'gauge', 'server', false],
  ['typhon.durability.wait.p99', 'ms', CodecKind.F16, 'gauge', 'server', false],
  ['typhon.session.outBytesPerSec', 'B/s', CodecKind.Varu, 'gauge', 'session', false],
  ['typhon.session.skippedFrames', 'count', CodecKind.Varu, 'counter', 'session', false],
  ['typhon.session.droppedCommands', 'count', CodecKind.Varu, 'counter', 'session', false],
];

/** The reserved index of a built-in command, or −1. */
function reservedCommandIdx(name: string): number {
  return name === BuiltInCommand.ClientRegion
    ? BuiltInCommand.ClientRegionIdx
    : name === BuiltInCommand.SubscribeRequest
      ? BuiltInCommand.SubscribeRequestIdx
      : -1;
}

/** The reserved index of a built-in metric, or −1. */
function reservedMetricIdx(name: string): number {
  return BUILT_IN_METRICS.findIndex((m) => m[0] === name);
}

/** Appends every wire rule `c` breaks to `problems`. */
export function validateCatalog(c: Catalog, problems: string[]): void {
  if (c.protocol.major !== ProtocolConstants.major || c.protocol.minor < 0) {
    problems.push(
      `protocol ${c.protocol.major}.${c.protocol.minor}; this client speaks major ${ProtocolConstants.major}`,
    );
  }

  if (c.app.name === '') {
    problems.push('app.name is missing');
  }

  if (c.tick.periodUs <= 0 || c.tick.pingHz <= 0) {
    problems.push('tick.periodUs and tick.pingHz must be positive');
  }

  let maxBytes = Number.MAX_SAFE_INTEGER;
  if (c.limits.frameBytes <= 0 || c.limits.clientMessageBytes <= 0 || c.limits.resumeGraceMs < 0) {
    problems.push(
      'limits.frameBytes and limits.clientMessageBytes must be positive, limits.resumeGraceMs non-negative',
    );
  } else {
    maxBytes = c.limits.frameBytes;
  }

  checkStrings('sessionKinds', c.sessionKinds, ProtocolConstants.sessionKindMaxBytes, problems);
  for (const [name, names] of Object.entries(c.enums)) {
    if (name === '') {
      problems.push('an enum needs a name');
    }

    checkStrings(`enum '${name}'`, names, Number.MAX_SAFE_INTEGER, problems);
    if (names.length === 0) {
      problems.push(`enum '${name}' has no names`);
    }
  }

  if (c.archetypes.length > ProtocolConstants.maxArchetypes) {
    problems.push(`${c.archetypes.length} archetypes; at most ${ProtocolConstants.maxArchetypes}`);
  }

  uniqueNames(c.archetypes, 'archetype', problems);
  for (const a of c.archetypes) {
    checkArchetype(a, c.enums, maxBytes, problems);
  }

  uniqueNames(c.events, 'event', problems);
  uniqueIndices(c.events, 'event', problems);
  for (const e of c.events) {
    if (e.scope === '') {
      problems.push(`event '${e.name}' needs a scope`);
    }

    checkMessageFields(`event '${e.name}'`, e.fields, c.enums, maxBytes, problems);
  }

  uniqueNames(c.commands, 'command', problems);
  uniqueIndices(c.commands, 'command', problems);
  for (const cmd of c.commands) {
    checkCommand(cmd, c.enums, maxBytes, problems);
  }

  uniqueNames(c.metrics, 'metric', problems);
  uniqueIndices(c.metrics, 'metric', problems);
  for (const m of c.metrics) {
    checkMetric(m, problems);
  }

  checkGrids(c.grids, c.archetypes.length, problems);
}

/**
 * Appends a problem unless `c` is exactly what canonicalization produces — indices, orders, reserved ranges and omitted
 * defaults. A plan built from a non-canonical catalog would silently map mask bits and indices to the wrong fields, so
 * this is a refusal at `WELCOME`, never a repair. Checks order; it never sorts.
 */
export function checkCanonical(c: Catalog, problems: string[]): void {
  const fail = (what: string): void => {
    problems.push(`the catalog is not canonical: ${what}`);
  };

  if (!ascending(c.sessionKinds)) {
    fail('sessionKinds are not in ordinal order');
  }

  // Enum key order is not checked: JSON.parse moves integer-like keys ("9", "10") ahead of the others, so a valid
  // catalog would be refused, and the order of enums never affects decoding (a field names its enum).

  c.archetypes.forEach((a, i) => {
    if (a.idx !== i || (i > 0 && !(c.archetypes[i - 1]!.name < a.name))) {
      fail(`archetype '${a.name}' is not at its ordinal position, or its idx is not that position`);
    }

    if (!ascending(a.groups) || !fieldsInLayoutOrder(a.fields, a.groups)) {
      fail(`archetype '${a.name}' groups or fields are not in wire order`);
    }

    if (a.owner !== undefined && (!ascending(a.owner.groups) || !fieldsInLayoutOrder(a.owner.fields, a.owner.groups))) {
      fail(`archetype '${a.name}' owner groups or fields are not in wire order`);
    }
  });

  c.events.forEach((e, i) => {
    if (e.idx !== ProtocolConstants.firstAppEventIdx + i || (i > 0 && !(c.events[i - 1]!.name < e.name))) {
      fail(`event '${e.name}' is not at its ordinal index`);
    }

    if (!fieldsInLayoutOrder(e.fields, [])) {
      fail(`event '${e.name}' fields are not in wire order`);
    }
  });

  checkReservedOrder(c.commands, reservedCommandIdx, ProtocolConstants.firstAppCommandIdx, 'command', fail);
  for (const cmd of c.commands) {
    if (!fieldsInLayoutOrder(cmd.fields, [])) {
      fail(`command '${cmd.name}' fields are not in wire order`);
    }
  }

  checkReservedOrder(c.metrics, reservedMetricIdx, ProtocolConstants.firstAppMetricIdx, 'metric', fail);
  for (const m of c.metrics) {
    if (m.scope === 'server' || m.kind === 'gauge') {
      fail(`metric '${m.name}' spells out a default scope or kind`);
    }
  }

  c.grids.forEach((g, i) => {
    // Canonical order is total, so comparing neighbours finds a duplicate too: it compares equal to the grid before it.
    if (g.idx !== i || (i > 0 && compareGrids(c.grids[i - 1]!, g) >= 0)) {
      fail(`grid ${g.idx} is not at its sorted position, or duplicates the grid before it`);
    }
  });
}

function checkReservedOrder(
  items: readonly { readonly idx: number; readonly name: string }[],
  reservedIdx: (name: string) => number,
  base: number,
  what: string,
  fail: (what: string) => void,
): void {
  let appRank = 0;
  let previousApp: string | null = null;
  items.forEach((item, i) => {
    if (i > 0 && !(items[i - 1]!.idx < item.idx)) {
      fail(`${what}s are not in index order`);
    }

    const reserved = reservedIdx(item.name);
    if (reserved >= 0) {
      if (item.idx !== reserved) {
        fail(`built-in ${what} '${item.name}' is not at its reserved index ${reserved}`);
      }

      return;
    }

    if (item.idx !== base + appRank || (previousApp !== null && !(previousApp < item.name))) {
      fail(`${what} '${item.name}' is not at its ordinal index from ${base}`);
    }

    previousApp = item.name;
    appRank++;
  });
}

function ascending(values: readonly string[]): boolean {
  for (let i = 1; i < values.length; i++) {
    if (!(values[i - 1]! < values[i]!)) {
      return false;
    }
  }

  return true;
}

/** Fields ordered by (section, packed first, ordinal name) — W11; section 0 is onEnter, group g is section 1 + g. */
function fieldsInLayoutOrder(fields: readonly CatalogField[], groups: readonly string[]): boolean {
  const key = (f: CatalogField): [number, number] => [
    f.onEnter === true ? 0 : 1 + groups.indexOf(f.group ?? ''),
    isPacked(codecKindOf(f.codec.t)) ? 0 : 1,
  ];
  for (let i = 1; i < fields.length; i++) {
    const [s0, p0] = key(fields[i - 1]!);
    const [s1, p1] = key(fields[i]!);
    const ordered = s0 !== s1 ? s0 < s1 : p0 !== p1 ? p0 < p1 : fields[i - 1]!.name < fields[i]!.name;
    if (!ordered) {
      return false;
    }
  }

  return true;
}

function compareGrids(a: CatalogGrid, b: CatalogGrid): number {
  const origin = compareSequences(a.origin, b.origin);
  if (origin !== 0) {
    return origin;
  }

  if (a.cell !== b.cell) {
    return a.cell < b.cell ? -1 : 1;
  }

  const dims = compareSequences(a.dims, b.dims);
  return dims !== 0 ? dims : compareSequences(a.archetypes, b.archetypes);
}

function compareSequences(a: readonly number[], b: readonly number[]): number {
  for (let i = 0; i < Math.min(a.length, b.length); i++) {
    if (a[i] !== b[i]) {
      return a[i]! < b[i]! ? -1 : 1;
    }
  }

  return a.length - b.length;
}

function checkCommand(cmd: CatalogCommand, enums: Catalog['enums'], maxBytes: number, problems: string[]): void {
  if (cmd.delivery !== 'queued' && cmd.delivery !== 'latest') {
    problems.push(`command '${cmd.name}': delivery must be 'queued' or 'latest'`);
  }

  if (cmd.rate !== undefined && (cmd.rate.perSec <= 0 || cmd.rate.burst <= 0)) {
    problems.push(`command '${cmd.name}': rate.perSec and rate.burst must be positive`);
  }

  if (cmd.name === BuiltInCommand.ClientRegion && !hasClientRegionShape(cmd)) {
    problems.push(`command '${cmd.name}' is a built-in whose shape does not match its definition`);
  } else if (cmd.name === BuiltInCommand.SubscribeRequest) {
    problems.push(`command '${cmd.name}' is reserved; its shape is decided when source subscriptions are built`);
  }

  checkMessageFields(`command '${cmd.name}'`, cmd.fields, enums, maxBytes, problems);
}

/** `ClientRegion` is recognised by shape, not by name (W28). */
function hasClientRegionShape(cmd: CatalogCommand): boolean {
  const field = (name: string) => cmd.fields.find((f) => f.name === name)?.codec;
  const vertices = field(BuiltInCommand.regionVerticesField);
  return (
    cmd.fields.length === 3 &&
    cmd.delivery === 'latest' &&
    cmd.rate?.perSec === 5 &&
    cmd.rate.burst === 5 &&
    vertices?.t === 'list' &&
    vertices.minCount === 3 &&
    vertices.maxCount === 16 &&
    vertices.of?.t === 'pos2' &&
    field(BuiltInCommand.regionAltitudeField)?.t === 'f16' &&
    field(BuiltInCommand.regionBudgetField)?.t === 'u16' &&
    cmd.fields.every((f) => f.group === undefined && f.onEnter !== true && f.enum === undefined)
  );
}

function checkArchetype(a: CatalogArchetype, enums: Catalog['enums'], maxBytes: number, problems: string[]): void {
  const where = `archetype '${a.name}'`;
  checkGroups(where, a.groups, true, problems);
  const names = new Set<string>();
  for (const f of a.fields) {
    const at = `${where} field '${f.name}'`;
    if (names.has(f.name)) {
      problems.push(`${at} is declared twice`);
    }

    names.add(f.name);
    const hasGroup = f.group !== undefined && f.group !== '';
    if (hasGroup === (f.onEnter === true)) {
      problems.push(`${at} must set exactly one of group and onEnter`);
    } else if (hasGroup && !a.groups.includes(f.group)) {
      problems.push(`${at} names group '${f.group}', which the archetype does not declare`);
    }

    checkField(at, f, enums, false, maxBytes, problems);
  }

  if (a.position !== undefined) {
    checkPosition(where, a.position, problems);
  }

  if (a.owner !== undefined) {
    checkGroups(`${where} owner`, a.owner.groups, false, problems);
    for (const f of a.owner.fields) {
      const at = `${where} owner field '${f.name}'`;
      if (names.has(f.name)) {
        problems.push(`${at} is declared twice`);
      }

      names.add(f.name);
      if (f.onEnter === true || f.group === undefined || !a.owner.groups.includes(f.group)) {
        problems.push(`${at} must name one of the owner groups, and cannot be onEnter`);
      }

      checkField(at, f, enums, false, maxBytes, problems);
    }
  }
}

function checkGroups(where: string, groups: readonly string[], allowEmpty: boolean, problems: string[]): void {
  if (groups.length > ProtocolConstants.maxGroups || (!allowEmpty && groups.length === 0)) {
    problems.push(`${where}: ${groups.length} groups; ${allowEmpty ? 0 : 1}..${ProtocolConstants.maxGroups} allowed`);
  }

  if (new Set(groups).size !== groups.length || groups.includes('')) {
    problems.push(`${where}: a group is empty or declared twice`);
  }
}

function checkMessageFields(
  where: string,
  fields: readonly CatalogField[],
  enums: Catalog['enums'],
  maxBytes: number,
  problems: string[],
): void {
  const names = new Set<string>();
  for (const f of fields) {
    const at = `${where} field '${f.name}'`;
    if (names.has(f.name)) {
      problems.push(`${at} is declared twice`);
    }

    names.add(f.name);
    if ((f.group !== undefined && f.group !== '') || f.onEnter === true) {
      problems.push(`${at}: event and command fields carry no group and no onEnter`);
    }

    checkField(at, f, enums, true, maxBytes, problems);
  }
}

function checkField(
  at: string,
  f: CatalogField,
  enums: Catalog['enums'],
  allowList: boolean,
  maxBytes: number,
  problems: string[],
): void {
  if (f.name === '') {
    problems.push(`${at}: a field needs a name`);
  }

  const kind = codecKindOf(f.codec.t);
  if (kind === CodecKind.Vel2 || kind === CodecKind.Vel3) {
    problems.push(`${at}: a vel codec is only valid inside a position`);
  }

  if (kind === CodecKind.List && !allowList) {
    problems.push(`${at}: a list is only valid in event and command fields`);
  }

  checkCodec(at, f.codec, maxBytes, problems);
  if (f.enum === undefined || f.enum === '') {
    return;
  }

  const names = Object.prototype.hasOwnProperty.call(enums, f.enum) ? enums[f.enum] : undefined;
  if (!ENUM_CODECS.includes(f.codec.t)) {
    problems.push(`${at}: an enum is allowed only on bits, u8, u16 and varu, not '${f.codec.t}'`);
  } else if (names === undefined) {
    problems.push(`${at}: enum '${f.enum}' is not declared`);
  } else {
    const n = f.codec.n ?? 0;
    const capacity = kind === CodecKind.Bits ? pow2(Math.min(Math.max(n, 1), 24)) : kind === CodecKind.U8 ? 256 : 65536;
    if (names.length > capacity) {
      problems.push(`${at}: enum '${f.enum}' has ${names.length} names; the codec holds ${capacity}`);
    }
  }
}

function checkPosition(where: string, position: CatalogPosition, problems: string[]): void {
  const posKind = codecKindOf(position.pos.t);
  const dims = posKind === CodecKind.Pos2 ? 2 : posKind === CodecKind.Pos3 ? 3 : 0;
  if (dims === 0) {
    problems.push(`${where} position: pos must be a pos2 or pos3 codec`);
  } else {
    checkCodec(`${where} position.pos`, position.pos, Number.MAX_SAFE_INTEGER, problems);
  }

  if (position.kind === 'static') {
    if (position.model !== undefined || position.vel !== undefined) {
      problems.push(`${where} position: a static position has no model and no vel`);
    }
  } else if (position.kind === 'motion') {
    if (position.model === 'linear') {
      const velKind = position.vel === undefined ? CodecKind.Unknown : codecKindOf(position.vel.t);
      const velDims = velKind === CodecKind.Vel2 ? 2 : velKind === CodecKind.Vel3 ? 3 : 0;
      if (velDims === 0 || velDims !== dims) {
        problems.push(`${where} position: the linear model needs a vel codec matching pos's dimensions`);
      } else {
        checkCodec(`${where} position.vel`, position.vel!, Number.MAX_SAFE_INTEGER, problems);
      }
    } else if (position.model === 'none') {
      if (position.vel !== undefined) {
        problems.push(`${where} position: the none model carries no vel`);
      }
    } else {
      problems.push(`${where} position: model must be 'linear' or 'none'`);
    }
  } else {
    problems.push(`${where} position: kind must be 'motion' or 'static'`);
  }
}

function checkMetric(m: CatalogMetric, problems: string[]): void {
  const where = `metric '${m.name}'`;
  const reserved = reservedMetricIdx(m.name);
  if (reserved >= 0) {
    const [, unit, codec, kind, scope, labelled] = BUILT_IN_METRICS[reserved]!;
    const shaped =
      m.unit === unit &&
      codecKindOf(m.codec.t) === codec &&
      (m.kind ?? 'gauge') === kind &&
      (m.scope ?? 'server') === scope &&
      (m.labels !== undefined) === labelled;
    if (!shaped) {
      problems.push(`${where} is a built-in whose shape does not match its definition`);
    }
  } else if (m.name.startsWith('typhon.')) {
    problems.push(`${where}: the 'typhon.' prefix is reserved for built-in metrics`);
  }

  if (m.scope !== undefined && m.scope !== 'server' && m.scope !== 'session') {
    problems.push(`${where}: scope must be 'server' or 'session'`);
  }

  if (m.kind !== undefined && m.kind !== 'gauge' && m.kind !== 'counter') {
    problems.push(`${where}: kind must be 'gauge' or 'counter'`);
  }

  if (m.labels !== undefined) {
    if (m.labels.length === 0) {
      problems.push(`${where}: a vector metric needs at least one label`);
    }

    checkStrings(`${where} labels`, m.labels, Number.MAX_SAFE_INTEGER, problems);
  }

  if (!METRIC_CODECS.includes(codecKindOf(m.codec.t))) {
    problems.push(`${where}: codec '${m.codec.t}' is not a metric codec`);
  }

  checkCodec(where, m.codec, Number.MAX_SAFE_INTEGER, problems);
}

function checkGrids(grids: readonly CatalogGrid[], archetypeCount: number, problems: string[]): void {
  grids.forEach((g, i) => {
    const where = `grid ${i}`;
    if ((g.dims.length !== 2 && g.dims.length !== 3) || g.origin.length !== g.dims.length) {
      problems.push(`${where}: dims and origin need 2 or 3 matching axes`);
    }

    if (!g.origin.every((o) => Number.isFinite(o))) {
      problems.push(`${where}: origin must be finite`);
    }

    if (!(g.cell > 0 && Number.isFinite(g.cell))) {
      problems.push(`${where}: cell must be positive and finite`);
    }

    let cells = 1;
    for (const d of g.dims) {
      if (!(Number.isInteger(d) && d >= 1)) {
        problems.push(`${where}: every dimension must be an integer of at least 1`);
        cells = 0;
        break;
      }

      cells = Math.min(cells * d, ProtocolConstants.maxGridCells + 1);
    }

    if (cells > ProtocolConstants.maxGridCells) {
      problems.push(`${where}: more than ${ProtocolConstants.maxGridCells} cells`);
    }

    const seen = new Set<number>();
    for (const idx of g.archetypes) {
      if (!Number.isInteger(idx) || idx < 0 || idx >= archetypeCount || seen.has(idx)) {
        problems.push(`${where}: archetype index ${idx} does not exist or is listed twice`);
      }

      seen.add(idx);
    }
  });
}

/** Bits of a codec's parameters, to refuse one its kind does not read. */
const Parameter = {
  Bits: 1,
  Bounds: 2,
  Scale: 4,
  QuantaDiv: 8,
  N: 16,
  MaxBytes: 32,
  List: 64,
  FixedBytes: 128,
} as const;

function readParameters(kind: CodecKind): number {
  switch (kind) {
    case CodecKind.Quant:
    case CodecKind.Pos2:
    case CodecKind.Pos3:
      return Parameter.Bits | Parameter.Bounds;
    case CodecKind.Vec2:
    case CodecKind.Vec3:
      return Parameter.Bits | Parameter.Scale;
    case CodecKind.Vel2:
    case CodecKind.Vel3:
      return Parameter.Bits | Parameter.QuantaDiv;
    case CodecKind.Unorm:
    case CodecKind.Snorm:
    case CodecKind.Angle:
      return Parameter.Bits;
    case CodecKind.Bits:
    case CodecKind.Bytes:
      return Parameter.N;
    case CodecKind.Str:
    case CodecKind.Blob:
      return Parameter.MaxBytes;
    case CodecKind.List:
      return Parameter.List;
    default:
      return 0;
  }
}

function checkCodec(at: string, codec: CatalogCodec, maxBytes: number, problems: string[]): void {
  const kind = codecKindOf(codec.t);
  if (kind !== CodecKind.Unknown) {
    // A parameter the kind does not read would be ignored here and honoured by another decoder: every decoder must read
    // the same width, so it is refused.
    const present =
      (nonZero(codec.bits) ? Parameter.Bits : 0) |
      (codec.min !== undefined || codec.max !== undefined ? Parameter.Bounds : 0) |
      (nonZero(codec.scale) ? Parameter.Scale : 0) |
      (nonZero(codec.quantaDiv) ? Parameter.QuantaDiv : 0) |
      (nonZero(codec.n) ? Parameter.N : 0) |
      (nonZero(codec.maxBytes) ? Parameter.MaxBytes : 0) |
      (codec.of !== undefined || nonZero(codec.minCount) || nonZero(codec.maxCount) ? Parameter.List : 0) |
      (nonZero(codec.fixedBytes) ? Parameter.FixedBytes : 0);
    if ((present & ~readParameters(kind)) !== 0) {
      problems.push(`${at}: codec '${codec.t}' carries a parameter its kind does not read`);
    }
  }

  switch (kind) {
    case CodecKind.Unknown: {
      const fixed = codec.fixedBytes ?? 0;
      if (codec.t === '' || !(Number.isSafeInteger(fixed) && fixed > 0 && fixed <= maxBytes)) {
        problems.push(
          `${at}: codec '${codec.t}' is unknown and declares no usable fixedBytes, so it cannot be skipped`,
        );
      }

      break;
    }
    case CodecKind.Quant:
      checkBits(at, codec.bits, problems);
      checkBounds(at, codec, 1, problems);
      break;
    case CodecKind.Pos2:
      checkBits(at, codec.bits, problems);
      checkBounds(at, codec, 2, problems);
      break;
    case CodecKind.Pos3:
      checkBits(at, codec.bits, problems);
      checkBounds(at, codec, 3, problems);
      break;
    case CodecKind.Vec2:
    case CodecKind.Vec3:
      checkBits(at, codec.bits, problems);
      if (!(codec.scale !== undefined && codec.scale > 0 && Number.isFinite(codec.scale))) {
        problems.push(`${at}: scale must be positive and finite`);
      }

      break;
    case CodecKind.Vel2:
    case CodecKind.Vel3:
      checkBits(at, codec.bits, problems);
      if (!(codec.quantaDiv !== undefined && Number.isSafeInteger(codec.quantaDiv) && codec.quantaDiv >= 1)) {
        problems.push(`${at}: quantaDiv must be an integer ≥ 1`);
      }

      break;
    case CodecKind.Unorm:
    case CodecKind.Snorm:
    case CodecKind.Angle:
      checkBits(at, codec.bits, problems);
      break;
    case CodecKind.Bits:
      if (!(
        codec.n !== undefined &&
        Number.isInteger(codec.n) &&
        codec.n >= 1 &&
        codec.n <= ProtocolConstants.maxPackedBits
      )) {
        problems.push(`${at}: bits.n must be in [1, ${ProtocolConstants.maxPackedBits}]`);
      }

      break;
    case CodecKind.Str:
    case CodecKind.Blob:
      if (!(
        codec.maxBytes !== undefined &&
        Number.isSafeInteger(codec.maxBytes) &&
        codec.maxBytes > 0 &&
        codec.maxBytes <= maxBytes
      )) {
        problems.push(`${at}: maxBytes must be positive and within limits.frameBytes`);
      }

      break;
    case CodecKind.Bytes:
      if (!(codec.n !== undefined && Number.isSafeInteger(codec.n) && codec.n > 0 && codec.n <= maxBytes)) {
        problems.push(`${at}: bytes.n must be positive and within limits.frameBytes`);
      }

      break;
    case CodecKind.List: {
      if (codec.of === undefined) {
        problems.push(`${at}: a list needs an element codec`);
        break;
      }

      if (!isListElement(codecKindOf(codec.of.t))) {
        problems.push(`${at}: a list element must be a numeric byte-aligned codec, not '${codec.of.t}'`);
      } else {
        checkCodec(`${at} element`, codec.of, maxBytes, problems);
      }

      const min = codec.minCount ?? 0;
      const max = codec.maxCount ?? 0;
      if (
        !Number.isInteger(min) ||
        !Number.isInteger(max) ||
        min < 0 ||
        min > max ||
        max > ProtocolConstants.maxListCount
      ) {
        problems.push(`${at}: list counts must satisfy 0 ≤ minCount ≤ maxCount ≤ ${ProtocolConstants.maxListCount}`);
      }

      break;
    }
    default:
      break;
  }
}

function nonZero(value: number | undefined): boolean {
  return value !== undefined && value !== 0;
}

function checkBits(at: string, bits: number | undefined, problems: string[]): void {
  if (bits !== 8 && bits !== 16 && bits !== 24 && bits !== 32) {
    problems.push(`${at}: bits must be 8, 16, 24 or 32`);
  }
}

function checkBounds(at: string, codec: CatalogCodec, axes: number, problems: string[]): void {
  const min = codec.min;
  const max = codec.max;
  if (min?.length !== axes || max?.length !== axes) {
    problems.push(`${at}: min and max need ${axes} value(s) each`);
    return;
  }

  const bits = codec.bits;
  if (bits !== 8 && bits !== 16 && bits !== 24 && bits !== 32) {
    return;
  }

  for (let i = 0; i < axes; i++) {
    const lo = min[i]!;
    const hi = max[i]!;
    if (!(Number.isFinite(lo) && Number.isFinite(hi) && hi > lo && Number.isFinite(hi - lo))) {
      problems.push(`${at}: axis ${i} needs finite bounds with max > min and a finite range`);
      continue;
    }

    // Below this step, min absorbs q × step and decoding stops round-tripping (W2).
    const magnitude = Math.max(Math.abs(lo), Math.abs(hi));
    if (quantStep(lo, hi, bits) < pow2(8) * ulp(magnitude)) {
      problems.push(`${at}: axis ${i} step is below 2⁸ ulps of its bounds, so quantization would not round-trip`);
    }
  }
}

const ulpBits = new DataView(new ArrayBuffer(8));

/** The gap from a finite, non-negative double to the next one up: C#'s `Math.BitIncrement(x) − x`. */
function ulp(x: number): number {
  ulpBits.setFloat64(0, x);
  const low = ulpBits.getUint32(4);
  if (low === 0xffffffff) {
    ulpBits.setUint32(4, 0);
    ulpBits.setUint32(0, ulpBits.getUint32(0) + 1);
  } else {
    ulpBits.setUint32(4, low + 1);
  }

  return ulpBits.getFloat64(0) - x;
}

function checkStrings(where: string, values: readonly string[], maxUtf8Bytes: number, problems: string[]): void {
  const seen = new Set<string>();
  for (const v of values) {
    if (v === '' || encodeUtf8(v).length > maxUtf8Bytes || seen.has(v)) {
      problems.push(`${where}: '${v}' is empty, longer than ${maxUtf8Bytes} UTF-8 bytes, or listed twice`);
    }

    seen.add(v);
  }
}

function uniqueNames(items: readonly { readonly name: string }[], what: string, problems: string[]): void {
  const seen = new Set<string>();
  for (const item of items) {
    if (item.name === '') {
      problems.push(`a ${what} needs a name`);
    } else if (seen.has(item.name)) {
      problems.push(`${what} '${item.name}' is declared twice`);
    }

    seen.add(item.name);
  }
}

function uniqueIndices(
  items: readonly { readonly idx: number; readonly name: string }[],
  what: string,
  problems: string[],
): void {
  const seen = new Set<number>();
  for (const item of items) {
    if (item.idx < 0 || item.idx > ProtocolConstants.maxMessageIndex) {
      problems.push(`${what} '${item.name}' has index ${item.idx}, outside 0..${ProtocolConstants.maxMessageIndex}`);
    } else if (seen.has(item.idx)) {
      problems.push(`${what} index ${item.idx} is used twice`);
    }

    seen.add(item.idx);
  }
}
