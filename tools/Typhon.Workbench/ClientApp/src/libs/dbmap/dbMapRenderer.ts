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
import { lodForScale, tileNodesForSpan, type DbLodState } from './dbMapLod';
import { gridCols, gridSubRect } from './dbMapGrid';
import {
  BYTE_CLASS_RGB,
  CRC_RGB,
  FREE_RGB,
  RESIDENCY_RGB,
  TAIL_RGB,
  USED_RGB,
  contentCellRgb,
  entropyRgb,
  fillDensityRgb,
  pageColorRgb,
  writeAgeRgb,
  type Rgb,
} from './dbMapColors';
import { hilbertD2XY, hilbertXY2D } from './hilbert';
import {
  DbPageType,
  isDetailEncoding,
  type DbChunkContent,
  type DbDetailTile,
  type DbMapData,
  type DbMapEncoding,
  type DbMapLens,
  type DbPageDetail,
} from './types';

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

/** Cell pixel size below which only L0 shows; above L1_FULL_CELL only L1 shows. Between → crossfade. */
const L0_ONLY_CELL = 0.5;
const L1_FULL_CELL = 4;
/** Opacity of the lens dim layer — non-highlighted pages fade back so the lens mask reads clearly (§4.3). */
const LENS_DIM_ALPHA = 0.62;
/** Outline colour for search-match cells (§4.5) — amber, distinct from the accent selection outline. */
const SEARCH_HIT_COLOR = 'rgb(245, 158, 11)';
/** Minimum cell size at which the segment-boundary overlay is drawn (zoomed-in only). */
const SEGMENT_OVERLAY_MIN_CELL = 5;
/** Page-cell pixel size above which the faint per-page gridline overlay is drawn. */
const GRID_MIN_CELL = 6;
/** Opacity of the per-page gridline overlay — barely visible, just enough to give each cell a border. */
const GRID_ALPHA = 0.04;
const MINIMAP_SIZE = 140;
const MINIMAP_MARGIN = 12;
const OFFSET_STRIP_HEIGHT = 16;
/** Safety caps so a degenerate camera can never schedule an unbounded fetch / draw. */
const MAX_VISIBLE_PAGES = 256;
const MAX_VISIBLE_CHUNKS = 256;

function clamp01(v: number): number {
  return v < 0 ? 0 : v > 1 ? 1 : v;
}

function lerpRgbCss(a: Rgb, b: Rgb, t: number): string {
  return `rgb(${Math.round(a[0] + (b[0] - a[0]) * t)}, ${Math.round(a[1] + (b[1] - a[1]) * t)}, ${Math.round(
    a[2] + (b[2] - a[2]) * t,
  )})`;
}

function rgb(c: Rgb): string {
  return `rgb(${c[0]}, ${c[1]}, ${c[2]})`;
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
  private _searchHits: readonly number[] = [];
  private _searchCurrent = -1;
  private _usedRatio = 0;

  // A2 detail-tier inputs, fed by the panel as the viewport changes.
  private _tiles: Map<number, DbDetailTile> = new Map();
  private _pageDetails: Map<number, DbPageDetail> = new Map();
  private _chunkContents: Map<string, DbChunkContent> = new Map();
  private _maxChangeRevision = 1;

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
    this._maxChangeRevision = 1;
    // A fresh map invalidates the previous map's lens mask, filter mask and search hits (page indices change).
    this._lensMask = null;
    this._filterMask = null;
    this._searchHits = [];
    this._searchCurrent = -1;
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
    let free = 0;
    for (let p = 0; p < data.pageCount; p++) {
      if (data.pageType[p] === DbPageType.Free) {
        free++;
      }
    }
    this._usedRatio = data.pageCount > 0 ? (data.pageCount - free) / data.pageCount : 0;
    this.paintOffscreen();
  }

  setEncoding(encoding: DbMapEncoding): void {
    if (this._encoding === encoding) {
      return;
    }
    this._encoding = encoding;
    this.paintOffscreen();
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

  /** Feeds the per-chunk decoded content (L4) for the visible chunks. */
  setChunkContents(chunks: Map<string, DbChunkContent>): void {
    this._chunkContents = chunks;
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
    const { l3Alpha, l4Alpha } = this.getLodState();
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

    if (l4Alpha > 0) {
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
    const cols = gridCols(detail.chunkTotal);
    const rows = Math.ceil(detail.chunkTotal / cols);
    const col = Math.min(cols - 1, Math.max(0, Math.floor(wx * cols)));
    const row = Math.min(rows - 1, Math.max(0, Math.floor(wy * rows)));
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
    const { x, y } = hilbertD2XY(this._layout.order, hit.page);
    const cols = gridCols(detail.chunkTotal);
    const rows = Math.ceil(detail.chunkTotal / cols);
    const chunkCol = hit.chunkInPage % cols;
    const chunkRow = Math.floor(hit.chunkInPage / cols);
    const wx = (screenX - this._camera.x) / this._camera.scale - (this._layout.dataRect.x + x) - chunkCol / cols;
    const wy = (screenY - this._camera.y) / this._camera.scale - (this._layout.dataRect.y + y) - chunkRow / rows;
    const ccols = gridCols(content.cells.length);
    const col = Math.min(ccols - 1, Math.max(0, Math.floor(wx * cols * ccols)));
    const row = Math.min(ccols - 1, Math.max(0, Math.floor(wy * rows * ccols)));
    const cellIndex = row * ccols + col;
    return cellIndex < content.cells.length ? { page: hit.page, chunkInPage: hit.chunkInPage, cellIndex } : null;
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

    // L0 — the data file as a single area-proportional rectangle filled by its used ratio.
    if (l1Alpha < 1) {
      ctx.globalAlpha = 1 - l1Alpha;
      ctx.fillStyle = lerpRgbCss(FREE_RGB, USED_RGB, this._usedRatio);
      this.fillWorldRect(ctx, layout.dataRect);
      ctx.globalAlpha = 1;
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

    // L3 — chunk grids, crossfaded in over L1; L4 — decoded content, crossfaded in over L3.
    if (l3Alpha > 0) {
      this.drawChunkBand(ctx, l3Alpha, l4Alpha);
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

    if (this._segmentOverlay && cellPx >= SEGMENT_OVERLAY_MIN_CELL && l3Alpha < 1) {
      this.drawSegmentOverlay(ctx);
    }

    // Search-match markers — every hit gets a thin amber outline; the current match a thicker one (§4.5).
    for (let i = 0; i < this._searchHits.length; i++) {
      this.drawCellHighlight(ctx, this._searchHits[i], SEARCH_HIT_COLOR, i === this._searchCurrent ? 3 : 1);
    }

    this.drawCellHighlight(ctx, this._hover, this._theme.mutedText, 1);
    this.drawCellHighlight(ctx, this._selection, this._theme.accent, 2);

    this.drawMinimap(ctx);
    this.drawOffsetStrip(ctx);

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
    // The inert Hilbert tail (cells beyond pageCount) reads as a flat dark background.
    for (let i = 0; i < buf.length; i += 4) {
      buf[i] = TAIL_RGB[0];
      buf[i + 1] = TAIL_RGB[1];
      buf[i + 2] = TAIL_RGB[2];
      buf[i + 3] = 255;
    }
    const { pageType, ownerSegmentId } = this._data;
    const detail = isDetailEncoding(this._encoding);
    for (let p = 0; p < pageCount; p++) {
      const { x, y } = hilbertD2XY(order, p);
      const rgbColor = detail
        ? this.detailPageRgb(p) ?? pageColorRgb('pageType', pageType[p], ownerSegmentId[p])
        : pageColorRgb(this._encoding, pageType[p], ownerSegmentId[p]);
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
      ctx.globalAlpha = l3Alpha;
      for (let i = 0; i < detail.chunkTotal; i++) {
        const occupied = detail.chunkOccupancy[i] === 1;
        ctx.fillStyle = rgb(occupied ? USED_RGB : FREE_RGB);
        this.fillWorldRect(ctx, gridSubRect(pageRect, cols, rows, i));
      }
      // Thin gridlines keep the chunk grid legible even when every chunk is occupied (a solid fill otherwise).
      this.drawChunkGridLines(ctx, pageRect, cols, rows, l3Alpha);

      if (l4Alpha > 0) {
        ctx.globalAlpha = l4Alpha;
        for (let i = 0; i < detail.chunkTotal; i++) {
          this.drawChunkContent(ctx, detail, gridSubRect(pageRect, cols, rows, i), i);
        }
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
    const content = this._chunkContents.get(`${detail.ownerSegmentId}:${detail.firstChunkId + chunkInPage}`);
    if (!content) {
      return;
    }
    if (content.decoder === 'unknown' || content.cells.length === 0) {
      this.drawUnknownTile(ctx, chunkRect);
      return;
    }
    const ccols = gridCols(content.cells.length);
    const crows = Math.ceil(content.cells.length / ccols);
    for (let j = 0; j < content.cells.length; j++) {
      const cell = content.cells[j];
      ctx.fillStyle = rgb(contentCellRgb(cell.kind, cell.colorKey));
      this.fillWorldRect(ctx, gridSubRect(chunkRect, ccols, crows, j));
    }
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

  private drawSegmentOverlay(ctx: CanvasRenderingContext2D): void {
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
    ctx.globalAlpha = 0.7;
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

  private drawCellHighlight(ctx: CanvasRenderingContext2D, page: number | null, color: string, width: number): void {
    if (page == null || !this._layout || page < 0 || page >= this._layout.pageCount) {
      return;
    }
    const { x, y } = hilbertD2XY(this._layout.order, page);
    const sx = worldToScreenX(this._camera, this._layout.dataRect.x + x);
    const sy = worldToScreenY(this._camera, this._layout.dataRect.y + y);
    const size = Math.max(this._camera.scale, 3);
    ctx.save();
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
