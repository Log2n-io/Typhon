import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { Crosshair, RefreshCw } from 'lucide-react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useDbMapStore } from '@/stores/useDbMapStore';
import { useDbMapSelectionStore } from '@/stores/useDbMapSelectionStore';
import { useDbMap } from '@/hooks/dbmap/useDbMap';
import { useDbMapChunks, useDbMapPages, useDbMapTiles } from '@/hooks/dbmap/useDbMapDetail';
import { formatFileSize } from '@/lib/formatters';
import { DbMapRenderer, type DbDetailRequest, type DbMapTheme } from '@/libs/dbmap/dbMapRenderer';
import {
  fitToRect,
  zoomAt,
  zoomToWorldRect,
  screenToWorldX,
  screenToWorldY,
  type Camera,
} from '@/libs/dbmap/camera';
import { hilbertD2XY } from '@/libs/dbmap/hilbert';
import {
  DbPageType,
  NO_SEGMENT,
  PAGE_SIZE,
  PAGE_TYPE_LABELS,
  isDetailEncoding,
  type DbMapData,
  type DbMapEncoding,
} from '@/libs/dbmap/types';
import {
  CRC_RGB,
  FREE_RGB,
  PAGE_TYPE_RGB,
  RESIDENCY_RGB,
  USED_RGB,
  fillDensityRgb,
  rgbCss,
  writeAgeRgb,
} from '@/libs/dbmap/dbMapColors';

const FIT_PADDING = 24;
const CLICK_SLOP_PX = 3;
/** Debounce before re-deriving the detail-fetch set after the camera settles. */
const DETAIL_SYNC_MS = 160;

const EMPTY_REQUEST: DbDetailRequest = { tileNodes: [], pages: [], chunks: [] };

/** Drag-gesture state, held in a ref so high-frequency mouse events never trigger React renders. */
interface DragState {
  mode: 'pan' | 'region' | 'minimap' | 'strip';
  startX: number;
  startY: number;
  startCam: Camera;
  moved: boolean;
}

/** Transient hover info shown in the on-surface tooltip. */
interface HoverInfo {
  pageIndex: number;
  typeLabel: string;
  segmentLabel: string;
  byteOffset: number;
  clientX: number;
  clientY: number;
}

/**
 * Database File Map panel (Module 15, Track A). Renders the open database's on-disk layout as a Hilbert-laid,
 * area-proportional page grid (A1) with the deep L3 chunk / L4 content bands (A2). Owns the 2D camera, drives
 * the on-demand detail-tile fetch from the viewport, and routes selections to the shared Detail panel. Gesture
 * transients live in refs (the profiler's rAF-coalesced pattern) so pan / zoom stay at 60 fps.
 */
export default function DbMapPanel(_props: IDockviewPanelProps) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const encoding = useDbMapStore((s) => s.encoding);
  const setEncoding = useDbMapStore((s) => s.setEncoding);
  const segmentOverlay = useDbMapStore((s) => s.segmentOverlay);
  const toggleSegmentOverlay = useDbMapStore((s) => s.toggleSegmentOverlay);
  const selectDbMap = useDbMapSelectionStore((s) => s.select);
  const clearDbMapSelection = useDbMapSelectionStore((s) => s.clear);

  const { data, isLoading, isError, refetch } = useDbMap(sessionId);

  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const surfaceRef = useRef<HTMLDivElement | null>(null);
  const rendererRef = useRef<DbMapRenderer | null>(null);
  const cameraRef = useRef<Camera>({ scale: 1, x: 0, y: 0 });
  const frameRef = useRef<number | null>(null);
  const dragRef = useRef<DragState | null>(null);
  const detailSyncRef = useRef<number | null>(null);
  // The camera is fit to the file only on first load — a later refresh (or refetch) keeps the user's viewport.
  const fittedRef = useRef(false);

  const [hover, setHover] = useState<HoverInfo | null>(null);
  const [regionRect, setRegionRect] = useState<{ x: number; y: number; w: number; h: number } | null>(null);
  const [themeTick, setThemeTick] = useState(0);
  const [detailReq, setDetailReq] = useState<DbDetailRequest>(EMPTY_REQUEST);
  const [lod, setLod] = useState<{ band: 'L1' | 'L3' | 'L4'; focusedPage: number | null }>({
    band: 'L1',
    focusedPage: null,
  });

  // On-demand detail data — TanStack Query caches each tile / page / chunk, so panning back never refetches.
  const tiles = useDbMapTiles(sessionId, detailReq.tileNodes);
  const pageDetails = useDbMapPages(sessionId, detailReq.pages);
  const chunkContents = useDbMapChunks(sessionId, detailReq.chunks);

  // rAF-coalesced redraw — every input mutates cameraRef then asks for one frame.
  const scheduleRender = useCallback(() => {
    if (frameRef.current != null) {
      return;
    }
    frameRef.current = requestAnimationFrame(() => {
      frameRef.current = null;
      const renderer = rendererRef.current;
      if (renderer) {
        renderer.setCamera(cameraRef.current);
        renderer.render();
      }
    });
  }, []);

  // After the camera settles, re-derive which detail tiles / pages / chunks the viewport now needs.
  const queueDetailSync = useCallback(() => {
    if (detailSyncRef.current != null) {
      window.clearTimeout(detailSyncRef.current);
    }
    detailSyncRef.current = window.setTimeout(() => {
      detailSyncRef.current = null;
      const renderer = rendererRef.current;
      if (!renderer) {
        return;
      }
      const req = renderer.getDetailRequest();
      setDetailReq((prev) => (sameRequest(prev, req) ? prev : req));
      const lodState = renderer.getLodState();
      const focused = renderer.getFocusedPage();
      setLod((prev) =>
        prev.band === lodState.band && prev.focusedPage === focused
          ? prev
          : { band: lodState.band, focusedPage: focused },
      );
    }, DETAIL_SYNC_MS);
  }, []);

  // Construct the renderer once the canvas element exists.
  useLayoutEffect(() => {
    if (!canvasRef.current) {
      return;
    }
    rendererRef.current = new DbMapRenderer(canvasRef.current);
    rendererRef.current.setTheme(readDbMapTheme());
  }, []);

  // Track <html>'s class attribute — ThemeProvider toggles `.dark` there; a tick triggers the redraw.
  useEffect(() => {
    const observer = new MutationObserver(() => setThemeTick((n) => n + 1));
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
    return () => observer.disconnect();
  }, []);

  // Push the decoded map into the renderer and frame the whole file. The encoding / overlay are applied by
  // their own effect below, which also runs on mount — so this effect deliberately tracks only data.
  useEffect(() => {
    const renderer = rendererRef.current;
    const surface = surfaceRef.current;
    if (!renderer || !surface) {
      return;
    }
    const { width, height } = surface.getBoundingClientRect();
    renderer.setViewport(width, height, window.devicePixelRatio || 1);
    renderer.setData(data ?? null);
    setDetailReq(EMPTY_REQUEST);
    const layout = renderer.getLayout();
    if (!data) {
      fittedRef.current = false;
    } else if (layout && width > 0 && height > 0 && !fittedRef.current) {
      cameraRef.current = fitToRect(layout.worldBounds, width, height, FIT_PADDING);
      fittedRef.current = true;
    }
    renderer.setCamera(cameraRef.current);
    renderer.render();
  }, [data]);

  // Theme change — re-resolve the token colours and repaint, without disturbing the camera.
  useEffect(() => {
    const renderer = rendererRef.current;
    if (!renderer) {
      return;
    }
    renderer.setTheme(readDbMapTheme());
    renderer.render();
  }, [themeTick]);

  // Encoding / overlay changes — recolor without reframing; a detail encoding triggers a tile fetch.
  useEffect(() => {
    const renderer = rendererRef.current;
    if (!renderer) {
      return;
    }
    renderer.setEncoding(encoding);
    renderer.setSegmentOverlay(segmentOverlay);
    scheduleRender();
    queueDetailSync();
  }, [encoding, segmentOverlay, scheduleRender, queueDetailSync]);

  // Detail data arrived — feed the renderer and repaint. Re-run the detail sync too: an L4 chunk request can
  // only be derived once the page details (which carry firstChunkId) have loaded, so page data arriving must
  // trigger a fresh getDetailRequest. The debounce + same-request guard keep this from churning.
  useEffect(() => {
    const renderer = rendererRef.current;
    if (!renderer) {
      return;
    }
    renderer.setDetailTiles(tiles);
    renderer.setPageDetails(pageDetails);
    renderer.setChunkContents(chunkContents);
    scheduleRender();
    queueDetailSync();
  }, [tiles, pageDetails, chunkContents, scheduleRender, queueDetailSync]);

  // Resize — keep the canvas backing store in sync with the surface.
  useEffect(() => {
    const surface = surfaceRef.current;
    const renderer = rendererRef.current;
    if (!surface || !renderer) {
      return;
    }
    const ro = new ResizeObserver(() => {
      const { width, height } = surface.getBoundingClientRect();
      renderer.setViewport(width, height, window.devicePixelRatio || 1);
      renderer.render();
    });
    ro.observe(surface);
    return () => ro.disconnect();
  }, []);

  // Non-passive wheel listener — zoom toward the cursor; Ctrl multiplies the speed.
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) {
      return;
    }
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const pt = canvasPoint(canvas, e.clientX, e.clientY);
      const step = e.ctrlKey ? 1.35 : 1.1;
      const factor = e.deltaY < 0 ? step : 1 / step;
      cameraRef.current = zoomAt(cameraRef.current, pt.x, pt.y, factor);
      scheduleRender();
      queueDetailSync();
    };
    canvas.addEventListener('wheel', onWheel, { passive: false });
    return () => canvas.removeEventListener('wheel', onWheel);
  }, [scheduleRender, queueDetailSync]);

  // Drop any pending detail-sync timer on unmount.
  useEffect(
    () => () => {
      if (detailSyncRef.current != null) {
        window.clearTimeout(detailSyncRef.current);
      }
    },
    [],
  );

  const fitWholeFile = useCallback(() => {
    const renderer = rendererRef.current;
    const surface = surfaceRef.current;
    const layout = renderer?.getLayout();
    if (!renderer || !surface || !layout) {
      return;
    }
    const { width, height } = surface.getBoundingClientRect();
    cameraRef.current = fitToRect(layout.worldBounds, width, height, FIT_PADDING);
    scheduleRender();
    queueDetailSync();
  }, [scheduleRender, queueDetailSync]);

  // ── Mouse interaction ───────────────────────────────────────────────────────────────────────────────
  // The gesture helpers are memoised so the window-level move/up listeners keep a stable identity for the
  // span of a drag (the deps below are all gesture-stable — they never change mid-drag).

  const jumpViaMinimap = useCallback(
    (screenX: number, screenY: number) => {
      const renderer = rendererRef.current;
      const surface = surfaceRef.current;
      if (!renderer || !surface) {
        return;
      }
      const world = renderer.minimapToWorld(screenX, screenY);
      if (!world) {
        return;
      }
      const { width, height } = surface.getBoundingClientRect();
      cameraRef.current = centerCameraOn(cameraRef.current, world.x, world.y, width, height);
      scheduleRender();
      queueDetailSync();
    },
    [scheduleRender, queueDetailSync],
  );

  const jumpViaOffsetStrip = useCallback(
    (screenX: number) => {
      const renderer = rendererRef.current;
      const surface = surfaceRef.current;
      const layout = renderer?.getLayout();
      if (!renderer || !surface || !layout) {
        return;
      }
      const page = renderer.offsetStripToPage(screenX);
      if (page == null) {
        return;
      }
      const { x, y } = hilbertD2XY(layout.order, page);
      const { width, height } = surface.getBoundingClientRect();
      cameraRef.current = centerCameraOn(
        cameraRef.current,
        layout.dataRect.x + x + 0.5,
        layout.dataRect.y + y + 0.5,
        width,
        height,
      );
      scheduleRender();
      queueDetailSync();
    },
    [scheduleRender, queueDetailSync],
  );

  const selectAt = useCallback(
    (screenX: number, screenY: number) => {
      const renderer = rendererRef.current;
      if (!renderer || !data) {
        return;
      }
      const band = renderer.getLodState().band;

      // L4 — a content cell decodes to a single record.
      if (band === 'L4') {
        const hit = renderer.pickContentCell(screenX, screenY);
        if (hit) {
          const detail = pageDetails.get(hit.page);
          const content = detail
            ? chunkContents.get(`${detail.ownerSegmentId}:${detail.firstChunkId + hit.chunkInPage}`)
            : undefined;
          const cell = content?.cells[hit.cellIndex];
          if (detail && cell) {
            renderer.setSelection(hit.page);
            scheduleRender();
            selectDbMap(data.databaseName, {
              kind: 'cell',
              pageIndex: hit.page,
              segmentId: detail.ownerSegmentId,
              chunkId: detail.firstChunkId + hit.chunkInPage,
              cellOffset: cell.offset,
            });
            return;
          }
        }
      }

      // L3 — a chunk.
      if (band === 'L3' || band === 'L4') {
        const hit = renderer.pickChunk(screenX, screenY);
        if (hit) {
          const detail = pageDetails.get(hit.page);
          if (detail && detail.ownerSegmentId >= 0) {
            renderer.setSelection(hit.page);
            scheduleRender();
            selectDbMap(data.databaseName, {
              kind: 'chunk',
              pageIndex: hit.page,
              segmentId: detail.ownerSegmentId,
              chunkId: detail.firstChunkId + hit.chunkInPage,
            });
            return;
          }
        }
      }

      // L1 — a page.
      const page = renderer.pageAt(screenX, screenY);
      renderer.setSelection(page);
      scheduleRender();
      if (page == null) {
        clearDbMapSelection();
        return;
      }
      selectDbMap(data.databaseName, { kind: 'page', pageIndex: page });
    },
    [data, pageDetails, chunkContents, scheduleRender, selectDbMap, clearDbMapSelection],
  );

  const handleWindowMouseMove = useCallback(
    (e: MouseEvent) => {
      const canvas = canvasRef.current;
      const drag = dragRef.current;
      if (!canvas || !drag) {
        return;
      }
      const pt = canvasPoint(canvas, e.clientX, e.clientY);
      if (Math.abs(pt.x - drag.startX) > CLICK_SLOP_PX || Math.abs(pt.y - drag.startY) > CLICK_SLOP_PX) {
        drag.moved = true;
      }
      if (drag.mode === 'pan') {
        cameraRef.current = {
          scale: drag.startCam.scale,
          x: drag.startCam.x + (pt.x - drag.startX),
          y: drag.startCam.y + (pt.y - drag.startY),
        };
        scheduleRender();
      } else if (drag.mode === 'minimap') {
        jumpViaMinimap(pt.x, pt.y);
      } else if (drag.mode === 'strip') {
        jumpViaOffsetStrip(pt.x);
      } else if (drag.mode === 'region') {
        setRegionRect({
          x: Math.min(drag.startX, pt.x),
          y: Math.min(drag.startY, pt.y),
          w: Math.abs(pt.x - drag.startX),
          h: Math.abs(pt.y - drag.startY),
        });
      }
    },
    [scheduleRender, jumpViaMinimap, jumpViaOffsetStrip],
  );

  const handleWindowMouseUp = useCallback(
    (e: MouseEvent) => {
      window.removeEventListener('mousemove', handleWindowMouseMove);
      window.removeEventListener('mouseup', handleWindowMouseUp);
      const canvas = canvasRef.current;
      const renderer = rendererRef.current;
      const drag = dragRef.current;
      dragRef.current = null;
      if (!canvas || !renderer || !drag) {
        return;
      }
      const pt = canvasPoint(canvas, e.clientX, e.clientY);

      if (drag.mode === 'region' && drag.moved) {
        const cam = cameraRef.current;
        const world = {
          x: screenToWorldX(cam, Math.min(drag.startX, pt.x)),
          y: screenToWorldY(cam, Math.min(drag.startY, pt.y)),
          w: Math.abs(pt.x - drag.startX) / cam.scale,
          h: Math.abs(pt.y - drag.startY) / cam.scale,
        };
        const surface = surfaceRef.current;
        if (surface && world.w > 0 && world.h > 0) {
          const { width, height } = surface.getBoundingClientRect();
          cameraRef.current = zoomToWorldRect(world, width, height, FIT_PADDING);
          scheduleRender();
          queueDetailSync();
        }
      } else if (drag.mode === 'pan' && !drag.moved) {
        selectAt(pt.x, pt.y);
      } else if (drag.mode === 'pan' && drag.moved) {
        queueDetailSync();
      }
      setRegionRect(null);
    },
    [handleWindowMouseMove, scheduleRender, queueDetailSync, selectAt],
  );

  const handleMouseDown = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current;
    const renderer = rendererRef.current;
    if (!canvas || !renderer) {
      return;
    }
    const pt = canvasPoint(canvas, e.clientX, e.clientY);
    const mm = renderer.getMinimapScreenRect();
    const strip = renderer.getOffsetStripScreenRect();

    let mode: DragState['mode'] = e.shiftKey ? 'region' : 'pan';
    if (pointIn(pt, mm)) {
      mode = 'minimap';
      jumpViaMinimap(pt.x, pt.y);
    } else if (pointIn(pt, strip)) {
      mode = 'strip';
      jumpViaOffsetStrip(pt.x);
    }
    dragRef.current = { mode, startX: pt.x, startY: pt.y, startCam: cameraRef.current, moved: false };
    window.addEventListener('mousemove', handleWindowMouseMove);
    window.addEventListener('mouseup', handleWindowMouseUp);
  };

  const handleHoverMove = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current;
    const renderer = rendererRef.current;
    if (!canvas || !renderer || dragRef.current || !data) {
      return;
    }
    const pt = canvasPoint(canvas, e.clientX, e.clientY);
    const page = renderer.pageAt(pt.x, pt.y);
    renderer.setHover(page);
    scheduleRender();
    if (page == null) {
      setHover(null);
      return;
    }
    setHover({
      pageIndex: page,
      typeLabel: PAGE_TYPE_LABELS[data.pageType[page]] ?? 'Unknown',
      segmentLabel: segmentLabel(data, page),
      byteOffset: page * PAGE_SIZE,
      clientX: e.clientX,
      clientY: e.clientY,
    });
  };

  const handleHoverLeave = () => {
    setHover(null);
    rendererRef.current?.setHover(null);
    scheduleRender();
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    if (e.key === 's' || e.key === 'S') {
      toggleSegmentOverlay();
      e.preventDefault();
    } else if (e.key === 'f' || e.key === 'F') {
      fitWholeFile();
      e.preventDefault();
    } else if (e.key === 'Escape') {
      rendererRef.current?.setSelection(null);
      clearDbMapSelection();
      scheduleRender();
      e.preventDefault();
    }
  };

  // ── Render ──────────────────────────────────────────────────────────────────────────────────────────

  return (
    <div
      className="flex h-full w-full flex-col overflow-hidden bg-background outline-none"
      tabIndex={0}
      onKeyDown={handleKeyDown}
      data-testid="dbmap-panel"
    >
      <div className="flex items-center gap-2 border-b border-border px-3 py-1.5">
        <label className="text-[11px] text-muted-foreground">Encoding</label>
        <select
          className="rounded border border-border bg-card px-1.5 py-0.5 text-[11px] text-foreground"
          value={encoding}
          onChange={(e) => setEncoding(e.target.value as DbMapEncoding)}
          data-testid="dbmap-encoding"
        >
          <optgroup label="Coarse">
            <option value="pageType">Page type</option>
            <option value="segment">Owning segment</option>
            <option value="freeUsed">Free / used</option>
          </optgroup>
          <optgroup label="Detail">
            <option value="fillDensity">Fill density</option>
            <option value="writeAge">Write age</option>
            <option value="crc">CRC status</option>
            <option value="residency">Cache residency</option>
          </optgroup>
        </select>
        <button
          type="button"
          onClick={toggleSegmentOverlay}
          className={`rounded border px-1.5 py-0.5 text-[11px] ${
            segmentOverlay
              ? 'border-primary bg-primary/15 text-foreground'
              : 'border-border bg-card text-muted-foreground'
          }`}
          title="Toggle segment-boundary overlay (s)"
        >
          Segments
        </button>
        <button
          type="button"
          onClick={fitWholeFile}
          className="flex items-center gap-1 rounded border border-border bg-card px-1.5 py-0.5 text-[11px] text-muted-foreground hover:text-foreground"
          title="Fit whole file (f)"
        >
          <Crosshair className="h-3 w-3" /> Fit
        </button>
        <button
          type="button"
          onClick={() => void refetch()}
          className="flex items-center gap-1 rounded border border-border bg-card px-1.5 py-0.5 text-[11px] text-muted-foreground hover:text-foreground"
          title="Refresh the map"
        >
          <RefreshCw className="h-3 w-3" /> Refresh
        </button>
        <DbMapLegend encoding={encoding} />
      </div>

      <div className="border-b border-border px-3 py-1 text-[11px] text-muted-foreground" data-testid="dbmap-breadcrumb">
        {data ? (
          <span>
            <span className="font-mono text-foreground">{data.databaseName}</span>
            {' · '}
            {data.pageCount.toLocaleString()} pages · {formatFileSize(data.dataFileBytes)}
            {data.walBytes > 0 ? ` · WAL ${formatFileSize(data.walBytes)}` : ' · no WAL'}
            {lod.band !== 'L1' && lod.focusedPage != null && (
              <span className="text-foreground">
                {' › '}
                Page {lod.focusedPage.toLocaleString()}
                {lod.band === 'L4' ? ' › chunk content' : ' › chunks'}
              </span>
            )}
          </span>
        ) : (
          <span>No database open</span>
        )}
      </div>

      <div ref={surfaceRef} className="relative min-h-0 flex-1 overflow-hidden">
        <canvas
          ref={canvasRef}
          onMouseDown={handleMouseDown}
          onMouseMove={handleHoverMove}
          onMouseLeave={handleHoverLeave}
          style={{ display: 'block', cursor: 'crosshair' }}
          data-testid="dbmap-canvas"
        />
        {regionRect && (
          <div
            className="pointer-events-none absolute border border-primary bg-primary/10"
            style={{ left: regionRect.x, top: regionRect.y, width: regionRect.w, height: regionRect.h }}
          />
        )}
        {isLoading && (
          <p className="absolute left-3 top-2 text-[11px] text-muted-foreground">Loading map…</p>
        )}
        {isError && (
          <p className="absolute left-3 top-2 text-[11px] text-destructive">Failed to load the file map.</p>
        )}
        {hover && <HoverTooltip info={hover} />}
      </div>
    </div>
  );
}

/** True when two detail requests address the same tiles / pages / chunks. */
function sameRequest(a: DbDetailRequest, b: DbDetailRequest): boolean {
  const sameNums = (x: number[], y: number[]) => x.length === y.length && x.every((v, i) => v === y[i]);
  return (
    sameNums(a.tileNodes, b.tileNodes) &&
    sameNums(a.pages, b.pages) &&
    a.chunks.length === b.chunks.length &&
    a.chunks.every((c, i) => c.segId === b.chunks[i].segId && c.chunkId === b.chunks[i].chunkId)
  );
}

function HoverTooltip({ info }: { info: HoverInfo }) {
  return (
    <div
      className="pointer-events-none z-50 rounded border border-border bg-popover px-2 py-1 text-[11px] text-popover-foreground shadow-md"
      style={{ position: 'fixed', left: info.clientX + 12, top: info.clientY - 8, transform: 'translateY(-100%)' }}
    >
      <span className="font-mono font-semibold text-foreground">#{info.pageIndex}</span>
      <span className="ml-2 text-muted-foreground">{info.typeLabel}</span>
      <span className="ml-2 text-muted-foreground">{info.segmentLabel}</span>
      <span className="ml-2 font-mono tabular-nums text-muted-foreground">
        @ 0x{info.byteOffset.toString(16).toUpperCase()}
      </span>
    </div>
  );
}

function DbMapLegend({ encoding }: { encoding: DbMapEncoding }) {
  if (encoding === 'segment') {
    return <span className="ml-auto text-[10px] text-muted-foreground">Colour: one hue per segment</span>;
  }
  if (isDetailEncoding(encoding)) {
    return <DbMapDetailLegend encoding={encoding} />;
  }
  const entries: { label: string; color: string }[] =
    encoding === 'freeUsed'
      ? [
          { label: 'Free', color: rgbCss(FREE_RGB) },
          { label: 'Used', color: rgbCss(USED_RGB) },
        ]
      : [DbPageType.Free, DbPageType.Root, DbPageType.Occupancy, DbPageType.Component, DbPageType.Index].map(
          (t) => ({ label: PAGE_TYPE_LABELS[t], color: rgbCss(PAGE_TYPE_RGB[t]) }),
        );
  return <SwatchRow entries={entries} />;
}

function DbMapDetailLegend({ encoding }: { encoding: DbMapEncoding }) {
  if (encoding === 'crc') {
    return (
      <SwatchRow
        entries={[
          { label: 'Unverified', color: rgbCss(CRC_RGB[0]) },
          { label: 'Verified', color: rgbCss(CRC_RGB[1]) },
          { label: 'Failed', color: rgbCss(CRC_RGB[2]) },
        ]}
      />
    );
  }
  if (encoding === 'residency') {
    return (
      <SwatchRow
        entries={[
          { label: 'On disk', color: rgbCss(RESIDENCY_RGB[0]) },
          { label: 'Clean', color: rgbCss(RESIDENCY_RGB[1]) },
          { label: 'Dirty', color: rgbCss(RESIDENCY_RGB[2]) },
        ]}
      />
    );
  }
  // Sequential ramp — fill density / write age.
  const stops = [0, 0.25, 0.5, 0.75, 1];
  const ramp = encoding === 'writeAge' ? writeAgeRgb : fillDensityRgb;
  const lo = encoding === 'writeAge' ? 'old' : 'empty';
  const hi = encoding === 'writeAge' ? 'new' : 'full';
  return (
    <div className="ml-auto flex items-center gap-1 text-[10px] text-muted-foreground">
      <span>{lo}</span>
      {stops.map((s) => (
        <span key={s} className="inline-block h-2.5 w-3" style={{ backgroundColor: rgbCss(ramp(s)) }} />
      ))}
      <span>{hi}</span>
    </div>
  );
}

function SwatchRow({ entries }: { entries: { label: string; color: string }[] }) {
  return (
    <div className="ml-auto flex items-center gap-2">
      {entries.map((e) => (
        <span key={e.label} className="flex items-center gap-1 text-[10px] text-muted-foreground">
          <span className="inline-block h-2.5 w-2.5 rounded-sm" style={{ backgroundColor: e.color }} />
          {e.label}
        </span>
      ))}
    </div>
  );
}

function canvasPoint(canvas: HTMLCanvasElement, clientX: number, clientY: number): { x: number; y: number } {
  const rect = canvas.getBoundingClientRect();
  return { x: clientX - rect.left, y: clientY - rect.top };
}

function pointIn(pt: { x: number; y: number }, r: { x: number; y: number; w: number; h: number }): boolean {
  return pt.x >= r.x && pt.x < r.x + r.w && pt.y >= r.y && pt.y < r.y + r.h;
}

function centerCameraOn(cam: Camera, worldX: number, worldY: number, viewportW: number, viewportH: number): Camera {
  return {
    scale: cam.scale,
    x: viewportW / 2 - worldX * cam.scale,
    y: viewportH / 2 - worldY * cam.scale,
  };
}

function segmentLabel(data: DbMapData, page: number): string {
  const segId = data.ownerSegmentId[page];
  if (segId === NO_SEGMENT) {
    return 'no segment';
  }
  const seg = data.segments.find((s) => s.id === segId);
  return seg ? `${seg.kind} #${seg.id}` : `segment #${segId}`;
}

/** Resolves the renderer theme from the design-token CSS variables on <html>. */
function readDbMapTheme(): DbMapTheme {
  if (typeof document === 'undefined') {
    return {
      background: '#0f172a',
      surface: '#1e293b',
      border: '#334155',
      text: '#e2e8f0',
      mutedText: '#94a3b8',
      accent: '#38bdf8',
    };
  }
  const cs = getComputedStyle(document.documentElement);
  const read = (name: string, fallback: string): string => {
    const v = cs.getPropertyValue(name).trim();
    return v.length > 0 ? v : fallback;
  };
  return {
    background: read('--background', '#0f172a'),
    surface: read('--card', '#1e293b'),
    border: read('--border', '#334155'),
    text: read('--foreground', '#e2e8f0'),
    mutedText: read('--muted-foreground', '#94a3b8'),
    accent: read('--primary', '#38bdf8'),
  };
}
