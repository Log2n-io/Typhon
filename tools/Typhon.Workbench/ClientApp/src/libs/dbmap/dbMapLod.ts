// LOD-band crossfade math for the Database File Map (Module 15, §6.2). Pure functions, extracted from the
// renderer so the crossfade ramps and the detail-tile addressing are unit-testable without a canvas.

/** Page-cell pixel size below / above which L1 fully shows / hands off to L3. */
export const L3_MIN_PAGE_PX = 220;
export const L3_FULL_PAGE_PX = 640;
/**
 * Page-cell pixel size band over which L3 crossfades into L4 (chunk content / cluster entity sub-grid). Kept just
 * above {@link L3_FULL_PAGE_PX} so content fades in shortly after the chunk grid is legible — at ~800 px/page a
 * 5-chunk cluster's 49-slot sub-grid is already ~30 px/slot, so waiting longer hid the level-under needlessly.
 * A short 640–800 px window stays pure-L3 (the fill heatmap), then content crossfades in by ~2000 px.
 */
export const L4_MIN_PAGE_PX = 800;
export const L4_FULL_PAGE_PX = 2000;
/**
 * Page-cell px at which the L4 chunk *content* starts being fetched — below {@link L4_MIN_PAGE_PX} so the (two-hop:
 * page-detail → chunk) decode has a head start and is resident by the time the L4 crossfade actually ramps. Without
 * this lead the content only loads once you're already at L4, so it lands after the camera settles and pops in.
 */
export const L4_CONTENT_PREFETCH_PAGE_PX = 560;

/** The LOD band currently dominant, plus the L3 / L4 crossfade alphas (0 = absent, 1 = fully in). */
export interface DbLodState {
  band: 'L1' | 'L3' | 'L4';
  l3Alpha: number;
  l4Alpha: number;
}

function clamp01(v: number): number {
  return v < 0 ? 0 : v > 1 ? 1 : v;
}

/**
 * Derives the LOD band and crossfade alphas from the camera scale (pixels per page cell). The crossfade is
 * continuous — both alphas ramp linearly across their band, so a zoom never jump-cuts between L1, L3 and L4.
 */
export function lodForScale(cellPx: number): DbLodState {
  const l3Alpha = clamp01((cellPx - L3_MIN_PAGE_PX) / (L3_FULL_PAGE_PX - L3_MIN_PAGE_PX));
  const l4Alpha = clamp01((cellPx - L4_MIN_PAGE_PX) / (L4_FULL_PAGE_PX - L4_MIN_PAGE_PX));
  const band: DbLodState['band'] = l4Alpha > 0.5 ? 'L4' : l3Alpha > 0.5 ? 'L3' : 'L1';
  return { band, l3Alpha, l4Alpha };
}

/** The detail-tile node ids covering the page-index span `[min, max]` for a given tile size. */
export function tileNodesForSpan(min: number, max: number, tileSize: number): number[] {
  if (tileSize <= 0 || max < min) {
    return [];
  }
  const nodes: number[] = [];
  for (let node = Math.floor(min / tileSize); node <= Math.floor(max / tileSize); node++) {
    nodes.push(node);
  }
  return nodes;
}
