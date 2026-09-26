import {
  ValueKind,
  type ArchetypePlan,
  type CatalogPlan,
  type FieldPlan,
  type SectionPlan,
} from '../protocol/catalog.js';
import { CodecKind } from '../protocol/codec-kinds.js';

/*
 * typhon-codegen (05-sdks § 2): per archetype, a decoder of its `ENTITIES` block specialized to the catalog — every codec
 * parameter a literal, every field read by the reader method its width needs and stored straight into its column — so a
 * frame is decoded with no interpreter dispatch: no per-field `switch`, no sink call per value, no per-column-type store.
 *
 * The generated code keeps the interpreter's rules to the letter, because the two paths must leave the same store for the
 * same bytes and refuse the same bytes with the same error:
 * - **No wide number crosses a call.** A 32-bit or floating read goes through the reader's `…Into` form into a scratch
 *   `Float64Array`, exactly where `readNumber` does; a value of 24 bits or fewer is a small integer and returns directly.
 * - **The same arithmetic, in the same order.** Each codec's decode is `readNumber`'s, written out with its parameters as
 *   literals. JavaScript prints a double in its shortest round-tripping form, so a literal is the parameter's exact bits.
 * - **The same record semantics.** Enters, segments, state records and leaves go through the target's methods, which are
 *   the interpreter's; a record the target refuses (an anomaly) still has its fields read, and discarded.
 * - **The same framing checks**, through the helpers `TickReader` itself calls.
 *
 * Only `ENTITIES` is generated: it is where the records are. Every other block stays with the interpreter.
 */

/** `typhon-codegen`'s usage line. */
export const CODEGEN_USAGE = 'usage: typhon-codegen <catalog.json> [-o <dir>] [--import <module>]';

/** The command line, parsed: the catalog to read, the directory `catalog.gen.ts` goes to, and the module it imports. */
export interface CodegenArguments {
  readonly catalog: string;
  readonly outDir: string;
  readonly importFrom: string;
}

/**
 * Parses `typhon-codegen`'s arguments (05-sdks § 2): `typhon-codegen catalog.json -o src/gen`. Kept apart from the
 * file I/O so it is testable in the browser-safe build; the `bin` script does the reading and writing.
 */
export function parseCodegenArgs(argv: readonly string[]): CodegenArguments {
  let catalog: string | null = null;
  let outDir = 'src/gen';
  let importFrom = '@typhondb/client';
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]!;
    if (arg === '-o' || arg === '--out' || arg === '--import') {
      const value = argv[++i];
      if (value === undefined || value.startsWith('-')) {
        throw new Error(`${arg} needs a value. ${CODEGEN_USAGE}`);
      }

      if (arg === '--import') {
        importFrom = value;
      } else {
        outDir = value;
      }
    } else if (arg.startsWith('-')) {
      throw new Error(`unknown option '${arg}'. ${CODEGEN_USAGE}`);
    } else if (catalog === null) {
      catalog = arg;
    } else {
      throw new Error(`one catalog only, got '${catalog}' and '${arg}'. ${CODEGEN_USAGE}`);
    }
  }

  if (catalog === null) {
    throw new Error(`no catalog given. ${CODEGEN_USAGE}`);
  }

  return { catalog, outDir, importFrom };
}

/** How the generated module is written. */
export interface GenerateOptions {
  /** The module the generated code imports the client from. Default `@typhondb/client`. */
  readonly importFrom?: string;
}

/**
 * The catalog's hash — FNV-1a 64 of its canonical bytes (03 § 4, W20) — as 16 lower-case hex digits, most significant
 * first: the form a session's `catalogHash` displays in. The generator computes it once; a client never does.
 */
export function catalogHashOf(canonicalBytes: Uint8Array): string {
  let hash = 0xcbf29ce484222325n;
  for (const b of canonicalBytes) {
    hash = ((hash ^ BigInt(b)) * 0x100000001b3n) & 0xffffffffffffffffn;
  }

  return hash.toString(16).padStart(16, '0');
}

/**
 * The generated module's source: one decoder per archetype and a `decoders` export to hand to `FrameApplier` with the
 * session's catalog hash. `canonicalBytes` are the catalog as the server serves it (`GET /typhon/catalog.json`, or
 * `dotnet typhon catalog export`), the bytes `plan` was compiled from.
 */
export function generateDecoders(plan: CatalogPlan, canonicalBytes: Uint8Array, options: GenerateOptions = {}): string {
  const hash = catalogHashOf(canonicalBytes);
  const gen = new Generator();
  const bodies: string[] = [];
  const names: string[] = [];
  for (const archetype of plan.archetypes) {
    const decodable = archetype.fields.every((f) => f.valueKind !== ValueKind.List);
    if (!decodable) {
      names.push('null');
      continue;
    }

    const name = `decode${archetype.idx}_${archetype.name.replace(/[^A-Za-z0-9_]/g, '_')}`;
    names.push(name);
    bodies.push(gen.archetype(archetype, name));
  }

  const imports = [...gen.values].sort();
  const types = [
    'EntitiesTarget',
    'GeneratedDecoders',
    'WireReader',
    ...(gen.usesColumns ? ['FieldArray'] : []),
    ...(gen.usesRealm ? ['RealmFrame'] : []),
  ].sort();
  const lines = [
    `// Generated by typhon-codegen from catalog ${hash}. Do not edit: regenerate it when the catalog changes.`,
    '// Exclude it from formatting (a formatter would only rewrite what the next generation writes again).',
    '/* eslint-disable */',
    `import {`,
    ...imports.map((i) => `  ${i},`),
    ...types.map((t) => `  type ${t},`),
    `} from '${options.importFrom ?? '@typhondb/client'}';`,
    '',
    '// Scratch shared by every decoder: a value is stored before the next is read. Not re-entrant, as the interpreter.',
    ...gen.scratchDeclarations(),
    '',
    ...bodies,
    `/** The generated decoders, for \`FrameApplier\`'s \`decoders\` option. */`,
    'export const decoders: GeneratedDecoders = {',
    `  catalogHash: '${hash}',`,
    `  entities: [${names.join(', ')}],`,
    '};',
    '',
  ];
  return lines.join('\n');
}

/** A number as a TypeScript literal of the same double, parenthesized when negative. */
function lit(x: number): string {
  if (Object.is(x, -0)) {
    return '(-0)';
  }

  if (!Number.isFinite(x)) {
    return Number.isNaN(x) ? 'NaN' : x > 0 ? 'Infinity' : '(-Infinity)';
  }

  const s = String(x);
  return x < 0 ? `(${s})` : s;
}

class Generator {
  /** Value imports the module needs. */
  readonly values = new Set<string>(['nextNetId', 'runLength']);
  /** The scratch arrays the decoders use: `T`, `T0`, `NONE`, and position and velocity views `P2`, `P3`, `V2`, `V3`. */
  private readonly scratch = new Set<string>(['T0']);
  usesColumns = false;
  /** Whether a decoder reads a position, and so the realm frame it decodes over (`typhon.3`). */
  usesRealm = false;
  private temp = 0;

  archetype(a: ArchetypePlan, name: string): string {
    const out: string[] = [];
    const emit = (depth: number, line: string): void => {
      out.push('  '.repeat(depth) + line);
    };

    const position = a.position;
    const dims = position?.dims ?? 0;
    const p = this.view('P', dims);
    const v = this.view('V', position?.vel != null ? dims : 0);

    // The store's columns, re-read when it grows: numeric fields by typed array, text and bytes by their slot arrays.
    const stored = a.fields.filter((f) => f.valueKind !== ValueKind.Skipped);
    emit(0, `/** \`${a.name}\` (archetype ${a.idx}): its \`ENTITIES\` records, after the archetype index. */`);
    // A parameter nothing reads is named with a leading underscore: `noUnusedParameters` then accepts it.
    const usesTick = position?.moving === true || a.fields.some((f) => f.kind === CodecKind.TickLo);
    const writes = (sections: readonly SectionPlan[]): boolean =>
      sections.some((section) => section.fields.some((f) => f.valueKind !== ValueKind.Skipped));
    // A position decodes over the session's realm frame (typhon.3, SUB-30): its width and bounds are read once per block,
    // never baked in, so one module serves every realm and every width.
    // A pos-coded field in a section decodes over the same frame, even on an archetype with no position of its own.
    const isPos = (f: { kind: CodecKind }): boolean => f.kind === CodecKind.Pos2 || f.kind === CodecKind.Pos3;
    const positioned =
      position !== null || [a.onEnter, ...a.groupSections].some((section) => section.fields.some(isPos));
    this.usesRealm ||= positioned;
    emit(
      0,
      `function ${name}(r: WireReader, ${usesTick ? 'tick' : '_tick'}: number, x: EntitiesTarget, ` +
        `${positioned ? 'frame: RealmFrame' : '_frame: unknown'}): void {`,
    );
    if (positioned) {
      emit(1, 'const fb = frame.positionBits;');
      emit(1, 'const fm = frame.min;');
      emit(1, 'const fs = frame.step;');
    }

    if (stored.length > 0) {
      emit(1, `const st = x.archetypeStore(${a.idx});`);
    }

    for (const f of stored) {
      emit(1, `const i${f.index} = st.fieldIndex(${JSON.stringify(f.name)});`);
    }

    const load = (depth: number, declare: boolean): void => {
      for (const f of stored) {
        const kw = declare ? 'let ' : '';
        if (f.valueKind === ValueKind.Number) {
          this.usesColumns = true;
          emit(depth, `${kw}c${f.index}${declare ? ': FieldArray' : ''} = st.columns[i${f.index}]!;`);
        } else if (f.valueKind === ValueKind.Text) {
          emit(depth, `${kw}c${f.index} = st.textAt(i${f.index});`);
        } else if (f.valueKind === ValueKind.Bytes) {
          emit(depth, `${kw}c${f.index} = st.bytesAt(i${f.index});`);
        }
      }
    };

    if (stored.length > 0) {
      emit(1, 'let ver = st.version;');
      load(1, true);
    }

    // ── enters ──
    const enterSlot = writes([a.onEnter, ...a.groupSections]) ? 'const slot = ' : '';
    emit(1, 'for (let runs = r.varu(); runs > 0; runs--) {');
    emit(2, 'let prev = -1;');
    emit(2, 'for (let n = runLength(r); n > 0; n--) {');
    emit(3, 'prev = nextNetId(r, prev);');
    if (position !== null) {
      this.number(position.pos, (i, e) => `${p}[${i}] = ${e};`, 3, emit);
      if (position.moving) {
        if (position.vel !== null) {
          this.number(position.vel, (i, e) => `${v}[${i}] = ${e};`, 3, emit);
        }

        emit(3, 'const low = r.u16();');
        emit(3, 'T0[0] = tick - ((tick - low) & 0xffff);');
        emit(3, `${enterSlot}x.enterSlot(prev, ${p}, ${v}, T0, r.u8());`);
      } else {
        emit(3, 'T0[0] = 0;');
        emit(3, `${enterSlot}x.enterSlot(prev, ${p}, ${v}, T0, 0);`);
      }
    } else {
      emit(3, 'T0[0] = 0;');
      emit(3, `${enterSlot}x.enterSlot(prev, ${p}, ${v}, T0, 0);`);
    }

    if (stored.length > 0) {
      emit(3, 'if (st.version !== ver) {');
      emit(4, 'ver = st.version;');
      load(4, false);
      emit(3, '}');
    }

    this.section(a.onEnter, 3, emit);
    for (const s of a.groupSections) {
      this.section(s, 3, emit);
    }

    emit(2, '}');
    emit(1, '}');
    emit(0, '');

    // ── segments ──
    emit(1, 'const segmentRuns = r.varu();');
    if (position === null || !position.moving) {
      this.values.add('segmentsOfAStill');
      emit(1, 'if (segmentRuns > 0) {');
      emit(2, `throw segmentsOfAStill(${JSON.stringify(a.name)});`);
      emit(1, '}');
    } else {
      emit(1, 'for (let runs = segmentRuns; runs > 0; runs--) {');
      emit(2, 'let prev = -1;');
      emit(2, 'for (let n = runLength(r); n > 0; n--) {');
      emit(3, 'prev = nextNetId(r, prev);');
      this.number(position.pos, (i, e) => `${p}[${i}] = ${e};`, 3, emit);
      if (position.vel !== null) {
        this.number(position.vel, (i, e) => `${v}[${i}] = ${e};`, 3, emit);
      }

      emit(3, 'const low = r.u16();');
      emit(3, 'T0[0] = tick - ((tick - low) & 0xffff);');
      emit(3, `x.segmentAt(prev, ${p}, ${v}, T0, r.u8());`);
      emit(2, '}');
      emit(1, '}');
    }

    emit(0, '');

    // ── state records ──
    const groups = a.groups.length;
    this.values.add('invalidStateMask');
    emit(1, 'for (let runs = r.varu(); runs > 0; runs--) {');
    emit(2, 'let prev = -1;');
    emit(2, 'for (let n = runLength(r); n > 0; n--) {');
    emit(3, 'prev = nextNetId(r, prev);');
    emit(3, 'const mask = r.u8();');
    emit(3, `if (mask === 0 || mask >> ${groups} !== 0) {`);
    emit(4, `throw invalidStateMask(mask, ${groups});`);
    emit(3, '}');
    emit(3, `${writes(a.groupSections) ? 'const slot = ' : ''}x.stateSlot(prev, mask);`);
    a.groupSections.forEach((s, g) => {
      if (s.fields.length === 0) {
        return;
      }

      emit(3, `if ((mask & ${1 << g}) !== 0) {`);
      this.section(s, 4, emit);
      emit(3, '}');
    });
    emit(2, '}');
    emit(1, '}');
    emit(0, '');

    // ── leaves ──
    emit(1, 'for (let runs = r.varu(); runs > 0; runs--) {');
    emit(2, 'let prev = -1;');
    emit(2, 'for (let n = runLength(r); n > 0; n--) {');
    emit(3, 'prev = nextNetId(r, prev);');
    emit(3, 'x.leave(prev);');
    emit(2, '}');
    emit(1, '}');
    emit(0, '}');
    emit(0, '');
    return out.join('\n');
  }

  private view(kind: 'P' | 'V', dims: number): string {
    const name = dims === 0 ? 'NONE' : `${kind}${dims}`;
    this.scratch.add(name);
    return name;
  }

  /** The module-level scratch arrays, only those a decoder uses: an unused one fails `noUnusedLocals`. */
  scratchDeclarations(): string[] {
    const s = this.scratch;
    const lines: string[] = [];
    if (s.has('T')) {
      lines.push('const T = new Float64Array(4);');
    }

    lines.push('const T0 = new Uint32Array(1);');
    if (s.has('NONE')) {
      lines.push('const NONE = new Float64Array(0);');
    }

    for (const kind of ['P', 'V']) {
      if (s.has(`${kind}2`) || s.has(`${kind}3`)) {
        lines.push(`const ${kind} = new Float64Array(3);`);
        for (const dims of [2, 3]) {
          if (s.has(`${kind}${dims}`)) {
            lines.push(`const ${kind}${dims} = ${kind}.subarray(0, ${dims});`);
          }
        }
      }
    }

    return lines;
  }

  /** A section's fields, read in wire order and stored into `slot` when it is not −1. */
  private section(s: SectionPlan, depth: number, emit: (depth: number, line: string) => void): void {
    if (s.packBytes > 0) {
      const at = this.name('at');
      const bytes = this.name('b');
      emit(depth, `const ${at} = r.take(${s.packBytes});`);
      emit(depth, `const ${bytes} = r.bytes;`);
      for (let i = 0; i < s.packedCount; i++) {
        const f = s.fields[i]!;
        const first = f.bitOffset >> 3;
        const parts: string[] = [];
        for (let k = 0; k < 4 && first + k < s.packBytes; k++) {
          const byte = `${bytes}[${at} + ${first + k}]!`;
          parts.push(k === 0 ? byte : `(${byte} << ${8 * k})`);
        }

        const mask = (1 << f.bitCount) - 1;
        emit(depth, `if (slot >= 0) {`);
        emit(
          depth + 1,
          `c${f.index}[slot${f.components > 1 ? ` * ${f.components}` : ''}] = ((${parts.join(' | ')}) >>> ${f.bitOffset & 7}) & ${mask};`,
        );
        emit(depth, '}');
      }
    }

    for (let i = s.packedCount; i < s.fields.length; i++) {
      const f = s.fields[i]!;
      switch (f.valueKind) {
        case ValueKind.Number: {
          const values: string[] = [];
          this.number(
            f,
            (_component, e) => {
              const v = this.name('v');
              values.push(v);
              return `const ${v} = ${e};`;
            },
            depth,
            emit,
          );
          emit(depth, 'if (slot >= 0) {');
          values.forEach((v, c) => {
            const index = f.components > 1 ? `slot * ${f.components} + ${c}` : 'slot';
            emit(depth + 1, `c${f.index}[${index}] = ${v};`);
          });
          emit(depth, '}');
          break;
        }
        case ValueKind.Text: {
          const t = this.name('text');
          emit(depth, `const ${t} = r.str(${f.maxBytes});`);
          emit(depth, 'if (slot >= 0) {');
          emit(depth + 1, `c${f.index}[slot] = ${t};`);
          emit(depth, '}');
          break;
        }
        case ValueKind.Bytes: {
          this.values.add('retainBytes');
          const length = this.name('length');
          const at = this.name('at');
          emit(depth, `const ${length} = ${f.kind === CodecKind.Bytes ? String(f.n) : `r.blobLength(${f.maxBytes})`};`);
          emit(depth, `const ${at} = r.take(${length});`);
          emit(depth, 'if (slot >= 0) {');
          emit(depth + 1, `c${f.index}[slot] = retainBytes(r.bytes, ${at}, ${length}, c${f.index}[slot]);`);
          emit(depth, '}');
          break;
        }
        default:
          emit(depth, `r.skip(${f.fixedBytes});`);
          break;
      }
    }
  }

  /**
   * One numeric value, component by component: each read and dequantized as `readNumber` does, then handed to `store`
   * as an expression, which returns the statement that consumes it. A wide read goes through the scratch `T`.
   */
  private number(
    f: FieldPlan,
    store: (component: number, expr: string) => string,
    depth: number,
    emit: (depth: number, line: string) => void,
  ): void {
    const unsigned = (bits: number, slot: number): string => {
      switch (bits) {
        case 8:
          return 'r.u8()';
        case 16:
          return 'r.u16()';
        case 24:
          return 'r.u24()';
        default:
          this.scratch.add('T');
          emit(depth, `r.u32Into(T, ${slot});`);
          return `T[${slot}]!`;
      }
    };
    const signed = (bits: number, slot: number): string => {
      switch (bits) {
        case 8:
          return 'r.i8()';
        case 16:
          return 'r.i16()';
        case 24:
          return 'r.i24()';
        default:
          this.scratch.add('T');
          emit(depth, `r.i32Into(T, ${slot});`);
          return `T[${slot}]!`;
      }
    };
    const into = (method: string): string => {
      this.scratch.add('T');
      emit(depth, `r.${method}(T, 0);`);
      return 'T[0]!';
    };
    const q = (expr: string): string => {
      const name = this.name('q');
      emit(depth, `const ${name} = ${expr};`);
      return name;
    };

    switch (f.kind) {
      case CodecKind.U8:
        emit(depth, store(0, 'r.u8()'));
        break;
      case CodecKind.I8:
        emit(depth, store(0, 'r.i8()'));
        break;
      case CodecKind.U16:
        emit(depth, store(0, 'r.u16()'));
        break;
      case CodecKind.I16:
        emit(depth, store(0, 'r.i16()'));
        break;
      case CodecKind.U32:
        emit(depth, store(0, into('u32Into')));
        break;
      case CodecKind.I32:
        emit(depth, store(0, into('i32Into')));
        break;
      case CodecKind.Varu:
      case CodecKind.EntityRef:
        emit(depth, store(0, into('varuInto')));
        break;
      case CodecKind.Vari:
        emit(depth, store(0, into('variInto')));
        break;
      case CodecKind.F32:
        emit(depth, store(0, into('f32Into')));
        break;
      case CodecKind.F16:
        emit(depth, store(0, into('f16Into')));
        break;
      case CodecKind.Quant:
        for (let i = 0; i < f.components; i++) {
          const code = q(unsigned(f.bits, 0));
          emit(depth, store(i, `${lit(f.min[i]!)} + ${code} * ${lit(f.step[i]!)}`));
        }

        break;
      case CodecKind.Pos2:
      case CodecKind.Pos3:
        // The frame's width, read into the scratch so a 32-bit code is never boxed; decodeQuant: min + q × step.
        this.scratch.add('T');
        for (let i = 0; i < f.components; i++) {
          emit(depth, 'r.unsignedInto(fb, T, 0);');
          emit(depth, store(i, `fm[${i}]! + T[0]! * fs[${i}]!`));
        }

        break;
      case CodecKind.Vec2:
      case CodecKind.Vec3:
        for (let i = 0; i < f.components; i++) {
          const code = q(signed(f.bits, 0));
          emit(depth, store(i, `(${code} < ${-f.limit} ? ${-f.limit} : ${code}) * ${lit(f.scale)}`));
        }

        break;
      case CodecKind.Vel2:
      case CodecKind.Vel3:
        for (let i = 0; i < f.components; i++) {
          const code = q(signed(f.bits, 0));
          emit(depth, store(i, `(${code} < ${-f.limit} ? ${-f.limit} : ${code}) * ${lit(f.velocityUnit)}`));
        }

        break;
      case CodecKind.Unorm:
        emit(depth, store(0, `${q(unsigned(f.bits, 0))} / ${lit(f.top)}`));
        break;
      case CodecKind.Snorm: {
        const x = this.name('x');
        emit(depth, `const ${x} = ${q(signed(f.bits, 0))} / ${lit(f.limit)};`);
        emit(depth, store(0, `${x} < -1 ? -1 : ${x}`));
        break;
      }
      case CodecKind.Angle:
        this.values.add('TAU');
        emit(depth, store(0, `(${q(signed(f.bits, 0))} * TAU) / ${lit(f.top + 1)}`));
        break;
      case CodecKind.Quat3: {
        this.values.add('decodeQuat3Halves');
        this.scratch.add('T');
        const low = this.name('low');
        emit(depth, `const ${low} = r.u16();`);
        emit(depth, `decodeQuat3Halves(${low}, r.u16(), T, 0);`);
        for (let i = 0; i < 4; i++) {
          emit(depth, store(i, `T[${i}]!`));
        }

        break;
      }
      case CodecKind.TickLo: {
        const low = this.name('low');
        this.scratch.add('T');
        emit(depth, `const ${low} = r.u16();`);
        emit(depth, `T[0] = (tick - ((tick - ${low}) & 0xffff)) >>> 0;`);
        emit(depth, store(0, 'T[0]!'));
        break;
      }
      default:
        throw new Error(`'${f.codec.t}' is not a byte-aligned numeric codec`);
    }
  }

  private name(prefix: string): string {
    return `${prefix}${this.temp++}`;
  }
}
