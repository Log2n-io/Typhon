import { ProtocolConstants } from '../protocol/constants.js';

/** A grid of per-cell entity counts, as the catalog's `grids` entry describes it. */
export interface GridSchema {
  readonly index: number;
  /** World coordinates of the grid's minimum corner, one per axis: position axes 0, 1 and, for three axes, 2. */
  readonly origin: readonly number[];
  /** Edge of a cell on every axis, in world units. */
  readonly cell: number;
  /** Cells per axis, 2 or 3 axes. */
  readonly dims: readonly number[];
  /** Archetype indices counted per cell, in the order the counts travel. */
  readonly archetypes: readonly number[];
}

const EMPTY = new Uint32Array(0);

/**
 * Per-cell counts per archetype (the far tier, `AGG` block): `counts[cell × archetypeCount + slot]` — grouped by cell,
 * so four archetypes map straight onto an RGBA texel. Cells are row-major, axis 0 fastest:
 * `cell = i₀ + dims₀ · (i₁ + dims₁ · i₂)` (`03-wire-protocol.md` § 10).
 *
 * **Memory.** Nothing is allocated before the grid's first `AGG`: a catalog may declare 2²⁴ cells, and a client that
 * never receives the grid should not pay four bytes per cell per archetype for it.
 *
 * **Consuming it.** {@link changed} lists the cells of the current frame only. A renderer does not see every frame
 * (several can be applied between two renders), so it should key on {@link version}: when it moved, re-read the grid.
 * At 64 × 64 cells a full re-read is a few kilobytes.
 */
export class AggregateGrid {
  readonly schema: GridSchema;
  readonly index: number;
  readonly cellCount: number;
  readonly archetypeCount: number;

  /** `cellCount × archetypeCount` counts; empty until the first `AGG` for this grid. */
  counts: Uint32Array = EMPTY;
  /** Cells changed this frame, in `[0, changedCount)`; empty until the first `AGG`. */
  changed: Uint32Array = EMPTY;
  changedCount = 0;
  /** Whether the grid was reset this frame. */
  wasReset = false;
  /** Incremented on every change: a consumer that saw version N has seen everything up to it. */
  version = 0;

  private stamps: Uint32Array = EMPTY;
  private allocated = false;
  private frame = 1;

  constructor(schema: GridSchema) {
    const axes = schema.dims.length;
    if ((axes !== 2 && axes !== 3) || schema.origin.length !== axes) {
      throw new Error(`Grid ${schema.index} needs 2 or 3 axes, with one origin per axis`);
    }

    if (!(schema.cell > 0 && Number.isFinite(schema.cell)) || !schema.origin.every((o) => Number.isFinite(o))) {
      throw new Error(`Grid ${schema.index} needs a finite origin and a positive finite cell`);
    }

    let cells = 1;
    for (const d of schema.dims) {
      if (!(Number.isInteger(d) && d >= 1)) {
        throw new Error(`Grid ${schema.index}: every dimension must be an integer of at least 1`);
      }

      cells = Math.min(cells * d, ProtocolConstants.maxGridCells + 1);
    }

    if (cells > ProtocolConstants.maxGridCells) {
      throw new Error(`Grid ${schema.index} has more than ${ProtocolConstants.maxGridCells} cells`);
    }

    this.schema = schema;
    this.index = schema.index;
    this.cellCount = cells;
    this.archetypeCount = schema.archetypes.length;
  }

  beginFrame(): void {
    this.frame++;
    this.changedCount = 0;
    this.wasReset = false;
  }

  /** Clears every cell (an `AGG` block with the `RESET` flag, or a `RESET` frame). */
  reset(): void {
    this.counts.fill(0);
    this.wasReset = true;
    this.version++;
  }

  /** Writes one cell's counts: `archetypeCount` values of `values` from `offset`. */
  setCell(cell: number, values: ArrayLike<number>, offset: number): void {
    if (!(Number.isInteger(cell) && cell >= 0 && cell < this.cellCount)) {
      throw new Error(`Grid ${this.index}: cell ${cell} is out of range`);
    }

    if (!(offset >= 0 && offset + this.archetypeCount <= values.length)) {
      throw new Error(`Grid ${this.index}: ${this.archetypeCount} counts do not fit at offset ${offset}`);
    }

    if (!this.allocated) {
      this.counts = new Uint32Array(this.cellCount * this.archetypeCount);
      this.changed = new Uint32Array(this.cellCount);
      this.stamps = new Uint32Array(this.cellCount);
      this.allocated = true;
    }

    const base = cell * this.archetypeCount;
    for (let a = 0; a < this.archetypeCount; a++) {
      this.counts[base + a] = values[offset + a]!;
    }

    if (this.stamps[cell] !== this.frame) {
      this.stamps[cell] = this.frame;
      this.changed[this.changedCount++] = cell;
    }

    this.version++;
  }

  /** Count of one archetype slot in a cell, 0 before the first `AGG`; `archetypeSlot` from {@link slotOfArchetype}. */
  count(cell: number, archetypeSlot: number): number {
    return this.counts[cell * this.archetypeCount + archetypeSlot] ?? 0;
  }

  /** Position of an archetype in this grid's counts, or -1 when the grid does not count it. */
  slotOfArchetype(archetype: number): number {
    return this.schema.archetypes.indexOf(archetype);
  }

  /** Largest count of one archetype over all cells. O(cells): call it when {@link version} moved, not per frame. */
  maxCount(archetypeSlot: number): number {
    const counts = this.counts;
    const stride = this.archetypeCount;
    let max = 0;
    if (!(archetypeSlot >= 0 && archetypeSlot < stride)) {
      return max;
    }

    for (let i = archetypeSlot; i < counts.length; i += stride) {
      const c = counts[i]!;
      if (c > max) {
        max = c;
      }
    }

    return max;
  }

  /**
   * Cell containing a world point given by its position axes 0, 1 and — for a three-axis grid — 2, or -1 outside the
   * grid (or for a non-finite coordinate). A two-axis grid ignores `a2`.
   */
  cellAt(a0: number, a1: number, a2 = 0): number {
    const { origin, dims, cell } = this.schema;
    const i0 = Math.floor((a0 - origin[0]!) / cell);
    const i1 = Math.floor((a1 - origin[1]!) / cell);
    if (!(i0 >= 0 && i1 >= 0 && i0 < dims[0]! && i1 < dims[1]!)) {
      return -1;
    }

    if (dims.length === 2) {
      return i0 + dims[0]! * i1;
    }

    const i2 = Math.floor((a2 - origin[2]!) / cell);
    if (!(i2 >= 0 && i2 < dims[2]!)) {
      return -1;
    }

    return i0 + dims[0]! * (i1 + dims[1]! * i2);
  }
}
