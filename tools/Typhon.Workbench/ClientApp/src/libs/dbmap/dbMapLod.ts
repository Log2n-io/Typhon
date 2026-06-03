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

/**
 * On-screen pixel size of a single cluster slot above which its entity's field grid (L5 — the entity-content level
 * beneath the L4 slot sub-grid, file-map §10 Q4 override) crossfades in. L5 is keyed to *slot* px, not page px, because
 * a slot's size depends on the page's chunk count and the cluster size — two clusters at the same page zoom can have
 * very different slot sizes. The field grid is legible by ~200 px/slot; it starts fading at {@link L5_MIN_SLOT_PX}.
 */
export const L5_MIN_SLOT_PX = 90;
export const L5_FULL_SLOT_PX = 200;
/** Slot px at which the entity decode starts being fetched — below {@link L5_MIN_SLOT_PX} so the (three-hop) decode is resident before the L5 crossfade ramps. */
export const L5_CONTENT_PREFETCH_SLOT_PX = 60;

/**
 * Fraction of the fit-to-screen scale at which the L0→L1 crossfade completes. Unlike the L3/L4/L5 thresholds above
 * (absolute page/slot px), this is a *fraction of fit-scale*: L1 (the Hilbert page grid) is fully shown at the fit
 * scale and any zoom-in; the L0 composition stripes are fully shown at `L0_FADE_FRACTION · fit` and below, crossfading
 * across `[fraction·fit, fit]`. Consumed by the renderer's `l1AlphaForScale` AND by the initial-fit camera
 * (`initialL0Camera`), which frames the file at exactly `L0_FADE_FRACTION · fit` — the largest pure-L0 view.
 */
export const L0_FADE_FRACTION = 0.5;

/** The LOD band currently dominant, plus the L3 / L4 crossfade alphas (0 = absent, 1 = fully in). */
export interface DbLodState {
  band: 'L1' | 'L3' | 'L4';
  l3Alpha: number;
  l4Alpha: number;
}

/**
 * The L5 entity-content crossfade alpha for a slot of the given on-screen pixel size (0 = absent, 1 = fully in). Pure
 * function of slot px so the renderer and the fetch planner agree on when the field grid shows. Independent of
 * {@link lodForScale} (which stays a pure page-px band) — L5 nests inside an already-fully-L4 slot.
 */
export function l5AlphaForSlotPx(slotPx: number): number {
  return clamp01((slotPx - L5_MIN_SLOT_PX) / (L5_FULL_SLOT_PX - L5_MIN_SLOT_PX));
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
