/** A grid of per-cell entity counts, as the catalog's `grids` entry describes it. */
export interface GridSchema {
  readonly index: number;
  /** World coordinates of the grid's minimum corner. */
  readonly originX: number;
  readonly originZ: number;
  readonly cellM: number;
  readonly dimsX: number;
  readonly dimsZ: number;
  /** Archetype indices counted per cell, in the order the counts travel. */
  readonly archetypes: readonly number[];
}

/**
 * Per-cell counts per archetype (the far tier, `AGG` block): `counts[cell × archetypeCount + slot]`, with
 * `cell = z × dimsX + x` — grouped by cell, so four archetypes map straight onto an RGBA texel.
 *
 * **Consuming it.** {@link changed} lists the cells of the current frame only. A renderer does not see every frame (several
 * can be applied between two renders), so it should key on {@link version}: when it moved, re-read the grid. At 64 × 64
 * cells a full re-read is a few kilobytes.
 */
export class AggregateGrid {
  readonly schema: GridSchema;
  readonly cellCount: number;
  readonly archetypeCount: number;
  readonly counts: Uint32Array;

  /** Cells changed this frame, in `[0, changedCount)`. */
  readonly changed: Uint32Array;
  changedCount = 0;
  /** Whether the grid was reset this frame. */
  wasReset = false;
  /** Incremented on every change: a consumer that saw version N has seen everything up to it. */
  version = 0;

  private readonly stamps: Uint32Array;
  private frame = 1;

  constructor(schema: GridSchema) {
    if (!(schema.dimsX > 0 && schema.dimsZ > 0 && schema.cellM > 0) || schema.archetypes.length === 0) {
      throw new Error(`Grid ${schema.index} has no cells or no archetypes`);
    }

    this.schema = schema;
    this.cellCount = schema.dimsX * schema.dimsZ;
    this.archetypeCount = schema.archetypes.length;
    this.counts = new Uint32Array(this.cellCount * this.archetypeCount);
    this.changed = new Uint32Array(this.cellCount);
    this.stamps = new Uint32Array(this.cellCount);
  }

  beginFrame(): void {
    this.frame++;
    this.changedCount = 0;
    this.wasReset = false;
  }

  /** Clears every cell (an `AGG` block with the `RESET` flag). */
  reset(): void {
    this.counts.fill(0);
    this.wasReset = true;
    this.version++;
  }

  /** Writes one cell's counts: `archetypeCount` values of `values` from `offset`. */
  setCell(cell: number, values: Uint32Array, offset: number): void {
    if (!(cell >= 0 && cell < this.cellCount)) {
      throw new Error(`Grid ${this.schema.index}: cell ${cell} is out of range`);
    }

    if (!(offset >= 0 && offset + this.archetypeCount <= values.length)) {
      throw new Error(`Grid ${this.schema.index}: ${this.archetypeCount} counts do not fit at offset ${offset}`);
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

  /** Count of one archetype slot in a cell. `archetypeSlot` must come from {@link slotOfArchetype} and not be -1. */
  count(cell: number, archetypeSlot: number): number {
    return this.counts[cell * this.archetypeCount + archetypeSlot]!;
  }

  /** Position of an archetype in this grid's counts, or -1 when the grid does not count it. */
  slotOfArchetype(archetype: number): number {
    return this.schema.archetypes.indexOf(archetype);
  }

  /** Largest count of one archetype over all cells. O(cells): call it when {@link version} moved, not per frame. */
  maxCount(archetypeSlot: number): number {
    let max = 0;
    for (let cell = 0; cell < this.cellCount; cell++) {
      const c = this.counts[cell * this.archetypeCount + archetypeSlot]!;
      if (c > max) {
        max = c;
      }
    }

    return max;
  }

  /** Cell containing a world point, or -1 outside the grid (or for a non-finite point). */
  cellAt(x: number, z: number): number {
    const cx = Math.floor((x - this.schema.originX) / this.schema.cellM);
    const cz = Math.floor((z - this.schema.originZ) / this.schema.cellM);
    if (!(cx >= 0 && cz >= 0 && cx < this.schema.dimsX && cz < this.schema.dimsZ)) {
      return -1;
    }

    return cz * this.schema.dimsX + cx;
  }
}
