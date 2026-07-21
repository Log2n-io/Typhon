// World-space geometry for the Database File Map (Module 15, §3.4, §5.2).
//
// One world unit = one 8 KiB page cell. The data file is a 2^order × 2^order Hilbert square; the WAL is a
// separate region beside it, sized so its on-screen area is proportional to its byte length (1 cell = PAGE_SIZE
// bytes everywhere — strict area ∝ bytes).

import type { Rect } from './camera';
import { hilbertOrderFor, hilbertSide } from './hilbert';
import { PAGE_SIZE } from './types';

export interface MapLayout {
  /** Hilbert curve order — grid is `side × side`. */
  order: number;
  /** Grid side length in cells (`2^order`). */
  side: number;
  /** Number of real pages — cells `[0, pageCount)` of the curve; the rest is inert tail. */
  pageCount: number;
  /** The Hilbert page-grid square. */
  dataRect: Rect;
  /**
   * The WAL region, or null when the database has no WAL — or when WAL display is opted out (the default). The
   * region is currently suppressed because it can only render as an empty opaque block until the WAL page grid
   * (Module 08 / WAL Events) exists; see <c>showWal</c> on {@link buildLayout}.
   */
  walRect: Rect | null;
  /** Union of the data + WAL regions — what the camera fits. */
  worldBounds: Rect;
}

/**
 * Builds the world geometry from the coarse-map header fields. `downSampleFactor` (§5.5) is the page count one
 * grid cell represents — it scales the WAL so the data ↔ WAL area ratio stays strictly proportional to bytes.
 *
 * `showWal` gates the WAL region (default `false`). It is off in production because, until the WAL page grid lands
 * (Module 08 / WAL Events), the region can only draw as an empty opaque block beside the data square — visual noise.
 * The sizing math is retained (and exercised with `showWal: true`) so the region returns cleanly once it has content;
 * with it off, the whole map routes through the already-supported "no WAL" (null) path — renderer, camera-fit,
 * hit-test, nav, and minimap all handle it.
 */
export function buildLayout(
  pageCount: number,
  walBytes: number,
  hilbertOrder: number,
  downSampleFactor = 1,
  showWal = false,
): MapLayout {
  const order = hilbertOrder > 0 ? hilbertOrder : hilbertOrderFor(Math.max(pageCount, 1));
  const side = hilbertSide(order);
  const dataRect: Rect = { x: 0, y: 0, w: side, h: side };

  // A thin gap separates the two L0 regions visually — it is background, not a sized region.
  const gap = side * 0.03;
  let walRect: Rect | null = null;
  if (showWal && walBytes > 0) {
    // Area ∝ bytes: the WAL spans the full data-file height; its width carries the area. One data cell holds
    // `downSampleFactor` pages, so the WAL must be measured in the same cell unit to keep the ratio honest.
    const walCells = walBytes / (PAGE_SIZE * downSampleFactor);
    const walWidth = Math.max(walCells / side, side * 0.02);
    walRect = { x: side + gap, y: 0, w: walWidth, h: side };
  }

  const worldWidth = side + (walRect ? gap + walRect.w : 0);
  return {
    order,
    side,
    pageCount,
    dataRect,
    walRect,
    worldBounds: { x: 0, y: 0, w: worldWidth, h: side },
  };
}
