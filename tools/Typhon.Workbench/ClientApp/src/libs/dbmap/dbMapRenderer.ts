// Owner-drawn Canvas 2D renderer for the Database File Map (Module 15, §6.6).
//
// Built on the profiler's owner-drawn pattern (libs/profiler/canvas) — no third-party drawing library. The
// coarse Hilbert map (L0/L1) is painted once into an offscreen image (one pixel per page); every frame is then
// a single camera-transformed drawImage, so the L1 cost is independent of database size. A2 adds the live
// per-frame deep bands — L3 chunk grids and L4 decoded content — and the L1↔L3↔L4 crossfades; those are
// viewport-culled, so their cost tracks what is on screen, not the file size. The class surface is the seam
// behind which a PixiJS renderer could be swapped if Canvas 2D ever missed 60 fps.

import {
  visibleWorldRect,
  worldToScreenX,
  worldToScreenY,
  type Camera,
  type Rect,
} from './camera';
import { buildLayout, type MapLayout } from './dbMapLayout';
import { pageAtScreen } from './dbMapHitTest';
import { L4_CONTENT_PREFETCH_PAGE_PX, lodForScale, tileNodesForSpan, type DbLodState } from './dbMapLod';
import { chunkAreaRect, gridCols, gridSubRect } from './dbMapGrid';
import {
  BYTE_CLASS_RGB,
  CRC_RGB,
  FREE_RGB,
  INDEX_INTERNAL_RGB,
  PAGE_TYPE_RGB,
  RESIDENCY_RGB,
  STRUCT_RGB,
  TAIL_RGB,
  allocationRgb,
  contentCellRgb,
  enabledOverlayRgb,
  entropyRgb,
  fillDensityRgb,
  pageColorRgb,
  writeAgeRgb,
  type Rgb,
} from './dbMapColors';
import { computeComposition, type L0Stripe } from './dbMapL0';
import { hilbertD2XY, hilbertXY2D } from './hilbert';
import { formatFileSize } from '@/lib/formatters';
import {
  DbChunkClass,
  DbCrcStatus,
  DbPageType,
  DbResidency,
  NO_SEGMENT,
  PAGE_SIZE,
  PAGE_TYPE_LABELS,
  isDetailEncoding,
  type DbChunkContent,
  type DbDetailTile,
  type DbMapData,
  type DbMapEncoding,
  type DbMapLens,
  type DbPageDetail,
} from './types';
import type { PathologyFlag } from './dbMapPathology';
import type { DbMapRegion } from './dbMapRegions';

/** Theme tokens the renderer needs — resolved from CSS variables by the panel. */
export interface DbMapTheme {
  background: string;
  surface: string;
  border: string;
  text: string;
  mutedText: string;
  accent: string;
}

/** What the panel must fetch for the current camera — computed by {@link DbMapRenderer.getDetailRequest}. */
export interface DbDetailRequest {
  /** Detail-tile node ids intersecting the viewport (for the active detail encoding). */
  tileNodes: number[];
  /** Visible page indices needing their L3 chunk grid. */
  pages: number[];
  /** Visible chunk refs needing their L4 content. */
  chunks: { segId: number; chunkId: number }[];
}

/** An L0 composition stripe with its on-screen rectangle (CSS pixels) — drives panel hit-testing (§4.4). */
export interface L0StripeScreenRect {
  stripe: L0Stripe;
  /** CSS-pixel rect — the area the stripe occupies on the canvas at the current camera. */
  screenRect: Rect;
}

/**
 * Cell pixel size below which only L0 shows; above L1_FULL_CELL only L1 shows. Between → crossfade.
 * Sized so the L0 composition stripes are the default view at "fit to screen" on a realistic database
 * (4k–1M pages → cell px 0.8–12 at fit, well inside the L0-dominant band). L1 takes over once cells are
 * large enough that individual pages can be visually counted — past ~32 px.
 */
const L0_ONLY_CELL = 6;
const L1_FULL_CELL = 32;
/** Opacity of the lens dim layer — non-highlighted pages fade back so the lens mask reads clearly (§4.3). */
const LENS_DIM_ALPHA = 0.62;
/** Outline colour for search-match cells (§4.5) — amber, distinct from the accent selection outline. */
const SEARCH_HIT_COLOR = 'rgb(245, 158, 11)';
/** Page-cell pixel size above which the faint per-page gridline overlay is drawn. */
const GRID_MIN_CELL = 6;
/**
 * Cell pixel size above which the page-header hatched strip is drawn (§3.4). The 192 B header maps to
 * `cell × 192/8192 = cell × 0.0234`, hitting 1 px at cell ≈ 43 — below this the strip is sub-pixel and the
 * page reads as solid body, which is fine since you can't make out anything else at that zoom either.
 */
const HEADER_STRIP_MIN_CELL = 44;
/** Header byte share of a page — drives the strip's vertical fraction of the cell. */
const HEADER_RATIO = 192 / 8192;
/**
 * Diagonal-hatch line spacing (screen px) for the header strip and the L3 header/directory overhead bands. Wide
 * enough that the 1 px diagonals don't moiré against the pixel grid when the bands grow large at deep zoom, still
 * tight enough to read as a pattern on the thin L1 header strip.
 */
const HEADER_HATCH_SPACING = 10;
/** Minimum cell px below which the free-page diagonal mark is sub-pixel and skipped. */
const FREE_HATCH_MIN_CELL = 4;
/** Minimum cell px below which corner markers (residency / CRC / pathology) are too small to register. */
const CORNER_MARKER_MIN_CELL = 16;
/** Persistent-marker corner-mark radius, in CSS pixels. */
const CORNER_MARKER_RADIUS = 3;
/** CRC-failure triangle leg length, in CSS pixels. */
const CRC_TRIANGLE_SIZE = 9;
/** Persistent semantic colours for the L1 corner markers (theme-independent — these read as warnings). */
const RESIDENT_CLEAN_COLOR = 'rgb(34, 197, 94)';
const RESIDENT_DIRTY_COLOR = 'rgb(234, 179, 8)';
const PATHOLOGY_COLOR = 'rgb(245, 158, 11)';
const CRC_FAILED_COLOR = 'rgb(239, 68, 68)';
// Overflow-bucket marker (A6). Deliberately NOT amber: overflow buckets are full, so they fill amber via the
// fillDensityRgb ramp — an amber dot on amber would vanish. Red appears nowhere in the dark→blue→amber ramp.
const OVERFLOW_DOT_COLOR = 'rgb(244, 63, 94)';

// Decoders whose L4 value is the Detail-panel inspector, not an on-canvas content-cell sub-grid (A6). They draw no
// sub-grid (drawChunkContent returns early) and expose no selectable sub-cell — hover / selection fall back to the
// chunk. Keeping this set in one place keeps the draw path and the hit-test (pickContentCell) in lockstep, so the
// hit-test never subdivides such a chunk into phantom cells the renderer doesn't draw.
const INSPECTOR_ONLY_DECODERS = new Set(['vsbs', 'string', 'hash-bucket', 'index']);
/** Cell pixel size above which a tiny segment-id badge appears in each page cell's corner (L1 tier 1). */
const CELL_LABEL_BADGE_MIN_CELL = 50;
/** Cell pixel size above which the page index is rendered centred in the cell (L1 tier 2). */
const CELL_LABEL_INDEX_MIN_CELL = 100;
/** Minimum on-screen bbox dimension (px) for a segment-name label to render — keeps tiny fragments unlabelled. */
const SEGMENT_LABEL_MIN_BBOX_PX = 80;
/** Minimum on-screen chunk-cell px above which the per-chunk index label is drawn at L3 (L3 enhancement #4). */
const CHUNK_LABEL_MIN_PX = 28;
/**
 * Duration of the per-chunk content fade-in. L4 decoded content is a two-hop fetch (chunk ids depend on the page
 * detail), so it usually lands after the zoom crossfade has already settled at full l4Alpha — without this it would
 * pop in. Fading each chunk's content from its arrival time eases that in (and makes cached vs cold load look the same).
 */
const CONTENT_FADE_MS = 160;
/** Opacity of the per-page gridline overlay — barely visible, just enough to give each cell a border. */
const GRID_ALPHA = 0.04;
const MINIMAP_SIZE = 140;
const MINIMAP_MARGIN = 12;
const OFFSET_STRIP_HEIGHT = 16;
/** L0 header band height in CSS pixels (DB name + size + counters + pathology badge). */
const L0_HEADER_HEIGHT = 22;
/** L0 legend chrome dimensions — drawn in the minimap slot when the minimap is hidden at L0. */
const L0_LEGEND_WIDTH = 200;
const L0_LEGEND_ROW_HEIGHT = 14;
const L0_LEGEND_PAD = 8;
/** Below this on-screen stripe height (CSS px) the in-stripe label is suppressed — it would not be legible. */
const L0_STRIPE_LABEL_MIN_PX = 14;
/** Stripes with under this share are kept in the column but dropped from the legend list (visual noise). */
const L0_LEGEND_MIN_FRACTION = 0.005;
/** Safety caps so a degenerate camera can never schedule an unbounded fetch / draw. */
const MAX_VISIBLE_PAGES = 256;
const MAX_VISIBLE_CHUNKS = 256;

function clamp01(v: number): number {
  return v < 0 ? 0 : v > 1 ? 1 : v;
}

function rgb(c: Rgb): string {
  return `rgb(${c[0]}, ${c[1]}, ${c[2]})`;
}

/** Total bytes before chunk 0 on a chunk-based page — header + root directory + stride-alignment padding (A6). */
function chunkOverheadBytes(detail: DbPageDetail): number {
  return (detail.headerBytes ?? 0) + (detail.directoryBytes ?? 0) + (detail.paddingBytes ?? 0);
}

export class DbMapRenderer {
  private readonly _canvas: HTMLCanvasElement;
  private readonly _ctx: CanvasRenderingContext2D;
  private readonly _offscreen: HTMLCanvasElement;
  private readonly _offCtx: CanvasRenderingContext2D;
  // The lens highlight buffer — the offscreen Hilbert image with non-masked pages made transparent. Rebuilt
  // only when the lens mask or the base encoding changes, so a lens costs one extra drawImage per frame (§4.3).
  private readonly _highlight: HTMLCanvasElement;
  private readonly _highlightCtx: CanvasRenderingContext2D;
  // The filter-to-dim buffer (§4.6) — a translucent dim overlay covering only filter-excluded cells, so it
  // composes on top of the lens (a cell stays bright iff it passes both). Rebuilt only when the filter changes.
  private readonly _filter: HTMLCanvasElement;
  private readonly _filterCtx: CanvasRenderingContext2D;
  private _filterMask: Uint8Array | null = null;

  private _data: DbMapData | null = null;
  private _layout: MapLayout | null = null;
  private _encoding: DbMapEncoding = 'pageType';
  private _segmentOverlay = false;
  private _lens: DbMapLens = 'none';
  private _lensMask: Uint8Array | null = null;
  private _camera: Camera = { scale: 1, x: 0, y: 0 };
  private _hover: number | null = null;
  private _selection: number | null = null;
  // Chunk-granular hover / selection for L3 (proposal 2) — set by the panel from pickChunk. When present (and
  // the chunk band is visible) the outline tracks the individual chunk rather than the whole page cell.
  private _hoverChunk: { page: number; chunkInPage: number } | null = null;
  private _selectionChunk: { page: number; chunkInPage: number } | null = null;
  private _hoverCell: { page: number; chunkInPage: number; cellIndex: number } | null = null;
  private _selectionCell: { page: number; chunkInPage: number; cellIndex: number } | null = null;
  private _searchHits: readonly number[] = [];
  private _searchCurrent = -1;

  // A2 detail-tier inputs, fed by the panel as the viewport changes.
  private _tiles: Map<number, DbDetailTile> = new Map();
  private _pageDetails: Map<number, DbPageDetail> = new Map();
  private _chunkContents: Map<string, DbChunkContent> = new Map();
  // Per-chunk content arrival timestamps (key → performance.now()), for the fade-in (see CONTENT_FADE_MS).
  private _contentArrival: Map<string, number> = new Map();
  private _componentOverlay: { segmentId: number; componentSlot: number } | null = null;
  private _maxChangeRevision = 1;

  // L0 composition stripes, lazily computed and cached on (data, encoding) — see {@link getL0Stripes}.
  private _l0Stripes: L0Stripe[] | null = null;
  // Set of flagged pages (under-filled etc.) for the L1 markers; count drives the L0 header badge.
  private _pathologyPages: Set<number> = new Set();
  // Whether to draw the persistent cache-residency corner mark per page (L1 enhancement #3).
  private _residencyOverlay = true;
  // Whether to draw on-canvas region captions per RLE run (L1 enhancement #7); regions come from the panel.
  private _regionCaptions = false;
  private _regions: readonly DbMapRegion[] = [];

  private _cssW = 1;
  private _cssH = 1;
  private _dpr = 1;

  private _theme: DbMapTheme = {
    background: '#0f172a',
    surface: '#1e293b',
    border: '#334155',
    text: '#e2e8f0',
    mutedText: '#94a3b8',
    accent: '#38bdf8',
  };

  constructor(canvas: HTMLCanvasElement) {
    this._canvas = canvas;
    const ctx = canvas.getContext('2d');
    if (!ctx) {
      throw new Error('DbMapRenderer: 2D canvas context unavailable');
    }
    this._ctx = ctx;
    this._offscreen = document.createElement('canvas');
    // willReadFrequently — the offscreen image is read back by paintHighlightBuffer to build the lens mask.
    const offCtx = this._offscreen.getContext('2d', { willReadFrequently: true });
    if (!offCtx) {
      throw new Error('DbMapRenderer: offscreen 2D context unavailable');
    }
    this._offCtx = offCtx;
    this._highlight = document.createElement('canvas');
    const highlightCtx = this._highlight.getContext('2d');
    if (!highlightCtx) {
      throw new Error('DbMapRenderer: highlight 2D context unavailable');
    }
    this._highlightCtx = highlightCtx;
    this._filter = document.createElement('canvas');
    const filterCtx = this._filter.getContext('2d');
    if (!filterCtx) {
      throw new Error('DbMapRenderer: filter 2D context unavailable');
    }
    this._filterCtx = filterCtx;
  }

  // ── Inputs ────────────────────────────────────────────────────────────────────────────────────────────

  setData(data: DbMapData | null): void {
    this._data = data;
    this._tiles = new Map();
    this._pageDetails = new Map();
    this._chunkContents = new Map();
    this._contentArrival = new Map();
    this._maxChangeRevision = 1;
    // A fresh map invalidates the previous map's lens mask, filter mask, search hits and L0 composition.
    this._lensMask = null;
    this._filterMask = null;
    this._searchHits = [];
    this._searchCurrent = -1;
    this._hoverChunk = null;
    this._selectionChunk = null;
    this._hoverCell = null;
    this._selectionCell = null;
    this._l0Stripes = null;
    if (!data) {
      this._layout = null;
      return;
    }
    this._layout = buildLayout(data.pageCount, data.walBytes, data.hilbertOrder, data.downSampleFactor);
    this._offscreen.width = this._layout.side;
    this._offscreen.height = this._layout.side;
    this._highlight.width = this._layout.side;
    this._highlight.height = this._layout.side;
    this._filter.width = this._layout.side;
    this._filter.height = this._layout.side;
    this.paintOffscreen();
  }

  setEncoding(encoding: DbMapEncoding): void {
    if (this._encoding === encoding) {
      return;
    }
    this._encoding = encoding;
    this._l0Stripes = null;
    this.paintOffscreen();
  }

  /**
   * Sets the pathology flag set. Drives both the L0 header badge (count) and the L1 per-cell marker (page
   * lookup against the set). Empty array clears both.
   */
  setPathologyFlags(flags: readonly PathologyFlag[]): void {
    this._pathologyPages = new Set(flags.map((f) => f.pageIndex));
  }

  /** Whether the cache-residency corner mark is drawn on each page at L1 (L1 enhancement #3). */
  setResidencyOverlay(on: boolean): void {
    this._residencyOverlay = on;
  }

  /** Whether on-canvas region captions are drawn at L1 (L1 enhancement #7). */
  setRegionCaptions(on: boolean): void {
    this._regionCaptions = on;
  }

  /** Feeds the RLE region list (from {@link buildRegions}) used by the captions overlay. */
  setRegions(regions: readonly DbMapRegion[]): void {
    this._regions = regions;
  }

  setSegmentOverlay(on: boolean): void {
    this._segmentOverlay = on;
  }

  /**
   * Sets the active analytical lens and its per-page highlight mask (1 = highlighted, 0 = dimmed). The mask is
   * computed by the panel; this rebuilds the highlight buffer once, so the lens then costs one extra drawImage
   * per frame regardless of database size (§4.3).
   */
  setLens(lens: DbMapLens, mask: Uint8Array | null): void {
    this._lens = lens;
    this._lensMask = lens === 'none' ? null : mask;
    this.paintHighlightBuffer();
  }

  /**
   * Sets the filter-to-dim mask (§4.6) — 1 = the cell passes the filter (stays bright), 0 = it is dimmed back.
   * `null` clears the filter. Rebuilds the filter buffer once; the filter then costs one drawImage per frame
   * and composes on top of the lens — a cell is bright only if it passes the lens *and* the filter.
   */
  setFilter(mask: Uint8Array | null): void {
    this._filterMask = mask;
    this.paintFilterBuffer();
  }

  setCamera(camera: Camera): void {
    this._camera = camera;
  }

  setHover(page: number | null): void {
    this._hover = page;
  }

  setSelection(page: number | null): void {
    this._selection = page;
  }

  /** Sets the chunk under the cursor for the L3 hover outline; null falls back to the whole-page hover (#2). */
  setHoverChunk(hit: { page: number; chunkInPage: number } | null): void {
    this._hoverChunk = hit;
  }

  /** Sets the selected chunk for the L3 selection outline; null falls back to the whole-page selection (#2). */
  setHoverCell(hit: { page: number; chunkInPage: number; cellIndex: number } | null): void {
    this._hoverCell = hit;
  }

  setSelectionCell(hit: { page: number; chunkInPage: number; cellIndex: number } | null): void {
    this._selectionCell = hit;
  }

  setSelectionChunk(hit: { page: number; chunkInPage: number } | null): void {
    this._selectionChunk = hit;
  }

  /** Sets the search-match pages to mark on the map; `current` is the index the camera is flown to (§4.5). */
  setSearchHits(pages: readonly number[], current: number): void {
    this._searchHits = pages;
    this._searchCurrent = current;
  }

  setTheme(theme: DbMapTheme): void {
    this._theme = theme;
    // The filter buffer bakes in the theme's dim colour — rebuild it so a theme toggle recolours the dim layer.
    this.paintFilterBuffer();
  }

  /** Feeds the detail tiles for the active detail encoding; repaints the offscreen L1 map. */
  setDetailTiles(tiles: Map<number, DbDetailTile>): void {
    this._tiles = tiles;
    let max = 1;
    for (const tile of tiles.values()) {
      if (tile.maxChangeRevision > max) {
        max = tile.maxChangeRevision;
      }
    }
    this._maxChangeRevision = max;
    if (isDetailEncoding(this._encoding)) {
      this.paintOffscreen();
    }
  }

  /** Feeds the per-page detail (L3 chunk grids) for the visible pages. */
  setPageDetails(pages: Map<number, DbPageDetail>): void {
    this._pageDetails = pages;
  }

  /** Feeds the per-chunk decoded content (L4) for the visible chunks. Stamps an arrival time on each newly-present
   *  chunk so it fades in (see CONTENT_FADE_MS) rather than popping when its (late) decode lands. */
  setChunkContents(chunks: Map<string, DbChunkContent>): void {
    const now = performance.now();
    const arrival = this._contentArrival;
    for (const key of chunks.keys()) {
      if (!arrival.has(key)) {
        arrival.set(key, now);
      }
    }
    // Drop arrivals for chunks no longer resident so re-entering a region fades again (and the map stays bounded).
    for (const key of arrival.keys()) {
      if (!chunks.has(key)) {
        arrival.delete(key);
      }
    }
    this._chunkContents = chunks;
  }

  /** Whether any resident chunk's content is still within its fade-in window — the panel keeps rendering until false. */
  hasFadingContent(): boolean {
    const now = performance.now();
    for (const t of this._contentArrival.values()) {
      if (now - t < CONTENT_FADE_MS) {
        return true;
      }
    }
    return false;
  }

  /** Ease-out fade multiplier (0..1) for a chunk's content, from its arrival time; 1 once past the fade window. */
  private contentFadeAlpha(key: string): number {
    const arrival = this._contentArrival.get(key);
    if (arrival == null) {
      return 1;
    }
    const e = (performance.now() - arrival) / CONTENT_FADE_MS;
    return e >= 1 ? 1 : e <= 0 ? 0 : 1 - Math.pow(1 - e, 3);
  }

  /**
   * Sets the per-component enabled-state overlay (A6 §10.1): when active, L4 entity slots of `segmentId` recolour by
   * whether component bit `componentSlot` is set in each slot's `enabledMask`. Pass null to restore occupancy colouring.
   */
  setComponentOverlay(overlay: { segmentId: number; componentSlot: number } | null): void {
    this._componentOverlay = overlay;
  }

  setViewport(cssWidth: number, cssHeight: number, dpr: number): void {
    this._cssW = Math.max(1, cssWidth);
    this._cssH = Math.max(1, cssHeight);
    this._dpr = dpr;
    this._canvas.width = Math.floor(this._cssW * dpr);
    this._canvas.height = Math.floor(this._cssH * dpr);
    this._canvas.style.width = `${this._cssW}px`;
    this._canvas.style.height = `${this._cssH}px`;
  }

  getLayout(): MapLayout | null {
    return this._layout;
  }

  /**
   * The offscreen Hilbert map image — one pixel per cell, painted in the active encoding. Drives the whole-map
   * PNG export (§4.6); null until a map is loaded.
   */
  getWholeMapImage(): HTMLCanvasElement | null {
    return this._layout ? this._offscreen : null;
  }

  // ── LOD / detail-request queries (consumed by the panel) ────────────────────────────────────────────────

  /** The current LOD band and crossfade alphas, derived purely from the camera scale. */
  getLodState(): DbLodState {
    return lodForScale(this._camera.scale);
  }

  /**
   * The currently-dominant *display* band — extends {@link DbLodState}'s band (which lives in [L1, L3, L4])
   * with `L0` for the zoomed-out composition view. Drives the per-band Legend chrome (Module 15 L1 #7).
   */
  getDisplayBand(): 'L0' | 'L1' | 'L3' | 'L4' {
    const cellPx = this._camera.scale;
    const { band, l3Alpha, l4Alpha } = lodForScale(cellPx);
    if (l4Alpha > 0.5) {
      return 'L4';
    }
    if (l3Alpha > 0.5) {
      return 'L3';
    }
    if (band === 'L1') {
      const l1Alpha = clamp01((cellPx - L0_ONLY_CELL) / (L1_FULL_CELL - L0_ONLY_CELL));
      return l1Alpha < 0.5 ? 'L0' : 'L1';
    }
    return band;
  }

  /** The page index under the viewport centre, or null when the centre is off the page grid. */
  getFocusedPage(): number | null {
    return this.pageAt(this._cssW / 2, this._cssH / 2);
  }

  /**
   * Computes what the panel must fetch for the current camera: detail tiles for the active detail encoding,
   * L3 page details when zoomed into the chunk band, and L4 chunk content when zoomed into the content band.
   */
  getDetailRequest(): DbDetailRequest {
    const request: DbDetailRequest = { tileNodes: [], pages: [], chunks: [] };
    if (!this._data || !this._layout) {
      return request;
    }
    const { l3Alpha } = this.getLodState();
    const span = this.visiblePageSpan();

    if (isDetailEncoding(this._encoding)) {
      // visiblePageSpan is null when the whole file is on screen — at that zoom the detail encoding still
      // needs every tile to colour the map, so fall back to the full tile range (each tile stays bounded).
      const tileSize = this._data.detailTileSize;
      request.tileNodes = span
        ? tileNodesForSpan(span.min, span.max, tileSize)
        : tileNodesForSpan(0, this._data.pageCount - 1, tileSize);
    }

    if (l3Alpha > 0) {
      request.pages = this.visiblePageList();
    }

    // Fetch L4 chunk content before the L4 crossfade actually ramps (L4_CONTENT_PREFETCH_PAGE_PX < L4_MIN_PAGE_PX),
    // so its two-hop decode (page-detail → chunk) is resident as the content fades in instead of popping after the
    // zoom settles. l4Alpha itself is unaffected — this only changes *when fetching starts*, not when content shows.
    if (this._camera.scale >= L4_CONTENT_PREFETCH_PAGE_PX) {
      for (const page of this.visiblePageList()) {
        const detail = this._pageDetails.get(page);
        if (!detail || detail.chunkTotal <= 0 || detail.ownerSegmentId < 0) {
          continue;
        }
        for (let i = 0; i < detail.chunkTotal && request.chunks.length < MAX_VISIBLE_CHUNKS; i++) {
          request.chunks.push({ segId: detail.ownerSegmentId, chunkId: detail.firstChunkId + i });
        }
      }
    }

    return request;
  }

  /**
   * The detail request the given camera *would* need, computed without disturbing the live camera. The panel
   * uses this to prefetch a fly-to destination's L3/L4 tier while the camera is still animating toward it, so
   * the deep bands are resident (or in flight) by the time the tween lands — eliminating the blank-then-pop on
   * the L1→L3 transition. The camera is swapped in only for the synchronous duration of {@link getDetailRequest}
   * and always restored, so no frame ever observes the borrowed camera. Note the L4 chunk set still depends on
   * resident page details: a cold destination prefetches its L3 page bodies first, and the chunks follow once
   * those resolve (the panel re-syncs on `pageDetails` arrival, by which point the tween has usually landed).
   */
  getDetailRequestForCamera(cam: Camera): DbDetailRequest {
    const live = this._camera;
    this._camera = cam;
    try {
      return this.getDetailRequest();
    } finally {
      this._camera = live;
    }
  }

  // ── Chrome geometry (used by the panel for minimap / offset-strip hit-testing) ──────────────────────────

  getMinimapScreenRect(): Rect {
    return {
      x: this._cssW - MINIMAP_SIZE - MINIMAP_MARGIN,
      y: this._cssH - MINIMAP_SIZE - MINIMAP_MARGIN - OFFSET_STRIP_HEIGHT,
      w: MINIMAP_SIZE,
      h: MINIMAP_SIZE,
    };
  }

  getOffsetStripScreenRect(): Rect {
    return { x: 0, y: this._cssH - OFFSET_STRIP_HEIGHT, w: this._cssW, h: OFFSET_STRIP_HEIGHT };
  }

  /** Maps a point inside the minimap to the world coordinate it represents. */
  minimapToWorld(screenX: number, screenY: number): { x: number; y: number } | null {
    if (!this._layout) {
      return null;
    }
    const mm = this.getMinimapScreenRect();
    const fx = clamp01((screenX - mm.x) / mm.w);
    const fy = clamp01((screenY - mm.y) / mm.h);
    return { x: fx * this._layout.worldBounds.w, y: fy * this._layout.worldBounds.h };
  }

  /** Maps a point on the offset strip to a page index. */
  offsetStripToPage(screenX: number): number | null {
    if (!this._layout || this._layout.pageCount === 0) {
      return null;
    }
    const f = clamp01(screenX / this._cssW);
    return Math.min(this._layout.pageCount - 1, Math.floor(f * this._layout.pageCount));
  }

  // ── Hit-testing ─────────────────────────────────────────────────────────────────────────────────────────

  /** The page index under a screen point, or null when off the page grid. */
  pageAt(screenX: number, screenY: number): number | null {
    return this._layout ? pageAtScreen(this._camera, this._layout, screenX, screenY) : null;
  }

  /** The chunk (page + in-page index) under a screen point at L3, or null. */
  pickChunk(screenX: number, screenY: number): { page: number; chunkInPage: number } | null {
    const page = this.pageAt(screenX, screenY);
    if (page == null || !this._layout) {
      return null;
    }
    const detail = this._pageDetails.get(page);
    if (!detail || detail.chunkTotal <= 0) {
      return null;
    }
    const { x, y } = hilbertD2XY(this._layout.order, page);
    const wx = (screenX - this._camera.x) / this._camera.scale - (this._layout.dataRect.x + x);
    const wy = (screenY - this._camera.y) / this._camera.scale - (this._layout.dataRect.y + y);
    // The chunk grid occupies only the cell below the reserved overhead band — remap wy into that sub-area so the
    // hit-test matches the render (a point in the header/directory/padding band is not a chunk).
    const top = chunkOverheadBytes(detail) / PAGE_SIZE;
    if (wy < top) {
      return null;
    }
    const wyArea = top < 1 ? (wy - top) / (1 - top) : 0;
    const cols = gridCols(detail.chunkTotal);
    const rows = Math.ceil(detail.chunkTotal / cols);
    const col = Math.min(cols - 1, Math.max(0, Math.floor(wx * cols)));
    const row = Math.min(rows - 1, Math.max(0, Math.floor(wyArea * rows)));
    const chunkInPage = row * cols + col;
    return chunkInPage < detail.chunkTotal ? { page, chunkInPage } : null;
  }

  /** The content cell (page + chunk + cell index) under a screen point at L4, or null. */
  pickContentCell(screenX: number, screenY: number): { page: number; chunkInPage: number; cellIndex: number } | null {
    const hit = this.pickChunk(screenX, screenY);
    if (!hit || !this._layout) {
      return null;
    }
    const detail = this._pageDetails.get(hit.page);
    if (!detail) {
      return null;
    }
    const content = this._chunkContents.get(`${detail.ownerSegmentId}:${detail.firstChunkId + hit.chunkInPage}`);
    if (!content || content.cells.length === 0) {
      return null;
    }
    // Inspector-only decoders draw no sub-grid (see drawChunkContent), so there is no selectable sub-cell — hover /
    // selection fall back to the chunk. Without this the hit-test would subdivide the chunk into phantom cells matching
    // the metadata rows, painting a hover rect over nothing and opening a confusing cell detail (Role / Entries / …).
    if (INSPECTOR_ONLY_DECODERS.has(content.decoder)) {
      return null;
    }
    const { x, y } = hilbertD2XY(this._layout.order, hit.page);
    const cols = gridCols(detail.chunkTotal);
    const rows = Math.ceil(detail.chunkTotal / cols);
    const chunkCol = hit.chunkInPage % cols;
    const chunkRow = Math.floor(hit.chunkInPage / cols);
    // Map the cursor into the chunk's local space, accounting for the reserved overhead band (chunks live below it).
    const top = chunkOverheadBytes(detail) / PAGE_SIZE;
    const pageY = (screenY - this._camera.y) / this._camera.scale - (this._layout.dataRect.y + y);
    const areaY = top < 1 ? (pageY - top) / (1 - top) : 0;
    const wx = (screenX - this._camera.x) / this._camera.scale - (this._layout.dataRect.x + x) - chunkCol / cols;
    const ccols = gridCols(content.cells.length);
    const col = Math.min(ccols - 1, Math.max(0, Math.floor(wx * cols * ccols)));
    const row = Math.min(ccols - 1, Math.max(0, Math.floor((areaY * rows - chunkRow) * ccols)));
    const cellIndex = row * ccols + col;
    return cellIndex < content.cells.length ? { page: hit.page, chunkInPage: hit.chunkInPage, cellIndex } : null;
  }

  /** World rect of a file-page cell (1×1 in the Hilbert data grid), or null when the layout isn't built. */
  pageWorldRect(page: number): Rect | null {
    if (!this._layout) {
      return null;
    }
    const { x, y } = hilbertD2XY(this._layout.order, page);
    return { x: this._layout.dataRect.x + x, y: this._layout.dataRect.y + y, w: 1, h: 1 };
  }

  /** World rect of a chunk within its page (inside the reserved overhead band), or null if out of range. */
  chunkWorldRect(hit: { page: number; chunkInPage: number }): Rect | null {
    const pageRect = this.pageWorldRect(hit.page);
    const detail = this._pageDetails.get(hit.page);
    if (!pageRect || !detail || detail.chunkTotal <= 0 || hit.chunkInPage < 0 || hit.chunkInPage >= detail.chunkTotal) {
      return null;
    }
    const cols = gridCols(detail.chunkTotal);
    const rows = Math.ceil(detail.chunkTotal / cols);
    const area = chunkAreaRect(pageRect, chunkOverheadBytes(detail), PAGE_SIZE);
    return gridSubRect(area, cols, rows, hit.chunkInPage);
  }

  /** World rect of one decoded content cell within its chunk, or null if the chunk isn't decoded / index out of range. */
  contentCellWorldRect(hit: { page: number; chunkInPage: number; cellIndex: number }): Rect | null {
    const chunkRect = this.chunkWorldRect(hit);
    const detail = this._pageDetails.get(hit.page);
    if (!chunkRect || !detail) {
      return null;
    }
    const content = this._chunkContents.get(`${detail.ownerSegmentId}:${detail.firstChunkId + hit.chunkInPage}`);
    if (!content || hit.cellIndex < 0 || hit.cellIndex >= content.cells.length) {
      return null;
    }
    const ccols = gridCols(content.cells.length);
    const crows = Math.ceil(content.cells.length / ccols);
    return gridSubRect(chunkRect, ccols, crows, hit.cellIndex);
  }

  /**
   * World rect of the element under a screen point at the current LOD — a content cell at L4, a chunk at L3, the page
   * cell at L1 — for "double-click to fit". Mirrors the granularity of {@link drawContentCellHighlight} / selection.
   */
  elementWorldRectAt(screenX: number, screenY: number): Rect | null {
    const band = this.getLodState().band;
    if (band === 'L4') {
      const cell = this.pickContentCell(screenX, screenY);
      if (cell) {
        return this.contentCellWorldRect(cell);
      }
    }
    if (band === 'L3' || band === 'L4') {
      const chunk = this.pickChunk(screenX, screenY);
      if (chunk) {
        return this.chunkWorldRect(chunk);
      }
    }
    const page = this.pageAt(screenX, screenY);
    return page != null ? this.pageWorldRect(page) : null;
  }

  // ── Render ──────────────────────────────────────────────────────────────────────────────────────────

  render(): void {
    const ctx = this._ctx;
    ctx.save();
    ctx.setTransform(this._dpr, 0, 0, this._dpr, 0, 0);
    ctx.fillStyle = this._theme.background;
    ctx.fillRect(0, 0, this._cssW, this._cssH);

    if (!this._data || !this._layout) {
      ctx.fillStyle = this._theme.mutedText;
      ctx.font = '12px sans-serif';
      ctx.textAlign = 'center';
      ctx.fillText('No database open', this._cssW / 2, this._cssH / 2);
      ctx.restore();
      return;
    }

    const cam = this._camera;
    const layout = this._layout;
    const cellPx = cam.scale;
    const l1Alpha = clamp01((cellPx - L0_ONLY_CELL) / (L1_FULL_CELL - L0_ONLY_CELL));
    const { l3Alpha, l4Alpha } = this.getLodState();

    // L0 — composition stripes inside the data rect, sized strictly proportionally to bytes (§3.4). The L1
    // image fades in over the stripes during the L0→L1 crossfade — same colours so no flicker.
    const l0Alpha = 1 - l1Alpha;
    if (l0Alpha > 0) {
      this.drawL0Stripes(ctx, l0Alpha);
    }

    // L1 — the Hilbert page grid (the offscreen image), camera-transformed.
    if (l1Alpha > 0) {
      ctx.globalAlpha = l1Alpha;
      const dr = layout.dataRect;
      ctx.imageSmoothingEnabled = cam.scale < 1;
      ctx.drawImage(
        this._offscreen,
        worldToScreenX(cam, dr.x),
        worldToScreenY(cam, dr.y),
        dr.w * cam.scale,
        dr.h * cam.scale,
      );
      ctx.globalAlpha = 1;
    }

    // A faint per-page gridline overlay — gives every page cell a thin border once cells are big enough to
    // read it; suppressed once fully in L3 where the chunk grid takes over.
    if (l1Alpha > 0 && l3Alpha < 1) {
      this.drawPageGrid(ctx, l1Alpha);
    }

    // Free-page hatch (L1 enhancement #6) — one subtle diagonal per Free cell. Distinguishes intentionally
    // empty pages from "an encoding colour I don't recognise" so the user never confuses dead space with an
    // unfamiliar tint. Drawn under the header strip so the header hatch stays on top.
    if (l1Alpha > 0 && l3Alpha < 1 && cellPx >= FREE_HATCH_MIN_CELL) {
      this.drawFreePagesHatch(ctx, l1Alpha);
    }

    // Tail crosshatch — labels grid cells that aren't part of the file at all (Hilbert padding past
    // pageCount). Distinct from the free-page diagonal so the user can tell "outside file" from
    // "intentionally empty" at a glance.
    if (l1Alpha > 0 && l3Alpha < 1 && cellPx >= FREE_HATCH_MIN_CELL) {
      this.drawTailHatch(ctx, l1Alpha);
    }

    // Page-header strip (§3.4) — every page's 192 B header rendered to scale as a horizontal strip at the
    // top of the cell, filled with the page-type colour and hatched. The hatch signals "this zone is engine
    // overhead, not user-addressable data"; it stays visible under any encoding (the hatch pattern, not just
    // colour, differentiates it from the body), so `pageType` encoding still reads as split.
    if (l1Alpha > 0 && l3Alpha < 1 && cellPx >= HEADER_STRIP_MIN_CELL) {
      this.drawPageHeaderHatch(ctx, l1Alpha);
    }

    // Progressive per-cell labels — tier 1 = segment badge in the corner, tier 2 = page index centred.
    if (l1Alpha > 0 && l3Alpha < 1 && cellPx >= CELL_LABEL_BADGE_MIN_CELL) {
      this.drawCellLabels(ctx, l1Alpha);
    }

    // Persistent corner markers (L1 #3 + #4) — residency + CRC-failure + pathology. Always visible across
    // any encoding so the user never misses a critical signal because they're in the "wrong" colour mode.
    if (l1Alpha > 0 && l3Alpha < 1 && cellPx >= CORNER_MARKER_MIN_CELL) {
      this.drawCornerMarkers(ctx, l1Alpha);
    }

    // Lens — dim the whole data file, then punch the masked pages back through at full opacity (§4.3). Drawn
    // over L1 but under L3, so a drilled-in chunk grid stays unobscured.
    if (this._lens !== 'none' && l1Alpha > 0 && l3Alpha < 1) {
      const dr = layout.dataRect;
      ctx.globalAlpha = l1Alpha * LENS_DIM_ALPHA;
      ctx.fillStyle = this._theme.background;
      this.fillWorldRect(ctx, dr);
      ctx.globalAlpha = l1Alpha;
      ctx.imageSmoothingEnabled = cam.scale < 1;
      ctx.drawImage(
        this._highlight,
        worldToScreenX(cam, dr.x),
        worldToScreenY(cam, dr.y),
        dr.w * cam.scale,
        dr.h * cam.scale,
      );
      ctx.globalAlpha = 1;
    }

    // Filter-to-dim (§4.6) — a translucent overlay darkening only the filter-excluded cells. Drawn after the
    // lens so the two compose: a cell stays bright only if it passes the lens *and* the filter.
    if (this._filterMask && l1Alpha > 0 && l3Alpha < 1) {
      const dr = layout.dataRect;
      ctx.globalAlpha = l1Alpha;
      ctx.imageSmoothingEnabled = cam.scale < 1;
      ctx.drawImage(
        this._filter,
        worldToScreenX(cam, dr.x),
        worldToScreenY(cam, dr.y),
        dr.w * cam.scale,
        dr.h * cam.scale,
      );
      ctx.globalAlpha = 1;
    }

    // L3 — chunk grids, crossfaded in over L1; L4 — decoded content, crossfaded in over L3. A 2 px page
    // contour follows so it's unambiguous which page a chunk belongs to (the internal chunk gridlines are
    // deliberately faint and don't read as a page boundary).
    if (l3Alpha > 0) {
      this.drawChunkBand(ctx, l3Alpha, l4Alpha);
      this.drawPageContours(ctx, l3Alpha);
    }

    // The WAL — an opaque sized region, drawn at every zoom level (A1: no WAL page grid).
    if (layout.walRect) {
      ctx.fillStyle = this._theme.surface;
      this.fillWorldRect(ctx, layout.walRect);
      ctx.strokeStyle = this._theme.border;
      ctx.lineWidth = 1;
      this.strokeWorldRect(ctx, layout.walRect);
      this.drawWalLabel(ctx, layout.walRect);
    }

    // Data-file outline — keeps the file extent legible at any zoom.
    ctx.strokeStyle = this._theme.border;
    ctx.lineWidth = 1;
    this.strokeWorldRect(ctx, layout.dataRect);

    // The segment-boundary overlay is a page-grid (L1) annotation — tie it to the L1 image's visibility so it
    // fades in with the page grid and never draws over the L0 composition stripes (which aren't page-based).
    if (this._segmentOverlay && l1Alpha > 0 && l3Alpha < 1) {
      this.drawSegmentOverlay(ctx, l1Alpha);
    }

    // Page-cell markers — search hits, hover and selection are all L1+ annotations: each outlines a single
    // page at its Hilbert position, which is meaningless over the L0 composition stripes (a 1D byte-proportional
    // column, not the 2D page grid). Gate on the L1 image being visible so a selection made at L1 doesn't leave
    // a stray box floating over the stripes when the user zooms back out to L0. drawCellHighlight clamps the
    // outline to a 3 px minimum, so without this gate the box stays visible even at sub-pixel cell sizes.
    if (l1Alpha > 0) {
      // Search-match markers — every hit gets a thin amber outline; the current match a thicker one (§4.5).
      for (let i = 0; i < this._searchHits.length; i++) {
        this.drawCellHighlight(ctx, this._searchHits[i], SEARCH_HIT_COLOR, i === this._searchCurrent ? 3 : 1, l1Alpha);
      }
      // Hover / selection outline the individual chunk under the cursor at L3 (proposal 2), falling back to the
      // whole-page cell at L1 where there's no chunk grid. Gated on l3Alpha so a chunk selected at L3 reverts
      // to a page outline once zoomed back out rather than leaving a tiny stray box on the page.
      // Highlight at the finest granularity the zoom exposes: a decoded content cell at L4, else the chunk at L3,
      // else the whole page at L1. Without the L4 case a click on an entity slot updates the panel but leaves no
      // mark on the map (the outline stayed at chunk granularity).
      if (l4Alpha > 0 && this._hoverCell) {
        this.drawContentCellHighlight(ctx, this._hoverCell, this._theme.mutedText, 1);
      } else if (l3Alpha > 0 && this._hoverChunk) {
        this.drawChunkHighlight(ctx, this._hoverChunk, this._theme.mutedText, 1);
      } else {
        this.drawCellHighlight(ctx, this._hover, this._theme.mutedText, 1, l1Alpha);
      }
      if (l4Alpha > 0 && this._selectionCell) {
        this.drawContentCellHighlight(ctx, this._selectionCell, this._theme.accent, 2);
      } else if (l3Alpha > 0 && this._selectionChunk) {
        this.drawChunkHighlight(ctx, this._selectionChunk, this._theme.accent, 2);
      } else {
        this.drawCellHighlight(ctx, this._selection, this._theme.accent, 2, l1Alpha);
      }
    }

    // Per-run segment labels — one label per contiguous run, placed on the run's own cells. Drawn last of the
    // map content (after the header hatch, corner markers, cell badges and highlights) so a label pill is never
    // overdrawn by them. `SegName k/M` by default; the 'c' toggle switches to the verbose size variant.
    if (l1Alpha > 0 && l3Alpha < 1) {
      this.drawRunLabels(ctx, l1Alpha);
    }

    // The minimap is hidden at L0 — it shows the same image as the main view, so it adds nothing. It fades
    // in during the L0→L1 crossfade as the legend (which uses the same slot) fades out.
    if (l1Alpha > 0) {
      ctx.globalAlpha = l1Alpha;
      this.drawMinimap(ctx);
      ctx.globalAlpha = 1;
    }
    this.drawOffsetStrip(ctx);

    // Header band + legend last — chrome that must stay legible over anything underneath. Both fade with L0.
    if (l0Alpha > 0) {
      this.drawL0Header(ctx, l0Alpha);
      this.drawL0Legend(ctx, l0Alpha);
    }

    ctx.restore();
  }

  // ── L0 — composition stripes, header band, legend, hit-test (§3.4 / §4.4) ──────────────────────────

  /**
   * The L0 composition stripes for the current data + encoding. Cached — recompute is O(pageCount), cheap on
   * any realistic database, but caching keeps it off the per-frame path during a pan / wheel-zoom hold. The
   * cache is invalidated by {@link setData} and {@link setEncoding}.
   */
  private getL0Stripes(): readonly L0Stripe[] {
    if (!this._data) {
      return [];
    }
    if (!this._l0Stripes) {
      this._l0Stripes = computeComposition(this._data, this._encoding);
    }
    return this._l0Stripes;
  }

  /** The L0 stripe screen rectangles in CSS pixels — geometry shared by drawing and hit-testing. */
  getL0StripeRects(): L0StripeScreenRect[] {
    if (!this._layout || !this._data) {
      return [];
    }
    const stripes = this.getL0Stripes();
    if (stripes.length === 0) {
      return [];
    }
    const dr = this._layout.dataRect;
    const screenX = worldToScreenX(this._camera, dr.x);
    const screenY0 = worldToScreenY(this._camera, dr.y);
    const screenW = dr.w * this._camera.scale;
    const screenHTotal = dr.h * this._camera.scale;
    const out: L0StripeScreenRect[] = [];
    let cursor = 0;
    for (const stripe of stripes) {
      const h = stripe.fraction * screenHTotal;
      out.push({
        stripe,
        screenRect: { x: screenX, y: screenY0 + cursor, w: screenW, h },
      });
      cursor += h;
    }
    return out;
  }

  /** Returns the stripe under a screen point, or null when L0 is invisible (L1 fully in) or no stripe is hit. */
  l0HitTest(screenX: number, screenY: number): L0Stripe | null {
    // The stripes' opacity is `1 - l1Alpha`; once cell px ≥ L1_FULL_CELL they paint at alpha 0. A click in
    // that range belongs to L1 — return null so the panel falls through to its `pageAt` path.
    if (this._camera.scale >= L1_FULL_CELL) {
      return null;
    }
    for (const { stripe, screenRect } of this.getL0StripeRects()) {
      if (
        screenX >= screenRect.x &&
        screenX < screenRect.x + screenRect.w &&
        screenY >= screenRect.y &&
        screenY < screenRect.y + screenRect.h
      ) {
        return stripe;
      }
    }
    return null;
  }

  /** Paints the L0 composition stripes inside the data rect. Each stripe's height is exactly `fraction × h`. */
  private drawL0Stripes(ctx: CanvasRenderingContext2D, alpha: number): void {
    const rects = this.getL0StripeRects();
    if (rects.length === 0) {
      return;
    }
    ctx.save();
    ctx.globalAlpha = alpha;
    for (const { stripe, screenRect } of rects) {
      ctx.fillStyle = rgb(stripe.color);
      ctx.fillRect(screenRect.x, screenRect.y, screenRect.w, screenRect.h);
    }
    // Hairlines between stripes — barely visible, only when each stripe is comfortably tall, so they don't
    // collapse into a solid mush on tiny stripes. Keeps the column readable even when adjacent stripes
    // happen to share a colour family (e.g. several blue segments).
    ctx.strokeStyle = this._theme.background;
    ctx.lineWidth = 1;
    ctx.globalAlpha = alpha * 0.35;
    ctx.beginPath();
    for (let i = 1; i < rects.length; i++) {
      const y = Math.round(rects[i].screenRect.y) + 0.5;
      ctx.moveTo(rects[i].screenRect.x, y);
      ctx.lineTo(rects[i].screenRect.x + rects[i].screenRect.w, y);
    }
    ctx.stroke();

    // In-stripe labels, gated on each stripe's own on-screen height — small bands stay color-only.
    ctx.globalAlpha = alpha;
    ctx.fillStyle = this._theme.text;
    ctx.font = '11px sans-serif';
    ctx.textAlign = 'left';
    ctx.textBaseline = 'middle';
    const labelPadX = 8;
    for (const { stripe, screenRect } of rects) {
      if (screenRect.h < L0_STRIPE_LABEL_MIN_PX) {
        continue;
      }
      const maxLabelPx = screenRect.w - labelPadX * 2;
      if (maxLabelPx <= 0) {
        continue;
      }
      const label = `${stripe.label}  ·  ${formatFileSize(stripe.byteCount)}  ·  ${(stripe.fraction * 100).toFixed(1)}%`;
      const cy = screenRect.y + screenRect.h / 2;
      // Truncate with ellipsis so a narrow stripe never spills past its right edge — keeps the column from
      // overflowing the data rect when the panel shrinks or the stripe represents a small slice.
      ctx.fillText(truncateToWidth(ctx, label, maxLabelPx), screenRect.x + labelPadX, cy);
    }
    ctx.restore();
  }

  /** Header band at the top of the canvas — DB identity + global counters, L0-only (fades with `alpha`). */
  private drawL0Header(ctx: CanvasRenderingContext2D, alpha: number): void {
    if (!this._data) {
      return;
    }
    const data = this._data;
    const head = `${data.databaseName}  ·  ${formatFileSize(data.dataFileBytes)}  ·  ${data.pageCount.toLocaleString()} pages  ·  ${data.segments.length} segments  ·  LSN ${data.checkpointLsn}`;
    ctx.save();
    ctx.globalAlpha = alpha;
    ctx.fillStyle = this._theme.surface;
    ctx.fillRect(0, 0, this._cssW, L0_HEADER_HEIGHT);
    ctx.fillStyle = this._theme.border;
    ctx.fillRect(0, L0_HEADER_HEIGHT - 1, this._cssW, 1);
    ctx.fillStyle = this._theme.text;
    ctx.font = '11px sans-serif';
    ctx.textAlign = 'left';
    ctx.textBaseline = 'middle';
    ctx.fillText(head, 10, L0_HEADER_HEIGHT / 2);
    if (this._pathologyPages.size > 0) {
      // The amber badge is colour-anchored (matches the search-hit / Root colour); kept theme-independent
      // so a pathology flag reads the same regardless of light / dark mode.
      ctx.fillStyle = PATHOLOGY_COLOR;
      ctx.textAlign = 'right';
      ctx.fillText(`⚠ ${this._pathologyPages.size} issues`, this._cssW - 10, L0_HEADER_HEIGHT / 2);
    }
    ctx.restore();
  }

  /**
   * Legend at the bottom-right — one row per stripe with a colour swatch, label, byte size and %. Sits in the
   * minimap slot, which is hidden at L0; legend opacity mirrors the minimap's emerging opacity during the
   * L0→L1 crossfade so neither overlaps the other visually.
   */
  private drawL0Legend(ctx: CanvasRenderingContext2D, alpha: number): void {
    const stripes = this.getL0Stripes().filter((s) => s.fraction >= L0_LEGEND_MIN_FRACTION);
    if (stripes.length === 0) {
      return;
    }
    const rowH = L0_LEGEND_ROW_HEIGHT;
    const innerH = stripes.length * rowH + L0_LEGEND_PAD * 2;
    const w = L0_LEGEND_WIDTH;
    // Anchor to the minimap slot so the legend ↔ minimap handoff is visually consistent during the crossfade.
    const mm = this.getMinimapScreenRect();
    const x = mm.x + mm.w - w;
    const y = mm.y + mm.h - innerH;
    ctx.save();
    ctx.globalAlpha = alpha;
    ctx.fillStyle = this._theme.surface;
    ctx.fillRect(x, y, w, innerH);
    ctx.strokeStyle = this._theme.border;
    ctx.lineWidth = 1;
    ctx.strokeRect(x + 0.5, y + 0.5, w - 1, innerH - 1);

    ctx.font = '10px sans-serif';
    ctx.textBaseline = 'middle';
    for (let i = 0; i < stripes.length; i++) {
      const s = stripes[i];
      const ry = y + L0_LEGEND_PAD + i * rowH + rowH / 2;
      ctx.fillStyle = rgb(s.color);
      ctx.fillRect(x + L0_LEGEND_PAD, ry - 5, 10, 10);
      ctx.fillStyle = this._theme.text;
      ctx.textAlign = 'left';
      // Truncate long labels — segment+typeName can easily overflow the panel.
      const maxLabelPx = w - L0_LEGEND_PAD * 2 - 14 - 80;
      ctx.fillText(truncateToWidth(ctx, s.label, maxLabelPx), x + L0_LEGEND_PAD + 16, ry);
      ctx.fillStyle = this._theme.mutedText;
      ctx.textAlign = 'right';
      ctx.fillText(`${formatFileSize(s.byteCount)}  ${(s.fraction * 100).toFixed(1)}%`, x + w - L0_LEGEND_PAD, ry);
    }
    ctx.restore();
  }

  // ── Private draw helpers ────────────────────────────────────────────────────────────────────────────

  private paintOffscreen(): void {
    if (!this._data || !this._layout) {
      return;
    }
    const { side, pageCount, order } = this._layout;
    const img = this._offCtx.createImageData(side, side);
    const buf = img.data;
    // createImageData zero-fills, so the inert Hilbert tail (cells beyond pageCount) stays fully transparent
    // — the canvas background shows through and {@link drawTailHatch} labels the region with a crosshatch.
    // Keeping TAIL_RGB imported for any callers that still want it (e.g. the unknown tile).
    for (let p = 0; p < pageCount; p++) {
      const { x, y } = hilbertD2XY(order, p);
      const rgbColor = this.pageEncodingRgb(p);
      const o = (y * side + x) * 4;
      buf[o] = rgbColor[0];
      buf[o + 1] = rgbColor[1];
      buf[o + 2] = rgbColor[2];
      buf[o + 3] = 255;
    }
    this._offCtx.putImageData(img, 0, 0);
    this.paintHighlightBuffer();
  }

  /**
   * The page's [r,g,b] under the active encoding — the single resolution used by both the offscreen L1 image
   * and the L3 chunk band, so an occupied chunk is tinted exactly the colour its page shows at L1 (proposal 1,
   * encoding continuity). Detail encodings fall back to the coarse `pageType` colour until the tile loads.
   */
  private pageEncodingRgb(page: number): Rgb {
    if (!this._data) {
      return PAGE_TYPE_RGB[DbPageType.Unknown];
    }
    const { pageType, ownerSegmentId } = this._data;
    if (isDetailEncoding(this._encoding)) {
      return this.detailPageRgb(page) ?? pageColorRgb('pageType', pageType[page], ownerSegmentId[page]);
    }
    return pageColorRgb(this._encoding, pageType[page], ownerSegmentId[page]);
  }

  /**
   * Rebuilds the lens highlight buffer: a copy of the offscreen Hilbert image in which every page outside the
   * lens mask is fully transparent. O(pageCount), but runs only when the mask or the base encoding changes.
   */
  private paintHighlightBuffer(): void {
    if (!this._layout) {
      return;
    }
    const { side, pageCount, order } = this._layout;
    const dst = this._highlightCtx.createImageData(side, side);
    if (this._lens !== 'none' && this._lensMask) {
      const src = this._offCtx.getImageData(0, 0, side, side).data;
      const mask = this._lensMask;
      for (let p = 0; p < pageCount; p++) {
        if (mask[p] !== 1) {
          continue;
        }
        const { x, y } = hilbertD2XY(order, p);
        const o = (y * side + x) * 4;
        dst.data[o] = src[o];
        dst.data[o + 1] = src[o + 1];
        dst.data[o + 2] = src[o + 2];
        dst.data[o + 3] = 255;
      }
    }
    this._highlightCtx.putImageData(dst, 0, 0);
  }

  /**
   * Rebuilds the filter-to-dim buffer (§4.6): the whole grid filled with the theme's dim colour at
   * {@link LENS_DIM_ALPHA}, then the filter-passing cells punched back to transparent. The result is a dim
   * overlay touching only excluded cells — one drawImage per frame, rebuilt only when the filter changes.
   */
  private paintFilterBuffer(): void {
    if (!this._layout) {
      return;
    }
    const { side, pageCount, order } = this._layout;
    const ctx = this._filterCtx;
    ctx.globalCompositeOperation = 'source-over';
    ctx.clearRect(0, 0, side, side);
    const mask = this._filterMask;
    if (!mask) {
      return;
    }
    ctx.globalAlpha = LENS_DIM_ALPHA;
    ctx.fillStyle = this._theme.background;
    ctx.fillRect(0, 0, side, side);
    ctx.globalAlpha = 1;
    // destination-out clears the alpha of the passing cells, so only the excluded cells keep the dim fill.
    ctx.globalCompositeOperation = 'destination-out';
    for (let p = 0; p < pageCount; p++) {
      if (mask[p] === 1) {
        const { x, y } = hilbertD2XY(order, p);
        ctx.fillRect(x, y, 1, 1);
      }
    }
    ctx.globalCompositeOperation = 'source-over';
  }

  /** Detail-encoding colour for a page, or null when its tile is not loaded (caller falls back to coarse). */
  private detailPageRgb(page: number): Rgb | null {
    if (!this._data) {
      return null;
    }
    const tile = this._tiles.get(Math.floor(page / this._data.detailTileSize));
    if (!tile) {
      return null;
    }
    const i = page - tile.firstPage;
    if (i < 0 || i >= tile.pageCount) {
      return null;
    }
    switch (this._encoding) {
      case 'fillDensity':
        return fillDensityRgb(tile.fillRatio[i] / 255);
      case 'writeAge':
        return writeAgeRgb(tile.changeRevision[i] / this._maxChangeRevision);
      case 'crc':
        return CRC_RGB[tile.crcStatus[i]] ?? CRC_RGB[0];
      case 'residency':
        return RESIDENCY_RGB[tile.residency[i]] ?? RESIDENCY_RGB[0];
      case 'entropy':
        return entropyRgb(tile.entropy[i] / 255);
      case 'byteClass':
        return BYTE_CLASS_RGB[tile.byteClass[i]] ?? BYTE_CLASS_RGB[0];
      default:
        return null;
    }
  }

  /** Draws the L3 chunk grids (and, crossfaded over them, the L4 decoded content) for the visible pages. */
  private drawChunkBand(ctx: CanvasRenderingContext2D, l3Alpha: number, l4Alpha: number): void {
    if (!this._layout || !this._data) {
      return;
    }
    const layout = this._layout;
    const pageType = this._data.pageType;
    for (const page of this.visiblePageList()) {
      const detail = this._pageDetails.get(page);
      if (!detail) {
        continue;
      }
      const { x, y } = hilbertD2XY(layout.order, page);
      const pageRect: Rect = { x: layout.dataRect.x + x, y: layout.dataRect.y + y, w: 1, h: 1 };

      // An occupancy page is not chunk-based but is the densest page in the file: render it as a mini
      // allocation-map of the file-page range it governs (A6, §10.2) instead of leaving it blank.
      if (pageType[page] === DbPageType.Occupancy && detail.occupancyMap && detail.occupancyMap.length > 0) {
        this.drawOccupancyMap(ctx, pageRect, detail, l3Alpha);
        continue;
      }

      if (detail.chunkTotal <= 0) {
        // A non-chunk-based page (free / root / occupancy / index) has no L3 chunk grid — it keeps its L1
        // appearance. Only a genuinely unclassified page becomes the unknown tile (§3.4), never blank.
        if (pageType[page] === DbPageType.Unknown) {
          ctx.globalAlpha = l3Alpha;
          this.drawUnknownTile(ctx, pageRect);
          ctx.globalAlpha = 1;
        }
        continue;
      }

      const cols = gridCols(detail.chunkTotal);
      const rows = Math.ceil(detail.chunkTotal / cols);
      const chunkPx = this._camera.scale / cols;
      const occ = detail.chunkOccupancy;
      // The chunk grid maps the page's real data area, not the whole cell: reserve the byte-proportional overhead
      // band at the top (per-page header + the root page's segment directory). This is why a segment's root page
      // visibly fits fewer chunks than its later pages (A6).
      const area = chunkAreaRect(pageRect, chunkOverheadBytes(detail), PAGE_SIZE);
      // Fill frontier — index of the last allocated chunk. Free chunks before it are reclaimable holes (internal
      // fragmentation); free chunks after it are the page's growth headroom (proposal 3).
      let lastOccupied = -1;
      for (let i = detail.chunkTotal - 1; i >= 0; i--) {
        if (occ[i] === 1) {
          lastOccupied = i;
          break;
        }
      }
      // The overhead band at the top of the cell: header (hatched) + root-only directory (distinct hatch).
      this.drawOverheadBands(ctx, page, pageRect, detail, l3Alpha);
      // Occupied chunks inherit the page's encoding colour so the L1→L3 crossfade is seamless and every encoding
      // stays meaningful at L3 (proposal 1); free chunks keep the dark slate fill. Container-kind chunks (A6 —
      // e.g. a cluster) instead colour by their intra-chunk fill ramp so a half-empty cluster reads cooler than
      // a full one — the per-structure fragmentation signal, not just allocated/free.
      const occColor = rgb(this.pageEncodingRgb(page));
      const freeColor = rgb(FREE_RGB);
      const structColor = rgb(STRUCT_RGB);
      const internalColor = rgb(INDEX_INTERNAL_RGB);
      const chunkFill = detail.chunkFill;
      const chunkClass = detail.chunkClass;
      ctx.globalAlpha = l3Alpha;
      for (let i = 0; i < detail.chunkTotal; i++) {
        const cls = chunkClass ? chunkClass[i] : undefined;
        if (occ[i] !== 1) {
          ctx.fillStyle = freeColor;
        } else if (cls === DbChunkClass.NonData) {
          // A structural chunk (hashmap meta / directory, B-tree directory) is not data — dim slate, no fill ramp; hatched below (A6).
          ctx.fillStyle = structColor;
        } else if (cls === DbChunkClass.Internal) {
          // A B-tree internal node — the sparse skeleton over the leaves; amber accent so the tree shape reads at a glance (A6).
          ctx.fillStyle = internalColor;
        } else if (chunkFill && (cls === DbChunkClass.ContainerFill || cls === DbChunkClass.Overflow)) {
          // Container / overflow chunks colour by intra-chunk fill so a half-empty structure reads cooler than a full one.
          ctx.fillStyle = rgb(fillDensityRgb(chunkFill[i] / 255));
        } else {
          // Plain occupied (component row) or a B-tree leaf — the page encoding colour.
          ctx.fillStyle = occColor;
        }
        this.fillWorldRect(ctx, gridSubRect(area, cols, rows, i));
      }
      // Thin gridlines keep the chunk grid legible even when every chunk is occupied (a solid fill otherwise).
      this.drawChunkGridLines(ctx, area, cols, rows, l3Alpha);
      // Reclaimable holes — free chunks before the fill frontier — marked with an amber diagonal (proposal 3).
      this.drawChunkHoles(ctx, occ, area, cols, rows, lastOccupied, l3Alpha);
      // Per-kind class markers (A6): hatch the non-data (meta / directory) chunks, dot the overflowing buckets.
      this.drawChunkClassMarkers(ctx, detail, occ, area, cols, rows, l3Alpha);

      // The near-square cols×rows grid can exceed chunkTotal; the surplus trailing cells (bottom-right) are
      // not real chunks. Mark them as invalid area with the page-level Hilbert-tail X crosshatch so the padding
      // never masquerades as data by leaking the page's base colour through the grid.
      const cellCount = cols * rows;
      for (let i = detail.chunkTotal; i < cellCount; i++) {
        const sub = gridSubRect(area, cols, rows, i);
        this.drawInvalidChunkCell(
          ctx,
          worldToScreenX(this._camera, sub.x),
          worldToScreenY(this._camera, sub.y),
          sub.w * this._camera.scale,
          sub.h * this._camera.scale,
          l3Alpha,
        );
      }

      if (l4Alpha > 0) {
        ctx.globalAlpha = l4Alpha;
        for (let i = 0; i < detail.chunkTotal; i++) {
          // Only allocated chunks have live content. A free chunk's bytes are stale/zero, so decoding them paints
          // garbage — most visibly the cluster enabled-state overlay (red/green) bleeding onto unallocated slots.
          // Leave free chunks with their dark L3 fill.
          if (occ[i] !== 1) {
            continue;
          }
          this.drawChunkContent(ctx, detail, gridSubRect(area, cols, rows, i), i);
        }
      }

      // Per-chunk index labels once chunks are large enough to carry text (proposal 4). An L3 affordance: it
      // fades out as the L4 decoded content fades in, so the cell shows its index at L3 and its content at L4.
      const labelAlpha = l3Alpha * (1 - l4Alpha);
      if (chunkPx >= CHUNK_LABEL_MIN_PX && labelAlpha > 0.01) {
        this.drawChunkLabels(ctx, detail, area, cols, rows, labelAlpha);
      }
      ctx.globalAlpha = 1;
    }
  }

  /** Draws one chunk's L4 decoded content cells, or the unknown tile when the chunk has no typed decode. */
  private drawChunkContent(ctx: CanvasRenderingContext2D, detail: DbPageDetail, chunkRect: Rect, chunkInPage: number): void {
    // Cull chunks well below a few pixels — their content cells would be sub-pixel.
    if (chunkRect.w * this._camera.scale < 8) {
      return;
    }
    const key = `${detail.ownerSegmentId}:${detail.firstChunkId + chunkInPage}`;
    const content = this._chunkContents.get(key);
    if (!content) {
      return;
    }
    // VSBS / string / hashmap / index chunks carry no meaningful sub-grid — their L4 value is the Detail-panel inspector
    // (chain link, element / byte counts, string preview, bucket stats, node leaf/internal + links). Keep the L3 colouring.
    if (INSPECTOR_ONLY_DECODERS.has(content.decoder)) {
      return;
    }
    // Ease the content in from its arrival time on top of the caller's l4Alpha, so a late decode fades rather than pops.
    const baseAlpha = ctx.globalAlpha;
    const fade = this.contentFadeAlpha(key);
    if (fade <= 0) {
      return;
    }
    ctx.globalAlpha = baseAlpha * fade;
    if (content.decoder === 'unknown' || content.cells.length === 0) {
      this.drawUnknownTile(ctx, chunkRect);
      ctx.globalAlpha = baseAlpha;
      return;
    }
    const ccols = gridCols(content.cells.length);
    const crows = Math.ceil(content.cells.length / ccols);
    // Per-component overlay (A6): for cluster entity slots of the overlaid segment, colour by the selected component's
    // enabled bit instead of plain occupancy. Bit position = the picker's component slot index.
    const overlay = this._componentOverlay;
    const overlayActive = overlay != null && overlay.segmentId === detail.ownerSegmentId;
    for (let j = 0; j < content.cells.length; j++) {
      const cell = content.cells[j];
      let color: Rgb;
      if (overlayActive && cell.kind === 'entitySlot') {
        const enabled = ((cell.enabledMask ?? 0) & (1 << overlay!.componentSlot)) !== 0;
        color = enabledOverlayRgb(cell.colorKey > 0, enabled);
      } else {
        color = contentCellRgb(cell.kind, cell.colorKey);
      }
      ctx.fillStyle = rgb(color);
      this.fillWorldRect(ctx, gridSubRect(chunkRect, ccols, crows, j));
    }
    ctx.globalAlpha = baseAlpha;
  }

  /**
   * Draws the byte-proportional overhead band at the top of a chunk-based page cell, split into its three distinct
   * parts (A6) so the surface maps the page's memory honestly: the fixed per-page header (backslash hatch, every
   * page), the root-only segment directory / page-index table (slash hatch), and the stride-alignment padding
   * (X-crosshatch dead space — the bytes the engine wastes to stride-align chunk 0, which is why a large-stride
   * segment shows a wider band). The chunk grid is laid out below all three (see {@link chunkAreaRect}). Drawn at
   * l3Alpha; the L1 header strip covers lower zooms and fades out as L3 takes over.
   */
  private drawOverheadBands(ctx: CanvasRenderingContext2D, page: number, pageRect: Rect, detail: DbPageDetail, alpha: number): void {
    if (!this._data) {
      return;
    }
    const headerBytes = detail.headerBytes ?? 0;
    const dirBytes = detail.directoryBytes ?? 0;
    const padBytes = detail.paddingBytes ?? 0;
    if (headerBytes <= 0 && dirBytes <= 0 && padBytes <= 0) {
      return;
    }
    const cam = this._camera;
    const sx = worldToScreenX(cam, pageRect.x);
    const sy = worldToScreenY(cam, pageRect.y);
    const w = pageRect.w * cam.scale;
    const cellH = pageRect.h * cam.scale;
    const headerH = (cellH * headerBytes) / PAGE_SIZE;
    const dirH = (cellH * dirBytes) / PAGE_SIZE;
    const padH = (cellH * padBytes) / PAGE_SIZE;
    const c = PAGE_TYPE_RGB[this._data.pageType[page]] ?? PAGE_TYPE_RGB[DbPageType.Unknown];
    const color = `rgb(${c[0]},${c[1]},${c[2]})`;
    // Bands stack in byte order: header, then root-only directory, then alignment padding (dead space).
    this.hatchBand(ctx, sx, sy, w, headerH, color, '\\', alpha);
    this.hatchBand(ctx, sx, sy + headerH, w, dirH, color, '/', alpha);
    if (padH >= 1) {
      this.drawInvalidChunkCell(ctx, sx, sy + headerH + dirH, w, padH, alpha);
    }
  }

  /** Fills a band with `color` then overlays clipped diagonal hatch lines leaning `dir` ('\\' or '/'). */
  private hatchBand(ctx: CanvasRenderingContext2D, sx: number, sy: number, w: number, h: number, color: string, dir: '\\' | '/', alpha: number): void {
    if (h < 1 || w < 1) {
      return;
    }
    ctx.save();
    ctx.globalAlpha = alpha;
    ctx.fillStyle = color;
    ctx.fillRect(sx, sy, w, h);
    ctx.beginPath();
    ctx.rect(sx, sy, w, h);
    ctx.clip();
    ctx.strokeStyle = this._theme.background;
    ctx.lineWidth = 1;
    ctx.globalAlpha = alpha * 0.7;
    ctx.beginPath();
    for (let d = -h; d < w; d += HEADER_HATCH_SPACING) {
      if (dir === '\\') {
        ctx.moveTo(sx + d, sy);
        ctx.lineTo(sx + d + h, sy + h);
      } else {
        ctx.moveTo(sx + d, sy + h);
        ctx.lineTo(sx + d + h, sy);
      }
    }
    ctx.stroke();
    ctx.restore();
  }

  /**
   * Draws an occupancy page as a mini allocation-map of the file-page range it governs (A6, §10.2) — a
   * map-within-the-map. Each cell is a down-sampled sub-range of the governed pages coloured by its allocated
   * fraction (the same fill ramp the L3 chunk fill uses), so the occupancy page becomes a legend for the file's
   * allocation, read from the inside. Fades in with the chunk band (l3Alpha); the page contour is drawn by
   * `drawPageContours` like every other visible page.
   */
  private drawOccupancyMap(ctx: CanvasRenderingContext2D, pageRect: Rect, detail: DbPageDetail, alpha: number): void {
    const map = detail.occupancyMap;
    if (!map || map.length === 0) {
      return;
    }
    const cols = detail.occupancyGridCols && detail.occupancyGridCols > 0 ? detail.occupancyGridCols : gridCols(map.length);
    const rows = Math.ceil(map.length / cols);
    ctx.globalAlpha = alpha;
    for (let i = 0; i < map.length; i++) {
      // Allocated → cyan, free → dark slate — the file's used/free palette (§10.2), not the fill heatmap.
      ctx.fillStyle = rgb(allocationRgb(map[i] / 255));
      this.fillWorldRect(ctx, gridSubRect(pageRect, cols, rows, i));
    }
    this.drawChunkGridLines(ctx, pageRect, cols, rows, alpha);
    ctx.globalAlpha = 1;
  }

  /**
   * Strokes a 2 px contour around every visible page at L3+. The internal chunk gridlines are intentionally
   * faint (so a dense grid stays legible), which makes it hard to tell where one page's chunks end and the
   * next begins — this stronger boundary delineates the page itself. Fades in with the chunk band (l3Alpha)
   * and uses the theme text colour so it reads against both the occupancy fills and the L4 content cells.
   */
  private drawPageContours(ctx: CanvasRenderingContext2D, alpha: number): void {
    if (!this._layout) {
      return;
    }
    const pages = this.visiblePageList();
    if (pages.length === 0) {
      return;
    }
    const cam = this._camera;
    const size = cam.scale;
    ctx.save();
    ctx.globalAlpha = alpha;
    ctx.strokeStyle = this._theme.text;
    ctx.lineWidth = 2;
    for (const page of pages) {
      const { x, y } = hilbertD2XY(this._layout.order, page);
      const sx = worldToScreenX(cam, this._layout.dataRect.x + x);
      const sy = worldToScreenY(cam, this._layout.dataRect.y + y);
      ctx.strokeRect(sx, sy, size, size);
    }
    ctx.restore();
  }

  /**
   * Renders one chunk-grid cell as "invalid area" — a background fill overlaid with an X crosshatch (the same
   * cue {@link drawTailHatch} uses for cells outside the file). Used for the surplus cells of a page's
   * near-square chunk grid (indices ≥ chunkTotal): they are not real chunks, so the fill hides the page's base
   * colour leaking through and the X marks the cell as non-data. Fades with the chunk band (`alpha` = l3Alpha).
   */
  private drawInvalidChunkCell(ctx: CanvasRenderingContext2D, sx: number, sy: number, w: number, h: number, alpha: number): void {
    ctx.save();
    ctx.globalAlpha = alpha;
    ctx.fillStyle = this._theme.background;
    ctx.fillRect(sx, sy, w, h);
    ctx.globalAlpha = alpha * 0.32;
    ctx.strokeStyle = this._theme.mutedText;
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(sx, sy);
    ctx.lineTo(sx + w, sy + h);
    ctx.moveTo(sx + w, sy);
    ctx.lineTo(sx, sy + h);
    ctx.stroke();
    ctx.restore();
  }

  /**
   * Marks reclaimable holes (proposal 3) — free chunks *before* the fill frontier — with an amber diagonal,
   * the same hue used for the pathology / fragmentation signal. Free chunks after the frontier are the page's
   * growth headroom and stay unmarked, so a glance distinguishes "fragmented" from "simply not full yet".
   * Batched into one path per page.
   */
  private drawChunkHoles(
    ctx: CanvasRenderingContext2D,
    occ: Uint8Array,
    pageRect: Rect,
    cols: number,
    rows: number,
    lastOccupied: number,
    alpha: number,
  ): void {
    if (lastOccupied <= 0) {
      return;
    }
    const cam = this._camera;
    ctx.save();
    ctx.globalAlpha = alpha * 0.5;
    ctx.strokeStyle = PATHOLOGY_COLOR;
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let i = 0; i < lastOccupied; i++) {
      if (occ[i] === 1) {
        continue;
      }
      const sub = gridSubRect(pageRect, cols, rows, i);
      const sx = worldToScreenX(cam, sub.x);
      const sy = worldToScreenY(cam, sub.y);
      ctx.moveTo(sx, sy);
      ctx.lineTo(sx + sub.w * cam.scale, sy + sub.h * cam.scale);
    }
    ctx.stroke();
    ctx.restore();
  }

  /**
   * Per-kind chunk class markers (A6, design §10.1). Two overlays on the L3 grid: a muted backslash hatch over the
   * non-data (hashmap meta / directory) chunks so structure never reads as data, and an amber corner dot on each
   * overflowing chunk (an overflow chunk, or a primary bucket that chains) — the hash-collision-pressure signal.
   */
  private drawChunkClassMarkers(
    ctx: CanvasRenderingContext2D,
    detail: DbPageDetail,
    occ: Uint8Array,
    area: Rect,
    cols: number,
    rows: number,
    alpha: number,
  ): void {
    const chunkClass = detail.chunkClass;
    if (!chunkClass) {
      return;
    }
    const cam = this._camera;

    // Non-data chunks: muted backslash hatch (batched into one path).
    ctx.save();
    ctx.globalAlpha = alpha * 0.4;
    ctx.strokeStyle = this._theme.mutedText;
    ctx.lineWidth = 1;
    ctx.beginPath();
    let hasHatch = false;
    for (let i = 0; i < detail.chunkTotal; i++) {
      if (occ[i] !== 1 || chunkClass[i] !== DbChunkClass.NonData) {
        continue;
      }
      hasHatch = true;
      const sub = gridSubRect(area, cols, rows, i);
      const sx = worldToScreenX(cam, sub.x);
      const sy = worldToScreenY(cam, sub.y);
      ctx.moveTo(sx, sy);
      ctx.lineTo(sx + sub.w * cam.scale, sy + sub.h * cam.scale);
    }
    if (hasHatch) {
      ctx.stroke();
    }
    ctx.restore();

    // Overflow chunks: red corner dot with a dark ring. Red (not amber) so it stays visible on the amber fill a
    // full overflowing bucket paints; the ring keeps it legible on a dark/blue fill too.
    ctx.save();
    ctx.globalAlpha = alpha;
    ctx.fillStyle = OVERFLOW_DOT_COLOR;
    ctx.strokeStyle = 'rgba(0, 0, 0, 0.55)';
    ctx.lineWidth = 1;
    for (let i = 0; i < detail.chunkTotal; i++) {
      if (occ[i] !== 1 || chunkClass[i] !== DbChunkClass.Overflow) {
        continue;
      }
      const sub = gridSubRect(area, cols, rows, i);
      const w = sub.w * cam.scale;
      const h = sub.h * cam.scale;
      // Cap the dot radius at 6px so it stays a marker, not a blob, at deep zoom; 5px inset from the top-right corner.
      const margin = 5;
      const r = Math.min(6, Math.max(1.5, Math.min(w, h) * 0.16));
      const cx = worldToScreenX(cam, sub.x) + w - r - margin;
      const cy = worldToScreenY(cam, sub.y) + r + margin;
      ctx.beginPath();
      ctx.arc(cx, cy, r, 0, Math.PI * 2);
      ctx.fill();
      if (r >= 2) {
        ctx.stroke();
      }
    }
    ctx.restore();
  }

  /**
   * Draws each chunk's `#globalId [inPageIndex]` label, centred, once the chunks are large enough to carry it
   * (proposal 4) — the same identity the hover tooltip shows. Each cell is fit-checked against its width, so a
   * label is skipped when it would overflow rather than spilling into neighbours; short ids therefore appear at
   * a shallower zoom than long ones.
   */
  private drawChunkLabels(
    ctx: CanvasRenderingContext2D,
    detail: DbPageDetail,
    pageRect: Rect,
    cols: number,
    rows: number,
    alpha: number,
  ): void {
    const cam = this._camera;
    ctx.save();
    // Match the per-cell labels: heavy text on a semitransparent plate so the id reads over any chunk fill.
    ctx.font = '700 12px sans-serif';
    for (let i = 0; i < detail.chunkTotal; i++) {
      const sub = gridSubRect(pageRect, cols, rows, i);
      const w = sub.w * cam.scale;
      const label = `#${detail.firstChunkId + i} [${i}]`;
      if (ctx.measureText(label).width > w - 6) {
        continue;
      }
      const sx = Math.round(worldToScreenX(cam, sub.x) + w / 2);
      const sy = Math.round(worldToScreenY(cam, sub.y) + (sub.h * cam.scale) / 2);
      this.drawPlatedLabel(ctx, label, sx, sy, 'center', alpha);
    }
    ctx.restore();
  }

  /** Outlines a single chunk (page + in-page index) — the L3 hover / selection marker (proposal 2). */
  private drawChunkHighlight(
    ctx: CanvasRenderingContext2D,
    hit: { page: number; chunkInPage: number },
    color: string,
    width: number,
  ): void {
    const sub = this.chunkWorldRect(hit);
    if (!sub) {
      return;
    }
    this.strokeWorldRectOutline(ctx, sub, color, width);
  }

  /** Outlines one decoded content cell (an entity slot / field cell) within its chunk at L4 — the finest-grained marker. */
  private drawContentCellHighlight(
    ctx: CanvasRenderingContext2D,
    hit: { page: number; chunkInPage: number; cellIndex: number },
    color: string,
    width: number,
  ): void {
    const sub = this.contentCellWorldRect(hit);
    if (!sub) {
      return;
    }
    this.strokeWorldRectOutline(ctx, sub, color, width);
  }

  /** Strokes a world-space rect's screen projection with a 1px-inflated coloured outline (shared by the chunk / cell highlights). */
  private strokeWorldRectOutline(ctx: CanvasRenderingContext2D, rect: Rect, color: string, width: number): void {
    const cam = this._camera;
    const sx = worldToScreenX(cam, rect.x);
    const sy = worldToScreenY(cam, rect.y);
    ctx.save();
    ctx.strokeStyle = color;
    ctx.lineWidth = width;
    ctx.strokeRect(sx - 0.5, sy - 0.5, rect.w * cam.scale + 1, rect.h * cam.scale + 1);
    ctx.restore();
  }

  /** Draws the internal gridlines of a `cols × rows` chunk grid inside a page rect. */
  private drawChunkGridLines(ctx: CanvasRenderingContext2D, pageRect: Rect, cols: number, rows: number, alpha: number): void {
    const sx = worldToScreenX(this._camera, pageRect.x);
    const sy = worldToScreenY(this._camera, pageRect.y);
    const sw = pageRect.w * this._camera.scale;
    const sh = pageRect.h * this._camera.scale;
    if (sw / cols < 4) {
      return;
    }
    ctx.save();
    ctx.globalAlpha = alpha * 0.5;
    ctx.strokeStyle = this._theme.mutedText;
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let c = 1; c < cols; c++) {
      const x = sx + (c / cols) * sw;
      ctx.moveTo(x, sy);
      ctx.lineTo(x, sy + sh);
    }
    for (let r = 1; r < rows; r++) {
      const y = sy + (r / rows) * sh;
      ctx.moveTo(sx, y);
      ctx.lineTo(sx + sw, y);
    }
    ctx.stroke();
    ctx.restore();
  }

  /** A distinct hatched tile for regions the engine can locate but not classify / decode (§3.4) — never blank. */
  private drawUnknownTile(ctx: CanvasRenderingContext2D, r: Rect): void {
    const sx = worldToScreenX(this._camera, r.x);
    const sy = worldToScreenY(this._camera, r.y);
    const sw = r.w * this._camera.scale;
    const sh = r.h * this._camera.scale;
    ctx.fillStyle = rgb(TAIL_RGB);
    ctx.fillRect(sx, sy, sw, sh);
    ctx.save();
    ctx.beginPath();
    ctx.rect(sx, sy, sw, sh);
    ctx.clip();
    ctx.strokeStyle = this._theme.mutedText;
    ctx.lineWidth = 1;
    ctx.globalAlpha = 0.5;
    for (let d = -sh; d < sw; d += 8) {
      ctx.beginPath();
      ctx.moveTo(sx + d, sy);
      ctx.lineTo(sx + d + sh, sy + sh);
      ctx.stroke();
    }
    ctx.restore();
  }

  private fillWorldRect(ctx: CanvasRenderingContext2D, r: Rect): void {
    ctx.fillRect(
      worldToScreenX(this._camera, r.x),
      worldToScreenY(this._camera, r.y),
      r.w * this._camera.scale,
      r.h * this._camera.scale,
    );
  }

  private strokeWorldRect(ctx: CanvasRenderingContext2D, r: Rect): void {
    ctx.strokeRect(
      worldToScreenX(this._camera, r.x) + 0.5,
      worldToScreenY(this._camera, r.y) + 0.5,
      r.w * this._camera.scale,
      r.h * this._camera.scale,
    );
  }

  private drawWalLabel(ctx: CanvasRenderingContext2D, walRect: Rect): void {
    const screenW = walRect.w * this._camera.scale;
    if (screenW < 24) {
      return;
    }
    ctx.save();
    ctx.fillStyle = this._theme.mutedText;
    ctx.font = '10px sans-serif';
    ctx.textAlign = 'center';
    ctx.translate(
      worldToScreenX(this._camera, walRect.x + walRect.w / 2),
      worldToScreenY(this._camera, walRect.y + walRect.h / 2),
    );
    ctx.rotate(-Math.PI / 2);
    ctx.fillText('WAL', 0, 0);
    ctx.restore();
  }

  /**
   * Subtle diagonal mark on every Free page — a single top-left → bottom-right line per cell, in the theme
   * muted colour at low alpha. Tells the user "this is an intentionally empty page" so the FREE_RGB dark
   * slate isn't mistaken for "an encoding colour I don't recognise". Iterates the full visible cell rect
   * (not the MAX_VISIBLE_PAGES-capped {@link visiblePageList}) so the pattern stays visible at moderate
   * zoom where free runs cover hundreds of cells.
   */
  private drawFreePagesHatch(ctx: CanvasRenderingContext2D, alpha: number): void {
    if (!this._layout || !this._data) {
      return;
    }
    const rect = this.visibleCellRect();
    if (!rect) {
      return;
    }
    const cell = this._camera.scale;
    const { order, pageCount, dataRect } = this._layout;
    const types = this._data.pageType;
    const cam = this._camera;
    // One stroke per frame covering every free cell — building one path is much cheaper than N strokes.
    ctx.save();
    ctx.globalAlpha = alpha * 0.32;
    ctx.strokeStyle = this._theme.mutedText;
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let cy = rect.cy0; cy <= rect.cy1; cy++) {
      for (let cx = rect.cx0; cx <= rect.cx1; cx++) {
        const page = hilbertXY2D(order, cx, cy);
        if (page < 0 || page >= pageCount) {
          continue;
        }
        if (types[page] !== DbPageType.Free) {
          continue;
        }
        const sx = worldToScreenX(cam, dataRect.x + cx);
        const sy = worldToScreenY(cam, dataRect.y + cy);
        ctx.moveTo(sx, sy);
        ctx.lineTo(sx + cell, sy + cell);
      }
    }
    ctx.stroke();
    ctx.restore();
  }

  /**
   * Crosshatch overlay for the Hilbert tail — cells beyond `pageCount` that aren't part of the file at all.
   * Pairs with the transparent tail in {@link paintOffscreen}: the canvas background shows through, and this
   * crosshatch (two-direction diagonals, distinct from the free-page single diagonal) labels the region as
   * "outside the file extent". Eliminates the long-standing confusion where the tail's dark slate looked
   * identical to FREE_RGB and made the file look mostly free when it wasn't.
   */
  private drawTailHatch(ctx: CanvasRenderingContext2D, alpha: number): void {
    if (!this._layout) {
      return;
    }
    const rect = this.visibleCellRect();
    if (!rect) {
      return;
    }
    const cell = this._camera.scale;
    const { order, pageCount, dataRect } = this._layout;
    const cam = this._camera;
    ctx.save();
    ctx.globalAlpha = alpha * 0.32;
    ctx.strokeStyle = this._theme.mutedText;
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let cy = rect.cy0; cy <= rect.cy1; cy++) {
      for (let cx = rect.cx0; cx <= rect.cx1; cx++) {
        const page = hilbertXY2D(order, cx, cy);
        // Skip real pages — tail = grid cells whose Hilbert index is past pageCount.
        if (page >= 0 && page < pageCount) {
          continue;
        }
        const sx = worldToScreenX(cam, dataRect.x + cx);
        const sy = worldToScreenY(cam, dataRect.y + cy);
        // Crosshatch: '\' and '/' diagonals — two lines per cell, both in the same batched path.
        ctx.moveTo(sx, sy);
        ctx.lineTo(sx + cell, sy + cell);
        ctx.moveTo(sx + cell, sy);
        ctx.lineTo(sx, sy + cell);
      }
    }
    ctx.stroke();
    ctx.restore();
  }

  /**
   * Draws a per-page hatched header strip at the top of each cell, strictly to scale (§3.4). The strip's
   * height is `cell × 192/8192 ≈ 2.34%` of the cell, filled with the page-type colour and overlaid with
   * diagonal hatch lines. The hatch convention reads as "engine overhead, not user-addressable data" — the
   * same visual cue tools like SpaceSniffer use for system/metadata zones. The hatch (not the colour) is
   * the differentiator, so the split stays visible even when the body encoding is `pageType` (i.e. body and
   * strip share a colour). Viewport-culled (bounded by MAX_VISIBLE_PAGES) and gated on HEADER_STRIP_MIN_CELL
   * so the strip is never sub-pixel.
   */
  private drawPageHeaderHatch(ctx: CanvasRenderingContext2D, alpha: number): void {
    if (!this._layout || !this._data) {
      return;
    }
    const pages = this.visiblePageList();
    if (pages.length === 0) {
      return;
    }
    const cell = this._camera.scale;
    const stripH = cell * HEADER_RATIO;
    if (stripH < 1) {
      return;
    }
    const types = this._data.pageType;
    ctx.save();
    ctx.globalAlpha = alpha;
    let lastType = -1;
    for (const page of pages) {
      const type = types[page];
      const { x, y } = hilbertD2XY(this._layout.order, page);
      const sx = worldToScreenX(this._camera, this._layout.dataRect.x + x);
      const sy = worldToScreenY(this._camera, this._layout.dataRect.y + y);
      // Strip fill in the page-type colour — preserves the type indicator while signalling "this is the
      // overhead band".
      if (type !== lastType) {
        const c = PAGE_TYPE_RGB[type] ?? PAGE_TYPE_RGB[DbPageType.Unknown];
        ctx.fillStyle = `rgb(${c[0]},${c[1]},${c[2]})`;
        lastType = type;
      }
      ctx.fillRect(sx, sy, cell, stripH);
      // Hatch — diagonal lines in the theme background colour, clipped to the strip so they don't bleed
      // into the body. One save/restore per cell to scope the clip; ~256 visible cells max, fast enough.
      ctx.save();
      ctx.beginPath();
      ctx.rect(sx, sy, cell, stripH);
      ctx.clip();
      ctx.strokeStyle = this._theme.background;
      ctx.lineWidth = 1;
      ctx.globalAlpha = alpha * 0.7;
      ctx.beginPath();
      for (let d = -stripH; d < cell; d += HEADER_HATCH_SPACING) {
        ctx.moveTo(sx + d, sy);
        ctx.lineTo(sx + d + stripH, sy + stripH);
      }
      ctx.stroke();
      ctx.restore();
    }
    ctx.restore();
  }

  /**
   * Per-run segment labels. One label per RLE run (a page-index-contiguous block of a single segment), placed
   * on the run's own cell centroid — so a label never floats over cells it doesn't own (a per-segment centroid
   * lies for scattered segments). Default text is `SegName k/M`, where M is the segment's total run count over
   * the whole file and k is this run's order; the `k/M` counter shows only when M > 1, so a contiguous segment
   * reads as a bare name and a fragmented one reads as `CompA 1/3 … 2/3 … 3/3` across its blocks. With the
   * region-captions toggle ('c') the label switches to the verbose `Type · Name · N pages · size`. LOD-gated:
   * a run is labelled only when its on-screen blob is big enough to fit text, so small scattered fragments stay
   * colour-only (the hover tooltip still identifies them).
   */
  private drawRunLabels(ctx: CanvasRenderingContext2D, alpha: number): void {
    if (!this._layout || !this._data || this._regions.length === 0) {
      return;
    }
    const rect = this.visibleCellRect();
    if (!rect) {
      return;
    }
    const span = this.visiblePageSpanInclusive(rect);
    if (!span) {
      return;
    }
    const { order, pageCount, dataRect } = this._layout;
    const cam = this._camera;
    const verbose = this._regionCaptions;

    const segMeta = new Map<number, { kind: string; typeName: string }>();
    for (const s of this._data.segments) {
      segMeta.set(s.id, { kind: s.kind, typeName: s.typeName });
    }

    // M = total runs per segment over the whole file (stable as the viewport pans).
    const runTotal = new Map<number, number>();
    for (const r of this._regions) {
      if (r.ownerSegmentId !== NO_SEGMENT) {
        runTotal.set(r.ownerSegmentId, (runTotal.get(r.ownerSegmentId) ?? 0) + 1);
      }
    }

    interface RunDraw {
      cx: number;
      cy: number;
      maxWidth: number;
      label: string;
      weight: number;
    }
    const draws: RunDraw[] = [];
    // k = per-segment running index by page order, advanced for every run (even off-screen) so it stays stable.
    const runIndex = new Map<number, number>();
    for (const region of this._regions) {
      if (region.ownerSegmentId === NO_SEGMENT) {
        continue; // free / unowned runs carry no segment label
      }
      const k = (runIndex.get(region.ownerSegmentId) ?? 0) + 1;
      runIndex.set(region.ownerSegmentId, k);

      const end = region.startPage + region.pageCount - 1;
      if (end < span.min || region.startPage > span.max) {
        continue; // run not in the visible page span
      }

      // Anchor the label on the run's widest *row* — the Hilbert row (constant y) with the largest X extent.
      // A horizontal label then sits on a solid strip of cells the run actually owns and gets that strip's
      // width as its budget, instead of a bbox centroid that can land between scattered cells. Sample-capped
      // so a huge run stays cheap; one entry per visited row.
      const rowsX = new Map<number, { minX: number; maxX: number }>();
      const step = Math.max(1, Math.floor(region.pageCount / 2048));
      for (let p = region.startPage; p <= end; p += step) {
        if (p < 0 || p >= pageCount) {
          continue;
        }
        const { x, y } = hilbertD2XY(order, p);
        const row = rowsX.get(y);
        if (!row) {
          rowsX.set(y, { minX: x, maxX: x });
        } else {
          if (x < row.minX) row.minX = x;
          if (x > row.maxX) row.maxX = x;
        }
      }
      if (rowsX.size === 0) {
        continue;
      }
      let bestY = 0;
      let bestMinX = 0;
      let bestMaxX = 0;
      let bestSpan = -1;
      for (const [y, row] of rowsX) {
        const spanX = row.maxX - row.minX;
        if (spanX > bestSpan) {
          bestSpan = spanX;
          bestY = y;
          bestMinX = row.minX;
          bestMaxX = row.maxX;
        }
      }
      const widthPx = (bestMaxX - bestMinX + 1) * cam.scale;
      if (widthPx < SEGMENT_LABEL_MIN_BBOX_PX) {
        continue; // widest row still too narrow on screen to carry a label
      }

      const meta = segMeta.get(region.ownerSegmentId);
      const name = meta
        ? meta.typeName.length > 0
          ? meta.typeName
          : `${meta.kind} #${region.ownerSegmentId}`
        : `segment #${region.ownerSegmentId}`;
      const m = runTotal.get(region.ownerSegmentId) ?? 1;
      const label = verbose
        ? `${PAGE_TYPE_LABELS[region.pageType] ?? 'Unknown'} · ${name} · ${region.pageCount.toLocaleString()} pages · ${formatFileSize(region.byteSize)}`
        : m > 1
          ? `${name} ${k}/${m}`
          : name;

      draws.push({
        cx: worldToScreenX(cam, dataRect.x + (bestMinX + bestMaxX + 1) / 2),
        cy: worldToScreenY(cam, dataRect.y + bestY + 0.5),
        maxWidth: widthPx,
        label,
        weight: region.pageCount,
      });
    }
    if (draws.length === 0) {
      return;
    }
    // Biggest blobs first, so a small run's pill draws on top and stays readable.
    draws.sort((a, b) => b.weight - a.weight);

    ctx.save();
    ctx.globalAlpha = alpha;
    ctx.font = '11px sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    const padX = 6;
    const pillH = 16;
    for (const d of draws) {
      const truncated = truncateToWidth(ctx, d.label, Math.max(0, d.maxWidth - padX * 2));
      const w = ctx.measureText(truncated).width + padX * 2;
      ctx.fillStyle = this._theme.surface;
      ctx.globalAlpha = alpha * 0.88;
      roundedRect(ctx, d.cx - w / 2, d.cy - pillH / 2, w, pillH, 3);
      ctx.fill();
      ctx.strokeStyle = this._theme.border;
      ctx.lineWidth = 1;
      ctx.stroke();
      ctx.globalAlpha = alpha;
      ctx.fillStyle = this._theme.text;
      ctx.fillText(truncated, d.cx, d.cy);
    }
    ctx.restore();
  }

  /**
   * Draws a label on a semitransparent background plate so it stays legible over any cell colour. The plate carries
   * the contrast, so the text is a plain fill (crisp — no stroke distorting the glyphs). `ctx.font` must already be
   * set (used for both measuring and drawing); coordinates are expected pixel-snapped by the caller.
   */
  private drawPlatedLabel(
    ctx: CanvasRenderingContext2D,
    text: string,
    x: number,
    cy: number,
    align: 'left' | 'center',
    alpha: number,
  ): void {
    ctx.textAlign = align;
    ctx.textBaseline = 'alphabetic';
    const m = ctx.measureText(text);
    const tw = m.width;
    // Centre on the actual glyph box, not the em box — digits sit high in the em, which left the plate bottom-heavy.
    const ascent = m.actualBoundingBoxAscent ?? 9;
    const descent = m.actualBoundingBoxDescent ?? 2;
    const glyphH = ascent + descent;
    const padX = 3;
    const padY = 2;
    const left = align === 'center' ? x - tw / 2 : x;
    ctx.globalAlpha = alpha * 0.55;
    ctx.fillStyle = this._theme.background;
    roundedRect(ctx, Math.round(left - padX), Math.round(cy - glyphH / 2 - padY), Math.round(tw + padX * 2), Math.round(glyphH + padY * 2), 2);
    ctx.fill();
    ctx.globalAlpha = alpha;
    ctx.fillStyle = this._theme.text;
    ctx.fillText(text, x, Math.round(cy + (ascent - descent) / 2));
  }

  /**
   * Progressive per-cell labels. Tier 1 (cell ≥ 50 px) — a tiny segment-id badge in the top-left corner.
   * Tier 2 (cell ≥ 100 px) — the page index centred under the cell's mid-line. Both fade with `alpha`.
   */
  private drawCellLabels(ctx: CanvasRenderingContext2D, alpha: number): void {
    if (!this._layout || !this._data) {
      return;
    }
    const pages = this.visiblePageList();
    if (pages.length === 0) {
      return;
    }
    const cell = this._camera.scale;
    const showIndex = cell >= CELL_LABEL_INDEX_MIN_CELL;
    const owners = this._data.ownerSegmentId;
    ctx.save();
    for (const page of pages) {
      const { x, y } = hilbertD2XY(this._layout.order, page);
      const sx = worldToScreenX(this._camera, this._layout.dataRect.x + x);
      const sy = worldToScreenY(this._camera, this._layout.dataRect.y + y);
      // Segment-id badge — top-left corner, monospace. Drawn once per contiguous run (on the run's first page, i.e.
      // where the owner changes from the previous page) rather than on every page: a multi-page segment otherwise
      // stamps the same #id across all its cells, which reads as noise. One badge per run-fragment still lets every
      // scattered block self-identify, and pairs with the per-run name pill (drawRunLabels).
      const sid = owners[page];
      if (sid !== NO_SEGMENT && (page === 0 || owners[page - 1] !== sid)) {
        ctx.font = '700 11px monospace';
        this.drawPlatedLabel(ctx, `#${sid}`, Math.round(sx + 4), Math.round(sy + 9), 'left', alpha);
      }
      if (showIndex) {
        ctx.font = '700 12px sans-serif';
        this.drawPlatedLabel(ctx, page.toLocaleString(), Math.round(sx + cell / 2), Math.round(sy + cell / 2), 'center', alpha);
      }
    }
    ctx.restore();
  }

  /**
   * Persistent per-page corner markers — three independent signals overlaid on the L1 image:
   *
   * - **Top-right CRC-failure triangle** (red) — drawn when a page's live CRC has come back as `Failed`.
   *   Always rendered when the detail tier is resident; cannot be suppressed.
   * - **Bottom-left pathology dot** (amber) — drawn for under-filled / leaked pages in {@link _pathologyPages}.
   *   Always rendered when flags are present.
   * - **Bottom-right residency dot** — green for `ResidentClean`, amber for `ResidentDirty`. Suppressed for
   *   `OnDiskOnly` to keep the visual quiet (most pages on a large DB are not in cache). Toggle via 'r'.
   *
   * All markers read across any base encoding so the user can never miss a critical signal because of their
   * colour-mode choice. Bounded by `MAX_VISIBLE_PAGES`.
   */
  private drawCornerMarkers(ctx: CanvasRenderingContext2D, alpha: number): void {
    if (!this._layout || !this._data) {
      return;
    }
    const pages = this.visiblePageList();
    if (pages.length === 0) {
      return;
    }
    const cell = this._camera.scale;
    const r = CORNER_MARKER_RADIUS;
    const inset = 3;
    const tri = Math.min(CRC_TRIANGLE_SIZE, cell / 3);
    const detailTileSize = this._data.detailTileSize;
    ctx.save();
    ctx.globalAlpha = alpha;
    for (const page of pages) {
      const { x, y } = hilbertD2XY(this._layout.order, page);
      const sx = worldToScreenX(this._camera, this._layout.dataRect.x + x);
      const sy = worldToScreenY(this._camera, this._layout.dataRect.y + y);

      // Look up the page's detail-tile slot once; null if not yet loaded — markers based on detail data are
      // simply suppressed for that page (we never show a stale or invented signal).
      const tile = this._tiles.get(Math.floor(page / detailTileSize));
      const slot = tile ? page - tile.firstPage : -1;
      const haveDetail = !!tile && slot >= 0 && slot < tile.pageCount;

      // CRC failure — top-right red triangle, the loudest signal.
      if (haveDetail && tile!.crcStatus[slot] === DbCrcStatus.Failed) {
        ctx.fillStyle = CRC_FAILED_COLOR;
        ctx.beginPath();
        ctx.moveTo(sx + cell - tri, sy);
        ctx.lineTo(sx + cell, sy);
        ctx.lineTo(sx + cell, sy + tri);
        ctx.closePath();
        ctx.fill();
      }

      // Pathology flag — bottom-left amber dot, only when the page is in the under-filled set.
      if (this._pathologyPages.has(page)) {
        ctx.fillStyle = PATHOLOGY_COLOR;
        ctx.beginPath();
        ctx.arc(sx + inset + r, sy + cell - inset - r, r, 0, Math.PI * 2);
        ctx.fill();
      }

      // Cache residency — bottom-right dot, suppressed for on-disk-only pages so most cells stay quiet.
      if (this._residencyOverlay && haveDetail) {
        const res = tile!.residency[slot];
        if (res === DbResidency.ResidentClean || res === DbResidency.ResidentDirty) {
          ctx.fillStyle = res === DbResidency.ResidentDirty ? RESIDENT_DIRTY_COLOR : RESIDENT_CLEAN_COLOR;
          ctx.beginPath();
          ctx.arc(sx + cell - inset - r, sy + cell - inset - r, r, 0, Math.PI * 2);
          ctx.fill();
        }
      }
    }
    ctx.restore();
  }

  /** Visible page-index span — convenience around {@link visiblePageSpan} that accepts a pre-computed rect. */
  private visiblePageSpanInclusive(rect: { cx0: number; cy0: number; cx1: number; cy1: number }): { min: number; max: number } | null {
    if (!this._layout) {
      return null;
    }
    const { order, pageCount } = this._layout;
    let min = pageCount;
    let max = -1;
    for (let cy = rect.cy0; cy <= rect.cy1; cy++) {
      for (let cx = rect.cx0; cx <= rect.cx1; cx++) {
        const page = hilbertXY2D(order, cx, cy);
        if (page >= 0 && page < pageCount) {
          if (page < min) min = page;
          if (page > max) max = page;
        }
      }
    }
    return max >= min ? { min, max } : null;
  }

  /**
   * Draws a barely-visible 1 px gridline at every page-cell boundary across the visible part of the Hilbert
   * square, so each page reads as a bordered cell. Viewport-culled (only the on-screen cell lines are drawn)
   * and gated on a minimum cell size so it never collapses into a solid mush when zoomed out.
   */
  private drawPageGrid(ctx: CanvasRenderingContext2D, alpha: number): void {
    if (!this._layout || this._camera.scale < GRID_MIN_CELL) {
      return;
    }
    const rect = this.visibleCellRect();
    if (!rect) {
      return;
    }
    const { dataRect } = this._layout;
    const cam = this._camera;
    const top = worldToScreenY(cam, dataRect.y + rect.cy0);
    const bottom = worldToScreenY(cam, dataRect.y + rect.cy1 + 1);
    const left = worldToScreenX(cam, dataRect.x + rect.cx0);
    const right = worldToScreenX(cam, dataRect.x + rect.cx1 + 1);

    ctx.save();
    ctx.globalAlpha = alpha * GRID_ALPHA;
    ctx.strokeStyle = this._theme.border;
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let cx = rect.cx0; cx <= rect.cx1 + 1; cx++) {
      const sx = Math.round(worldToScreenX(cam, dataRect.x + cx)) + 0.5;
      ctx.moveTo(sx, top);
      ctx.lineTo(sx, bottom);
    }
    for (let cy = rect.cy0; cy <= rect.cy1 + 1; cy++) {
      const sy = Math.round(worldToScreenY(cam, dataRect.y + cy)) + 0.5;
      ctx.moveTo(left, sy);
      ctx.lineTo(right, sy);
    }
    ctx.stroke();
    ctx.restore();
  }

  private drawSegmentOverlay(ctx: CanvasRenderingContext2D, alpha: number): void {
    if (!this._data || !this._layout) {
      return;
    }
    const { order, side, dataRect } = this._layout;
    const owner = this._data.ownerSegmentId;
    const pageCount = this._data.pageCount;
    const vis = visibleWorldRect(this._camera, this._cssW, this._cssH);
    const cx0 = Math.max(0, Math.floor(vis.x - dataRect.x));
    const cy0 = Math.max(0, Math.floor(vis.y - dataRect.y));
    const cx1 = Math.min(side - 1, Math.ceil(vis.x - dataRect.x + vis.w));
    const cy1 = Math.min(side - 1, Math.ceil(vis.y - dataRect.y + vis.h));

    ctx.save();
    ctx.strokeStyle = this._theme.text;
    ctx.lineWidth = 1;
    // Fade with the L1 page grid — the overlay is a page-grid annotation, so it tracks the grid's visibility.
    ctx.globalAlpha = 0.7 * alpha;
    const ownerAt = (cx: number, cy: number): number => {
      if (cx < 0 || cy < 0 || cx >= side || cy >= side) {
        return -1;
      }
      const page = hilbertXY2D(order, cx, cy);
      return page >= 0 && page < pageCount ? owner[page] : -1;
    };
    for (let cy = cy0; cy <= cy1; cy++) {
      for (let cx = cx0; cx <= cx1; cx++) {
        const here = ownerAt(cx, cy);
        const sx = worldToScreenX(this._camera, dataRect.x + cx);
        const sy = worldToScreenY(this._camera, dataRect.y + cy);
        if (here !== ownerAt(cx + 1, cy)) {
          ctx.beginPath();
          ctx.moveTo(sx + this._camera.scale, sy);
          ctx.lineTo(sx + this._camera.scale, sy + this._camera.scale);
          ctx.stroke();
        }
        if (here !== ownerAt(cx, cy + 1)) {
          ctx.beginPath();
          ctx.moveTo(sx, sy + this._camera.scale);
          ctx.lineTo(sx + this._camera.scale, sy + this._camera.scale);
          ctx.stroke();
        }
      }
    }
    ctx.restore();
  }

  private drawCellHighlight(ctx: CanvasRenderingContext2D, page: number | null, color: string, width: number, alpha = 1): void {
    if (page == null || !this._layout || page < 0 || page >= this._layout.pageCount) {
      return;
    }
    const { x, y } = hilbertD2XY(this._layout.order, page);
    const sx = worldToScreenX(this._camera, this._layout.dataRect.x + x);
    const sy = worldToScreenY(this._camera, this._layout.dataRect.y + y);
    const size = Math.max(this._camera.scale, 3);
    ctx.save();
    // Fade with the L1 page grid during the L0→L1 crossfade — the outline tracks the page cell it marks, so it
    // ramps in alongside the grid instead of popping at full opacity (matches the segment overlay).
    ctx.globalAlpha = alpha;
    ctx.strokeStyle = color;
    ctx.lineWidth = width;
    ctx.strokeRect(sx - 0.5, sy - 0.5, size + 1, size + 1);
    ctx.restore();
  }

  private drawMinimap(ctx: CanvasRenderingContext2D): void {
    if (!this._layout) {
      return;
    }
    const mm = this.getMinimapScreenRect();
    ctx.save();
    ctx.fillStyle = this._theme.background;
    ctx.fillRect(mm.x, mm.y, mm.w, mm.h);
    ctx.imageSmoothingEnabled = true;
    // The data file fills the minimap square; the WAL is omitted from the thumbnail for clarity.
    ctx.drawImage(this._offscreen, mm.x, mm.y, mm.w, mm.h);
    ctx.strokeStyle = this._theme.border;
    ctx.lineWidth = 1;
    ctx.strokeRect(mm.x + 0.5, mm.y + 0.5, mm.w, mm.h);

    // Viewport rectangle — the visible world region mapped into minimap space.
    const vis = visibleWorldRect(this._camera, this._cssW, this._cssH);
    const sx = layoutScale(vis.x, this._layout.worldBounds.w);
    const sy = layoutScale(vis.y, this._layout.worldBounds.h);
    const sw = layoutScale(vis.w, this._layout.worldBounds.w);
    const sh = layoutScale(vis.h, this._layout.worldBounds.h);
    ctx.strokeStyle = this._theme.accent;
    ctx.lineWidth = 1.5;
    ctx.strokeRect(
      mm.x + clamp01(sx) * mm.w,
      mm.y + clamp01(sy) * mm.h,
      Math.min(1, sw) * mm.w,
      Math.min(1, sh) * mm.h,
    );
    ctx.restore();
  }

  private drawOffsetStrip(ctx: CanvasRenderingContext2D): void {
    if (!this._layout) {
      return;
    }
    const strip = this.getOffsetStripScreenRect();
    ctx.save();
    ctx.fillStyle = this._theme.surface;
    ctx.fillRect(strip.x, strip.y, strip.w, strip.h);
    ctx.strokeStyle = this._theme.border;
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(strip.x, strip.y + 0.5);
    ctx.lineTo(strip.x + strip.w, strip.y + 0.5);
    ctx.stroke();

    // Brush — the page-index span currently visible (computed exactly when the visible cell set is small).
    const span = this.visiblePageSpan();
    if (span && this._layout.pageCount > 0) {
      const bx = (span.min / this._layout.pageCount) * strip.w;
      const bw = Math.max(2, ((span.max - span.min + 1) / this._layout.pageCount) * strip.w);
      ctx.fillStyle = this._theme.accent;
      ctx.globalAlpha = 0.5;
      ctx.fillRect(strip.x + bx, strip.y + 2, bw, strip.h - 4);
      ctx.globalAlpha = 1;
    }

    ctx.fillStyle = this._theme.mutedText;
    ctx.font = '9px sans-serif';
    ctx.textAlign = 'left';
    ctx.fillText('0', strip.x + 4, strip.y + 11);
    ctx.textAlign = 'right';
    ctx.fillText('EOF', strip.x + strip.w - 4, strip.y + 11);
    ctx.restore();
  }

  /** The visible cell rect in grid coordinates, clamped to the grid. */
  private visibleCellRect(): { cx0: number; cy0: number; cx1: number; cy1: number } | null {
    if (!this._layout) {
      return null;
    }
    const { side, dataRect } = this._layout;
    const vis = visibleWorldRect(this._camera, this._cssW, this._cssH);
    const cx0 = Math.max(0, Math.floor(vis.x - dataRect.x));
    const cy0 = Math.max(0, Math.floor(vis.y - dataRect.y));
    const cx1 = Math.min(side - 1, Math.ceil(vis.x - dataRect.x + vis.w));
    const cy1 = Math.min(side - 1, Math.ceil(vis.y - dataRect.y + vis.h));
    return cx1 >= cx0 && cy1 >= cy0 ? { cx0, cy0, cx1, cy1 } : null;
  }

  /** The bounding page-index span of currently visible cells, or null when the whole file is visible. */
  private visiblePageSpan(): { min: number; max: number } | null {
    if (!this._layout) {
      return null;
    }
    const rect = this.visibleCellRect();
    if (!rect) {
      return null;
    }
    const { order, pageCount } = this._layout;
    const cellCount = (rect.cx1 - rect.cx0 + 1) * (rect.cy1 - rect.cy0 + 1);
    if (cellCount >= 40000 || cellCount >= this._layout.side * this._layout.side) {
      return null;
    }
    let min = pageCount;
    let max = -1;
    for (let cy = rect.cy0; cy <= rect.cy1; cy++) {
      for (let cx = rect.cx0; cx <= rect.cx1; cx++) {
        const page = hilbertXY2D(order, cx, cy);
        if (page >= 0 && page < pageCount) {
          if (page < min) min = page;
          if (page > max) max = page;
        }
      }
    }
    return max >= min ? { min, max } : null;
  }

  /** The visible page indices, capped — the L3/L4 fetch + draw set. Empty when too zoomed out to be in L3. */
  private visiblePageList(): number[] {
    if (!this._layout) {
      return [];
    }
    const rect = this.visibleCellRect();
    if (!rect) {
      return [];
    }
    const { order, pageCount } = this._layout;
    const pages: number[] = [];
    for (let cy = rect.cy0; cy <= rect.cy1 && pages.length <= MAX_VISIBLE_PAGES; cy++) {
      for (let cx = rect.cx0; cx <= rect.cx1 && pages.length <= MAX_VISIBLE_PAGES; cx++) {
        const page = hilbertXY2D(order, cx, cy);
        if (page >= 0 && page < pageCount) {
          pages.push(page);
        }
      }
    }
    return pages.length > MAX_VISIBLE_PAGES ? [] : pages;
  }
}

function layoutScale(value: number, total: number): number {
  return total > 0 ? value / total : 0;
}

/** Traces a rounded rect path onto `ctx`; caller follows with `fill()` / `stroke()`. */
function roundedRect(ctx: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, r: number): void {
  const rr = Math.min(r, Math.min(w, h) / 2);
  ctx.beginPath();
  ctx.moveTo(x + rr, y);
  ctx.lineTo(x + w - rr, y);
  ctx.quadraticCurveTo(x + w, y, x + w, y + rr);
  ctx.lineTo(x + w, y + h - rr);
  ctx.quadraticCurveTo(x + w, y + h, x + w - rr, y + h);
  ctx.lineTo(x + rr, y + h);
  ctx.quadraticCurveTo(x, y + h, x, y + h - rr);
  ctx.lineTo(x, y + rr);
  ctx.quadraticCurveTo(x, y, x + rr, y);
  ctx.closePath();
}

/** Trims `text` with an `…` suffix until it fits within `maxPx` under the context's current font. */
function truncateToWidth(ctx: CanvasRenderingContext2D, text: string, maxPx: number): string {
  if (maxPx <= 0 || ctx.measureText(text).width <= maxPx) {
    return text;
  }
  const ellipsis = '…';
  let lo = 0;
  let hi = text.length;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (ctx.measureText(text.slice(0, mid) + ellipsis).width <= maxPx) {
      lo = mid;
    } else {
      hi = mid - 1;
    }
  }
  return lo === 0 ? ellipsis : text.slice(0, lo) + ellipsis;
}
