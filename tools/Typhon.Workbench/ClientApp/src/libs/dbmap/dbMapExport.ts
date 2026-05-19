// Export for the Database File Map (Module 15, §4.6 / §13 A4 AC5).
//
// Three exports: the current canvas view as a PNG, the whole Hilbert map as a PNG (nearest-neighbour upscaled,
// resolution-capped), and the region table as CSV. All client-side — a blob + a synthetic download anchor.

import { NO_SEGMENT, PAGE_TYPE_LABELS } from './types';
import type { DbMapRegion } from './dbMapRegions';

/** Resolution cap for the whole-map PNG — a 1024-cell-wide Hilbert image upscales to at most this (§12). */
const MAX_EXPORT_PX = 4096;

function downloadBlob(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}

/** A filesystem-safe stem from the database name. */
function safeName(databaseName: string): string {
  return databaseName.replace(/[^\w.-]+/g, '_') || 'database';
}

/** Exports the current canvas view as a PNG download. */
export function exportViewPng(canvas: HTMLCanvasElement, databaseName: string): void {
  canvas.toBlob((blob) => {
    if (blob) {
      downloadBlob(blob, `${safeName(databaseName)}-view.png`);
    }
  }, 'image/png');
}

/**
 * Exports the whole Hilbert map as a PNG. The map image is one pixel per cell, so it is upscaled with
 * nearest-neighbour to a crisp grid — the scale is the largest integer keeping the output within
 * {@link MAX_EXPORT_PX}.
 */
export function exportWholeMapPng(mapImage: HTMLCanvasElement, databaseName: string): void {
  const side = mapImage.width;
  if (side <= 0) {
    return;
  }
  const scale = Math.max(1, Math.floor(MAX_EXPORT_PX / side));
  const out = document.createElement('canvas');
  out.width = side * scale;
  out.height = side * scale;
  const ctx = out.getContext('2d');
  if (!ctx) {
    return;
  }
  ctx.imageSmoothingEnabled = false;
  ctx.drawImage(mapImage, 0, 0, out.width, out.height);
  out.toBlob((blob) => {
    if (blob) {
      downloadBlob(blob, `${safeName(databaseName)}-map.png`);
    }
  }, 'image/png');
}

/** Builds the region-table CSV text (§4.5) — one row per contiguous run, header first. */
export function regionsToCsv(regions: readonly DbMapRegion[]): string {
  const header = 'startPage,pageCount,byteSize,pageType,ownerSegmentId,fillAvgPercent';
  const rows = regions.map((r) =>
    [
      r.startPage,
      r.pageCount,
      r.byteSize,
      PAGE_TYPE_LABELS[r.pageType] ?? `type${r.pageType}`,
      r.ownerSegmentId === NO_SEGMENT ? '' : r.ownerSegmentId,
      r.fillAvg == null ? '' : Math.round(r.fillAvg * 100),
    ].join(','),
  );
  return [header, ...rows].join('\n');
}

/** Exports the region table as a CSV download. */
export function exportRegionsCsv(regions: readonly DbMapRegion[], databaseName: string): void {
  const blob = new Blob([regionsToCsv(regions)], { type: 'text/csv' });
  downloadBlob(blob, `${safeName(databaseName)}-regions.csv`);
}
