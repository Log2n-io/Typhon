import { describe, expect, it } from 'vitest';
import {
  CatalogPlan,
  DebugGrid,
  DebugSubType,
  parseCatalog,
  PushGeometry,
  PushShape,
  TickReader,
  WireFormatError,
  WireReader,
} from '../src/index.js';
import {
  bits,
  bitsOf,
  frameFromJson,
  goldenBin,
  goldenJson,
  goldenNames,
  RecordingSink,
  type FrameJson,
  type LogEntry,
} from './golden-support.js';

/*
 * debug-*: the DEBUG sub-blocks decode to exactly what the C# writer put in them.
 *
 * These vectors carry a `decoded` block the other suites do not: a DEBUG payload is skippable by length, so the call log
 * alone would pass with a decoder that never looked inside it. The comparison here is against the C# `DebugGrid.Read`
 * and `PushGeometry.Read` results, field for field and bit for bit.
 */

interface GridJson {
  readonly originX: string;
  readonly originY: string;
  readonly originZ: string;
  readonly cellM: string;
  readonly dimX: number;
  readonly dimY: number;
  readonly dimZ: number;
}

interface GeometryJson {
  readonly shape: string;
  readonly flags: number;
  readonly cursor?: string;
  readonly anchor?: readonly string[];
  readonly radiusM?: string;
  readonly slackM?: string;
  readonly level?: number;
  readonly dims?: number;
  readonly vertices?: readonly string[];
  readonly held?: number;
  readonly nearBudget?: number;
  readonly window?: readonly number[];
  readonly rows?: readonly string[];
}

interface DebugVector {
  readonly catalog: string;
  readonly frame?: FrameJson;
  readonly log: readonly LogEntry[];
  readonly decoded: GridJson & GeometryJson;
}

/** The payload of the vector's one `debug` call, as the decoder saw it. */
/** The vector holding two sub-blocks, the first unknown: the only one `payloadOf`'s "first debug entry" rule does not suit. */
const UNKNOWN_THEN_GRID = 'debug-unknown-then-grid';

function payloadOf(log: readonly LogEntry[], which = 0): { subType: number; bytes: Uint8Array } {
  const entry = log.filter((e) => e.call === 'debug')[which] as { subType: number; payload: string } | undefined;
  if (entry === undefined) {
    throw new Error('the vector holds no debug sub-block');
  }

  const bytes = new Uint8Array(entry.payload.length / 2);
  for (let i = 0; i < bytes.length; i++) {
    bytes[i] = parseInt(entry.payload.slice(i * 2, i * 2 + 2), 16);
  }

  return { subType: entry.subType, bytes };
}

/** A window row as the C# expectation writes it: the little-endian bytes as one hex number, no padding. */
function rowHex(geometry: PushGeometry, row: number): string {
  let bits = 0n;
  for (let b = geometry.rowBytes - 1; b >= 0; b--) {
    bits = (bits << 8n) | BigInt(geometry.rows[row * geometry.rowBytes + b]!);
  }

  return bits.toString(16);
}

describe('golden debug sub-blocks', () => {
  const names = goldenNames('debug-');

  it('finds the debug vectors', () => {
    expect(names).toEqual([
      'debug-grid',
      'debug-push-geometry-region',
      'debug-push-geometry-sphere',
      'debug-push-geometry-world',
      'debug-unknown-then-grid',
    ]);
  });

  for (const name of names) {
    it(`${name} — the call log`, () => {
      const vector = goldenJson(name) as DebugVector;
      const reader = new TickReader(CatalogPlan.compile(parseCatalog(goldenBin(vector.catalog))));
      reader.realm = frameFromJson(vector.frame);
      const sink = new RecordingSink();
      reader.read(goldenBin(name), sink);
      expect(sink.log).toEqual(vector.log);
    });

    it(`${name} — the payload decodes to the C# reader's result`, () => {
      if (name === UNKNOWN_THEN_GRID) {
        // Two sub-blocks, the first of a sub-type nothing decodes. Its own case is below; `payloadOf` takes the first.
        return;
      }

      const vector = goldenJson(name) as DebugVector;
      const { subType, bytes } = payloadOf(vector.log);
      const r = new WireReader(bytes);
      if (subType === DebugSubType.Grid) {
        const grid = new DebugGrid().readFrom(r);
        expect({
          originX: bits(grid.origin[0]!),
          originY: bits(grid.origin[1]!),
          originZ: bits(grid.origin[2]!),
          cellM: bits(grid.cellM),
          dimX: grid.dims[0],
          dimY: grid.dims[1],
          dimZ: grid.dims[2],
        }).toEqual({
          originX: vector.decoded.originX,
          originY: vector.decoded.originY,
          originZ: vector.decoded.originZ,
          cellM: vector.decoded.cellM,
          dimX: vector.decoded.dimX,
          dimY: vector.decoded.dimY,
          dimZ: vector.decoded.dimZ,
        });
        return;
      }

      expect(subType).toBe(DebugSubType.PushGeometry);
      const g = new PushGeometry().readFrom(r);
      const d = vector.decoded;
      expect(PushShape[d.shape as keyof typeof PushShape]).toBe(g.shape);
      expect(g.flags).toBe(d.flags);
      if (g.shape === PushShape.World) {
        // The C# vector writes the cursor as one 16-digit hex number; the SDK keeps it as two halves, to stay off BigInt.
        const cursor = (BigInt(g.cursorHi) << 32n) | BigInt(g.cursorLo);
        expect(cursor.toString(16).padStart(16, '0')).toBe(d.cursor);
        expect(g.window).toBe(0);
        return;
      }

      if (g.shape === PushShape.Sphere) {
        expect(bitsOf(g.anchor, 3)).toEqual(d.anchor);
        expect(bits(g.radiusM)).toBe(d.radiusM);
        expect(bits(g.slackM)).toBe(d.slackM);
        expect(g.level).toBe(d.level);
      } else {
        expect(g.dims).toBe(d.dims);
        expect(bitsOf(g.vertices, g.vertexCount * g.dims)).toEqual(d.vertices);
        expect(g.held).toBe(d.held);
        expect(g.nearBudget).toBe(d.nearBudget);
      }

      expect([g.windowOrigin[0], g.windowOrigin[1], g.windowOrigin[2], g.window]).toEqual(d.window);
      expect(Array.from({ length: g.rowCount }, (_, i) => rowHex(g, i))).toEqual(d.rows);
    });
  }

  /*
   * delivered() is checked against EVERY cell of the window, not against a handful of hand-picked ones.
   *
   * The handful was written first and a mutation walked straight through it: `lx >> 3` for `lx >> 2` picks the wrong
   * byte of a two-byte row, and each of the four cells named in the C# fixture happened to land on a byte holding the
   * same bit. The expectation below is rebuilt from the vector's own `rows` hex — the C# writer's numbers, not this
   * decoder's buffer — so a wrong index, a wrong row stride or a wrong bit within the byte all show.
   */
  for (const name of ['debug-push-geometry-sphere', 'debug-push-geometry-region']) {
    it(`${name} — delivered() agrees with the vector's rows on every cell of the window`, () => {
      const vector = goldenJson(name) as DebugVector;
      const g = new PushGeometry().readFrom(new WireReader(payloadOf(vector.log).bytes));
      const [ox, oy, oz, w] = vector.decoded.window!;
      const rows = vector.decoded.rows!.map((hex) => BigInt(`0x${hex}`));
      const depth = g.deep ? w! : 1;
      let set = 0;
      for (let lz = 0; lz < depth; lz++) {
        for (let ly = 0; ly < w!; ly++) {
          const bitsOfRow = rows[lz * w! + ly]!;
          for (let lx = 0; lx < w!; lx++) {
            const expected = ((bitsOfRow >> BigInt(lx)) & 1n) === 1n;
            expect(g.delivered(ox! + lx, oy! + ly, oz! + lz), `cell (${lx}, ${ly}, ${lz}) of the window`).toBe(
              expected,
            );
            set += expected ? 1 : 0;
          }
        }
      }

      expect(set, 'the vector would prove nothing if no cell were delivered').toBeGreaterThan(0);

      // Outside the window on each axis, whatever the bits say.
      expect(g.delivered(ox! - 1, oy!, oz!)).toBe(false);
      expect(g.delivered(ox! + w!, oy!, oz!)).toBe(false);
      expect(g.delivered(ox!, oy! + w!, oz!)).toBe(false);
    });
  }

  it('reads the flags the C# fixture asserts', () => {
    const sphere = new PushGeometry().readFrom(
      new WireReader(payloadOf((goldenJson('debug-push-geometry-sphere') as DebugVector).log).bytes),
    );
    const region = new PushGeometry().readFrom(
      new WireReader(payloadOf((goldenJson('debug-push-geometry-region') as DebugVector).log).bytes),
    );
    expect([sphere.viewComplete, sphere.deep]).toEqual([true, false]);
    expect([region.viewComplete, region.deep]).toEqual([false, true]);
  });

  it('reuses its buffers across decodes, and reports the newest shape', () => {
    const sphere = payloadOf((goldenJson('debug-push-geometry-sphere') as DebugVector).log).bytes;
    const region = payloadOf((goldenJson('debug-push-geometry-region') as DebugVector).log).bytes;
    const g = new PushGeometry();

    // The deep region needs 81 two-byte rows; the flat sphere five one-byte ones. Growing once and never shrinking is what
    // lets a camera being dragged decode a geometry per frame without allocating.
    g.readFrom(new WireReader(region));
    const grown = g.rows;
    expect(grown.length).toBeGreaterThanOrEqual(162);
    for (let i = 0; i < 20; i++) {
      g.readFrom(new WireReader(i % 2 === 0 ? sphere : region));
      expect(g.rows).toBe(grown);
    }

    expect(g.shape).toBe(PushShape.Region);
    expect(g.version).toBe(21);
  });

  it('carries NOTHING of the previous shape into the next, the way a fresh C# reader would not', () => {
    // A retained object rewritten in place is the documented steady state, so every field a shape does not carry must be
    // cleared rather than left. It was not: a region followed by a sphere kept the region's `held` and `nearBudget`, and
    // the HUD went on reporting them. The C# reader allocates, so the two decoders disagreed on identical bytes.
    const sphere = payloadOf((goldenJson('debug-push-geometry-sphere') as DebugVector).log).bytes;
    const region = payloadOf((goldenJson('debug-push-geometry-region') as DebugVector).log).bytes;

    const fresh = new PushGeometry().readFrom(new WireReader(sphere));
    const reused = new PushGeometry();
    reused.readFrom(new WireReader(region));
    reused.readFrom(new WireReader(sphere));

    // Field by field against a reader that saw only the sphere — which is what C# gives on every decode.
    expect(reused.held).toBe(fresh.held);
    expect(reused.nearBudget).toBe(fresh.nearBudget);
    expect(reused.vertexCount).toBe(fresh.vertexCount);
    expect(reused.dims).toBe(fresh.dims);
    expect(reused.radiusM).toBe(fresh.radiusM);
    expect(reused.slackM).toBe(fresh.slackM);
    expect(reused.level).toBe(fresh.level);
    expect([reused.cursorLo, reused.cursorHi]).toEqual([fresh.cursorLo, fresh.cursorHi]);
    expect(Array.from(reused.anchor)).toEqual(Array.from(fresh.anchor));
  });

  it('refuses a payload the C# reader refuses', () => {
    const world = payloadOf((goldenJson('debug-push-geometry-world') as DebugVector).log).bytes;
    const read = (bytes: ArrayLike<number>): void => {
      new PushGeometry().readFrom(new WireReader(Uint8Array.from(bytes)));
    };

    expect(() => {
      read([9, 0]);
    }).toThrow(WireFormatError); // an unknown shape
    expect(() => {
      read([...world, 0]);
    }).toThrow(/left 1 bytes unread/); // a byte after the payload
    expect(() => {
      read(world.subarray(0, world.length - 1));
    }).toThrow(WireFormatError); // a truncated cursor
    expect(() => {
      read([PushShape.Region, 0, 4, 0]);
    }).toThrow(/region of 4 dimensions/);
    expect(() => {
      read([PushShape.Sphere, 0, ...new Uint8Array(32), 0, 0, 0, 0, 65]);
    }).toThrow(/window of 65 cells/);
    // 40 zero bytes: four doubles and three single-byte varints is 35, so five are left over.
    expect(() => new DebugGrid().readFrom(new WireReader(new Uint8Array(40)))).toThrow(/GRID left 5 bytes unread/);
  });

  it('skips a sub-block of an unknown sub-type and still reads the one after it', () => {
    // 03 § 12 W26 names this as a required golden and neither suite had it: every other vector holds exactly one
    // sub-block, so the skip-by-length path — the entire reason the block is framed this way, and what a client relies on
    // to survive a sub-type added after it shipped — was never exercised end to end in either language.
    const vector = goldenJson(UNKNOWN_THEN_GRID) as DebugVector;
    const reader = new TickReader(CatalogPlan.compile(parseCatalog(goldenBin(vector.catalog))));
    reader.realm = frameFromJson(vector.frame);
    const sink = new RecordingSink();
    reader.read(goldenBin(UNKNOWN_THEN_GRID), sink);

    // Both sub-blocks are delivered — the reader hands an unknown one up rather than dropping it, exactly as C# does, and
    // the consumer decides. What matters is that the unknown one's length carried the reader to the next.
    const blocks = sink.log.filter((e) => e.call === 'debug') as { subType: number; payload: string }[];
    expect(blocks).toHaveLength(2);
    expect(blocks[0]!.subType).not.toBe(DebugSubType.Grid);
    expect(blocks[1]!.subType).toBe(DebugSubType.Grid);

    // And the GRID after it decodes to what the C# reader got, which is the proof the skip consumed exactly the right
    // number of bytes: one byte too few or too many and this payload is garbage.
    const { bytes } = payloadOf(vector.log, 1);
    const grid = new DebugGrid().readFrom(new WireReader(bytes));
    expect(bits(grid.cellM)).toBe(vector.decoded.cellM);
    expect([grid.dims[0], grid.dims[1], grid.dims[2]]).toEqual([
      vector.decoded.dimX,
      vector.decoded.dimY,
      vector.decoded.dimZ,
    ]);
  });
});
