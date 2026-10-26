import type { AggregateGrid } from '@typhondb/client';

const scale = new Float64Array(4);

/**
 * The slab of a three-axis grid the camera is in, clamped to the grid; 0 for a flat one.
 *
 * **The camera's slab, not the maximum over slabs.** A maximum would read as "something is somewhere below you", which
 * is not a number anyone can act on; the slab you are flying through is a claim about where you are (12-realms R8).
 *
 * @param grid The aggregate grid.
 * @param altitudeM The camera's altitude, in realm metres.
 * @returns The slab index.
 */
export function slabAt(grid: AggregateGrid, altitudeM: number): number {
  const depth = grid.schema.dims[2] ?? 1;
  if (depth <= 1) {
    return 0;
  }

  const origin = grid.schema.origin[2] ?? 0;
  const slab = Math.floor((altitudeM - origin) / grid.schema.cell);
  return slab < 0 ? 0 : slab >= depth ? depth - 1 : slab;
}

/**
 * Writes normalised counts into RGBA texels: `log(1 + count) / log(1 + max)` per channel.
 *
 * @param grid The aggregate grid.
 * @param out The RGBA buffer.
 * @param width Texture width, texels.
 * @param height Texture height, texels.
 * @param slab Which slab of a three-axis grid to draw; ignored by a flat grid. {@link slabAt} picks it.
 */
export function fillHeatmap(grid: AggregateGrid, out: Uint8Array, width: number, height: number, slab = 0): void {
  const channels = Math.min(4, grid.archetypeCount);
  scale.fill(0);
  for (let a = 0; a < channels; a++) {
    // The max over the WHOLE grid, not over the slab being drawn: slabs then stay comparable to one another, at the cost
    // of a quiet slab rendering near-black while another is busy. Deliberate, and worth stating since `slab` arrived.
    const max = grid.maxCount(a);
    scale[a] = max > 0 ? 255 / Math.log1p(max) : 0;
  }

  const dimsX = grid.schema.dims[0];
  const dimsZ = grid.schema.dims[1];
  const cells = Math.min(width, dimsX);
  const rows = Math.min(height, dimsZ);
  // `cell = i₀ + dims₀ · (i₁ + dims₁ · i₂)` (03-wire-protocol § 10): the slab is the whole i₂ term, and this used to
  // read `z · dims₀ + x`, which is that formula with i₂ pinned to 0 — right for a flat grid and only the floor of a deep one.
  const base = dimsX * dimsZ * slab;
  out.fill(0);
  for (let z = 0; z < rows; z++) {
    for (let x = 0; x < cells; x++) {
      const cell = base + z * dimsX + x;
      const texel = (z * width + x) * 4;
      for (let a = 0; a < channels; a++) {
        out[texel + a] = Math.round(Math.log1p(grid.count(cell, a)) * scale[a]);
      }
    }
  }
}
