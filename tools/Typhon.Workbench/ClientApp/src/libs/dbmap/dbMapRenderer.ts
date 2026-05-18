// Owner-drawn Canvas 2D renderer for the Database File Map (Module 15, §6.6).
//
// Built on the profiler's owner-drawn pattern (libs/profiler/canvas) — no third-party drawing library. The
// coarse Hilbert map is painted once into an offscreen image (one pixel per page); every frame is then a single
// camera-transformed drawImage, so per-frame cost is independent of database size — the de-risking the A1
// rendering spike validates. The class surface (setData / setCamera / render / …) is the seam behind which a
// PixiJS renderer could be swapped if Canvas 2D ever missed 60 fps.

import {
  visibleWorldRect,
  worldToScreenX,
  worldToScreenY,
  type Camera,
  type Rect,
} from './camera';
import { buildLayout, type MapLayout } from './dbMapLayout';
import { FREE_RGB, TAIL_RGB, USED_RGB, pageColorRgb, type Rgb } from './dbMapColors';
import { hilbertD2XY, hilbertXY2D } from './hilbert';
import { DbPageType, type DbMapData, type DbMapEncoding } from './types';

/** Theme tokens the renderer needs — resolved from CSS variables by the panel. */
export interface DbMapTheme {
  background: string;
  surface: string;
  border: string;
  text: string;
  mutedText: string;
  accent: string;
}

/** Cell pixel size below which only L0 shows; above L1_FULL_CELL only L1 shows. Between → crossfade. */
const L0_ONLY_CELL = 0.5;
const L1_FULL_CELL = 4;
/** Minimum cell size at which the segment-boundary overlay is drawn (zoomed-in only). */
const SEGMENT_OVERLAY_MIN_CELL = 5;
const MINIMAP_SIZE = 140;
const MINIMAP_MARGIN = 12;
const OFFSET_STRIP_HEIGHT = 16;

function clamp01(v: number): number {
  return v < 0 ? 0 : v > 1 ? 1 : v;
}

function lerpRgb(a: Rgb, b: Rgb, t: number): string {
  return `rgb(${Math.round(a[0] + (b[0] - a[0]) * t)}, ${Math.round(a[1] + (b[1] - a[1]) * t)}, ${Math.round(
    a[2] + (b[2] - a[2]) * t,
  )})`;
}

export class DbMapRenderer {
  private readonly _canvas: HTMLCanvasElement;
  private readonly _ctx: CanvasRenderingContext2D;
  private readonly _offscreen: HTMLCanvasElement;
  private readonly _offCtx: CanvasRenderingContext2D;

  private _data: DbMapData | null = null;
  private _layout: MapLayout | null = null;
  private _encoding: DbMapEncoding = 'pageType';
  private _segmentOverlay = false;
  private _camera: Camera = { scale: 1, x: 0, y: 0 };
  private _hover: number | null = null;
  private _selection: number | null = null;
  private _usedRatio = 0;

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
    const offCtx = this._offscreen.getContext('2d');
    if (!offCtx) {
      throw new Error('DbMapRenderer: offscreen 2D context unavailable');
    }
    this._offCtx = offCtx;
  }

  // ── Inputs ────────────────────────────────────────────────────────────────────────────────────────────

  setData(data: DbMapData | null): void {
    this._data = data;
    if (!data) {
      this._layout = null;
      return;
    }
    this._layout = buildLayout(data.pageCount, data.walBytes, data.hilbertOrder);
    this._offscreen.width = this._layout.side;
    this._offscreen.height = this._layout.side;
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

  setCamera(camera: Camera): void {
    this._camera = camera;
  }

  setHover(page: number | null): void {
    this._hover = page;
  }

  setSelection(page: number | null): void {
    this._selection = page;
  }

  setTheme(theme: DbMapTheme): void {
    this._theme = theme;
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

    // L0 — the data file as a single area-proportional rectangle filled by its used ratio.
    if (l1Alpha < 1) {
      ctx.globalAlpha = 1 - l1Alpha;
      ctx.fillStyle = lerpRgb(FREE_RGB, USED_RGB, this._usedRatio);
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

    if (this._segmentOverlay && cellPx >= SEGMENT_OVERLAY_MIN_CELL) {
      this.drawSegmentOverlay(ctx);
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
    for (let p = 0; p < pageCount; p++) {
      const { x, y } = hilbertD2XY(order, p);
      const rgb = pageColorRgb(this._encoding, pageType[p], ownerSegmentId[p]);
      const o = (y * side + x) * 4;
      buf[o] = rgb[0];
      buf[o + 1] = rgb[1];
      buf[o + 2] = rgb[2];
      buf[o + 3] = 255;
    }
    this._offCtx.putImageData(img, 0, 0);
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

  /** The bounding page-index span of currently visible cells, or null when the whole file is visible. */
  private visiblePageSpan(): { min: number; max: number } | null {
    if (!this._layout) {
      return null;
    }
    const { order, side, dataRect, pageCount } = this._layout;
    const vis = visibleWorldRect(this._camera, this._cssW, this._cssH);
    const cx0 = Math.max(0, Math.floor(vis.x - dataRect.x));
    const cy0 = Math.max(0, Math.floor(vis.y - dataRect.y));
    const cx1 = Math.min(side - 1, Math.ceil(vis.x - dataRect.x + vis.w));
    const cy1 = Math.min(side - 1, Math.ceil(vis.y - dataRect.y + vis.h));
    const cellCount = (cx1 - cx0 + 1) * (cy1 - cy0 + 1);
    if (cellCount <= 0 || cellCount >= 40000 || cellCount >= side * side) {
      return null;
    }
    let min = pageCount;
    let max = -1;
    for (let cy = cy0; cy <= cy1; cy++) {
      for (let cx = cx0; cx <= cx1; cx++) {
        const page = hilbertXY2D(order, cx, cy);
        if (page >= 0 && page < pageCount) {
          if (page < min) min = page;
          if (page > max) max = page;
        }
      }
    }
    return max >= min ? { min, max } : null;
  }
}

function layoutScale(value: number, total: number): number {
  return total > 0 ? value / total : 0;
}
