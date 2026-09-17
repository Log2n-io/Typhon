import type { AggregateGrid } from '@typhondb/client';

const scale = new Float64Array(4);

/** Writes normalised counts into RGBA texels: `log(1 + count) / log(1 + max)` per channel. */
export function fillHeatmap(grid: AggregateGrid, out: Uint8Array, width: number, height: number): void {
  const channels = Math.min(4, grid.archetypeCount);
  scale.fill(0);
  for (let a = 0; a < channels; a++) {
    const max = grid.maxCount(a);
    scale[a] = max > 0 ? 255 / Math.log1p(max) : 0;
  }

  const cells = Math.min(width, grid.schema.dimsX);
  const rows = Math.min(height, grid.schema.dimsZ);
  out.fill(0);
  for (let z = 0; z < rows; z++) {
    for (let x = 0; x < cells; x++) {
      const cell = z * grid.schema.dimsX + x;
      const texel = (z * width + x) * 4;
      for (let a = 0; a < channels; a++) {
        out[texel + a] = Math.round(Math.log1p(grid.count(cell, a)) * scale[a]);
      }
    }
  }
}
