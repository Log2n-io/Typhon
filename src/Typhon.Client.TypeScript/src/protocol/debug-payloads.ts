import { malformed } from './errors.js';
import type { WireReader } from './reader.js';

/**
 * The `DEBUG` sub-blocks the engine sends (`03-wire-protocol.md` § 3 W26, `09-phase2-design.md` § 15), decoded. A
 * correct client never needs them — they are skippable by length — so this module is what a *debugging* client uses to
 * draw what the server actually holds: the replication grid, the session's shape, and which cells have been delivered.
 *
 * **Reusable by construction.** Both payloads decode into an object the caller keeps and rewrites, because
 * `PUSH_GEOMETRY` arrives whenever the geometry moves — which, for a camera being dragged, is every frame. Their
 * buffers grow to the largest shape seen and are then never reallocated.
 *
 * The C# twins are `Typhon.Protocol.DebugGrid` and `Typhon.Protocol.PushGeometry`, and the goldens
 * `debug-grid`, `debug-push-geometry-{sphere,region,world}` hold both sides to the same bytes.
 */

/** The `DEBUG` sub-block types. An unknown one is skipped by its length. */
export const DebugSubType = {
  /** The replication grid ({@link DebugGrid}); sent with the session's first frame and every `RESET`. */
  Grid: 0x01,
  /** Reserved: the clusters meeting the session's range. Not sent yet. */
  ClusterAabbs: 0x02,
  /** Reserved: this tick's migrations in the session's range. Not sent yet. */
  Migrations: 0x03,
  /** The session's push geometry ({@link PushGeometry}). Experimental: its layout can change without a version. */
  PushGeometry: 0x80,
} as const;

/** The shape of a push session's geometry. */
export const PushShape = {
  /** A sphere — a disc in a flat grid — around an anchor. */
  Sphere: 0,
  /** The whole world, delivered in cell-key order. */
  World: 1,
  /** A hull the client sent (`ClientRegion`). */
  Region: 2,
} as const;

/** {@link PushGeometry.flags} bits. */
export const PushGeometryFlags = {
  None: 0,
  /** Every cell the geometry needs has been delivered, as of this frame. */
  ViewComplete: 1,
  /** The grid is deep: the window has `W²` rows, row `(lz · W) + ly`; otherwise `W` rows, row `ly`. */
  Deep: 2,
} as const;

/** The most vertices a region carries, and the most rows a window has — the C# `PushGeometry`'s own bounds. */
const MAX_VERTICES = 16;
const MAX_ROWS = 196;
const MAX_WINDOW = 64;

/**
 * A `GRID` sub-block: the replication grid every push session's cells are keyed on. Cell `(cx, cy, cz)` spans
 * `[origin + c·cell, origin + (c + 1)·cell)` on each axis.
 *
 * `GRID := f64 originX | f64 originY | f64 originZ | f64 cellM | varu dimX | varu dimY | varu dimZ`
 */
export class DebugGrid {
  /** The grid's lowest corner, three axes, in realm metres. */
  readonly origin = new Float64Array(3);
  /** Cells per axis; `dims[2]` is 1 in a flat grid. */
  readonly dims = new Int32Array(3);
  /** The cell side, in metres. */
  cellM = 0;
  /** Bumped on every decode, so a renderer can tell a new grid from the one it uploaded. */
  version = 0;

  /**
   * Decodes a `GRID` payload into this object.
   *
   * @param r A reader positioned at the payload's first byte, limited to it.
   * @returns This grid.
   */
  readFrom(r: WireReader): this {
    for (let i = 0; i < 3; i++) {
      this.origin[i] = r.f64();
    }

    this.cellM = r.f64();
    for (let i = 0; i < 3; i++) {
      this.dims[i] = r.varu();
    }

    expectEnd(r, 'GRID');
    this.version++;
    return this;
  }
}

/**
 * A `PUSH_GEOMETRY` sub-block: what the session holds as of the frame carrying it — its anchor or hull, its LOD level,
 * and a bitmap of the cells actually delivered.
 *
 * ```text
 * PUSH_GEOMETRY := u8 shape | u8 flags | body
 *   SPHERE := f64 anchor[3] | f32 radiusM | f32 slackM | u8 level | window
 *   WORLD  := u64 cursor
 *   REGION := u8 dims | varu vertexCount | (f64 x | f64 y [| f64 z])* | varu held | varu nearBudget | window
 *   window := vari originX | vari originY | vari originZ | u8 W | row*      W rows, or W² if DEEP
 * ```
 *
 * A sphere's {@link radiusM} is R′ *as the session holds it* — after any last-resort shrink — which is the number a
 * client should display, not the radius it asked for.
 */
export class PushGeometry {
  /** One of {@link PushShape}. */
  shape: number = PushShape.World;
  /** A bit set of {@link PushGeometryFlags}. */
  flags = 0;
  /** A sphere's anchor, three axes. */
  readonly anchor = new Float64Array(3);
  /** A sphere's R′, as the session holds it. */
  radiusM = 0;
  /** A sphere profile's half-band h. */
  slackM = 0;
  /** A sphere's LOD level. */
  level = 0;
  /** A World session's cursor, low 32 bits: every cell key below it is delivered. */
  cursorLo = 0;
  /** A World session's cursor, high 32 bits; both are `0xffffffff` once the walk passed the last cell. */
  cursorHi = 0;
  /** A region's dimensions, 2 or 3. */
  dims = 0;
  /** How many vertices of {@link vertices} are live; 0 before the client sent a region. */
  vertexCount = 0;
  /** A region's hull, {@link dims} numbers per vertex, in the engine's order. Only the first `vertexCount * dims` are live. */
  vertices = new Float64Array(MAX_VERTICES * 3);
  /** A region's near-budget estimate of what it holds. */
  held = 0;
  /** A region profile's near budget; 0 for none. */
  nearBudget = 0;
  /** The window's lowest cell, three axes. */
  readonly windowOrigin = new Int32Array(3);
  /** The window's width per axis, in cells; 0 for a World session. */
  window = 0;
  /** How many rows {@link rows} holds: {@link window}, or its square when `DEEP`. */
  rowCount = 0;
  /** Bytes per row, `⌈window / 8⌉`. */
  rowBytes = 0;
  /** The window's rows, little-endian, `rowBytes` each — bit `i` is the cell at `windowOrigin[0] + i`. */
  rows = new Uint8Array(MAX_ROWS * 8);
  /** Bumped on every decode, so a renderer can tell a new geometry from the one it uploaded. */
  version = 0;

  /** Whether the frame carrying this geometry completed the session's view. */
  get viewComplete(): boolean {
    return (this.flags & PushGeometryFlags.ViewComplete) !== 0;
  }

  get deep(): boolean {
    return (this.flags & PushGeometryFlags.Deep) !== 0;
  }

  /**
   * Whether the session holds a cell: inside the window, with its bit set. The C# `PushGeometry.Delivered`'s twin.
   *
   * @param cx The cell's x.
   * @param cy The cell's y.
   * @param cz The cell's z; ignored in a flat grid.
   * @returns Whether it is delivered.
   */
  delivered(cx: number, cy: number, cz: number): boolean {
    const lx = cx - this.windowOrigin[0]!;
    const ly = cy - this.windowOrigin[1]!;
    const lz = this.deep ? cz - this.windowOrigin[2]! : 0;
    const w = this.window;
    if (lx >>> 0 >= w || ly >>> 0 >= w || lz >>> 0 >= w) {
      return false;
    }

    const at = (lz * w + ly) * this.rowBytes + (lx >> 3);
    return ((this.rows[at]! >> (lx & 7)) & 1) !== 0;
  }

  /**
   * Decodes a `PUSH_GEOMETRY` payload into this object.
   *
   * @param r A reader positioned at the payload's first byte, limited to it.
   * @returns This geometry.
   * @throws WireFormatError The shape, the dimension count, the vertex count or the window's width is out of range.
   */
  readFrom(r: WireReader): this {
    this.shape = r.u8();
    this.flags = r.u8();
    // EVERY shape-specific field, not just the window's. The object is retained and rewritten as the geometry moves, so a
    // field the new shape does not carry keeps the old shape's value — `held` and `nearBudget` survived a region → sphere
    // change and the HUD went on reporting the region's numbers. The C# reader builds a fresh object, so leaving them was
    // also a disagreement between the two decoders on identical bytes, which is the one thing the goldens exist to catch.
    this.vertexCount = 0;
    this.window = 0;
    this.rowCount = 0;
    this.rowBytes = 0;
    this.held = 0;
    this.nearBudget = 0;
    this.radiusM = 0;
    this.slackM = 0;
    this.level = 0;
    this.cursorLo = 0;
    this.cursorHi = 0;
    this.dims = 0;
    this.anchor[0] = 0;
    this.anchor[1] = 0;
    this.anchor[2] = 0;
    switch (this.shape) {
      case PushShape.World:
        this.cursorLo = r.u32();
        this.cursorHi = r.u32();
        break;
      case PushShape.Sphere:
        for (let i = 0; i < 3; i++) {
          this.anchor[i] = r.f64();
        }

        this.radiusM = r.f32();
        this.slackM = r.f32();
        this.level = r.u8();
        this.readWindow(r);
        break;
      case PushShape.Region: {
        const dims = r.u8();
        if (dims !== 2 && dims !== 3) {
          throw malformed(`PUSH_GEOMETRY region of ${dims} dimensions`);
        }

        this.dims = dims;
        this.vertexCount = r.varuAtMost(MAX_VERTICES, 'PUSH_GEOMETRY vertex count');
        for (let i = 0; i < this.vertexCount * dims; i++) {
          this.vertices[i] = r.f64();
        }

        this.held = r.varuAtMost(0x7fffffff, 'PUSH_GEOMETRY held');
        this.nearBudget = r.varuAtMost(0x7fffffff, 'PUSH_GEOMETRY near budget');
        this.readWindow(r);
        break;
      }

      default:
        throw malformed(`PUSH_GEOMETRY shape ${this.shape} is unknown`);
    }

    expectEnd(r, 'PUSH_GEOMETRY');
    this.version++;
    return this;
  }

  private readWindow(r: WireReader): void {
    for (let i = 0; i < 3; i++) {
      this.windowOrigin[i] = r.vari();
    }

    const w = r.u8();
    if (w > MAX_WINDOW) {
      throw malformed(`PUSH_GEOMETRY window of ${w} cells`);
    }

    const rowCount = this.deep ? w * w : w;
    if (rowCount > MAX_ROWS) {
      throw malformed(`PUSH_GEOMETRY window of ${rowCount} rows`);
    }

    const rowBytes = (w + 7) >> 3;
    this.window = w;
    this.rowCount = rowCount;
    this.rowBytes = rowBytes;
    const total = rowCount * rowBytes;
    if (this.rows.length < total) {
      this.rows = new Uint8Array(total);
    }

    const at = r.take(total);
    this.rows.set(r.bytes.subarray(at, at + total), 0);
  }
}

/**
 * Refuses a payload with bytes left over, the way the C# `WireReader.ExpectEnd` does: a sub-block whose length exceeds
 * what its layout accounts for is a layout disagreement, and reading it silently would hide exactly the drift these
 * goldens exist to catch. The reader must be limited to the payload — {@link WireReader.reset} over its subarray, or a
 * `pushLimit` around it.
 */
function expectEnd(r: WireReader, what: string): void {
  if (!r.isAtEnd) {
    throw malformed(`${what} left ${r.remaining} bytes unread`);
  }
}
