// Hit-testing for the Database File Map (Module 15, §6.6) — a page-order xy→d inverse plus L0 region tests.
// Zoom-independent: everything goes through the camera transform.

import { screenToWorldX, screenToWorldY, type Camera } from './camera';
import { xyToPage } from './hilbert';
import type { MapLayout } from './dbMapLayout';
import type { DbMapPageOrder } from './types';

/** Which L0 region a screen point falls in. */
export type DbMapRegionHit = 'data' | 'wal' | null;

function inRect(px: number, py: number, r: { x: number; y: number; w: number; h: number }): boolean {
  return px >= r.x && px < r.x + r.w && py >= r.y && py < r.y + r.h;
}

/** The page index under a screen point, or null when the point is off the page grid / on the inert tail. */
export function pageAtScreen(
  cam: Camera,
  layout: MapLayout,
  pageOrder: DbMapPageOrder,
  screenX: number,
  screenY: number,
): number | null {
  const wx = screenToWorldX(cam, screenX);
  const wy = screenToWorldY(cam, screenY);
  if (!inRect(wx, wy, layout.dataRect)) {
    return null;
  }
  const cx = Math.floor(wx - layout.dataRect.x);
  const cy = Math.floor(wy - layout.dataRect.y);
  if (cx < 0 || cy < 0 || cx >= layout.side || cy >= layout.side) {
    return null;
  }
  const page = xyToPage(layout.order, pageOrder, cx, cy);
  return page >= 0 && page < layout.pageCount ? page : null;
}

/** Which L0 region (data file / WAL) a screen point falls in. */
export function regionAtScreen(cam: Camera, layout: MapLayout, screenX: number, screenY: number): DbMapRegionHit {
  const wx = screenToWorldX(cam, screenX);
  const wy = screenToWorldY(cam, screenY);
  if (inRect(wx, wy, layout.dataRect)) {
    return 'data';
  }
  if (layout.walRect && inRect(wx, wy, layout.walRect)) {
    return 'wal';
  }
  return null;
}
