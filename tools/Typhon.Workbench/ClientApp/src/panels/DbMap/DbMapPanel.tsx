import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useDbMapStore } from '@/stores/useDbMapStore';
import { useDbMapSelectionStore } from '@/stores/useDbMapSelectionStore';
import { useDbMap } from '@/hooks/dbmap/useDbMap';
import { useDbMapChunks, useDbMapPages, useDbMapTiles } from '@/hooks/dbmap/useDbMapDetail';
import { useDbMapSegment } from '@/hooks/dbmap/useDbMapSegment';
import { formatFileSize } from '@/lib/formatters';
import { DbMapRenderer, type DbDetailRequest, type DbMapTheme } from '@/libs/dbmap/dbMapRenderer';
import {
  cameraCenteredOn,
  fitToRect,
  tweenCamera,
  zoomAt,
  zoomToWorldRect,
  screenToWorldX,
  screenToWorldY,
  type Camera,
  type CameraTween,
} from '@/libs/dbmap/camera';
import { hilbertD2XY } from '@/libs/dbmap/hilbert';
import { DbPageType, NO_SEGMENT, PAGE_SIZE, PAGE_TYPE_LABELS, type DbMapData } from '@/libs/dbmap/types';
import { buildRegions } from '@/libs/dbmap/dbMapRegions';
import { findUnderFilledPages, LOW_FILL_THRESHOLD } from '@/libs/dbmap/dbMapPathology';
import {
  contiguousRuns,
  fillDensity,
  fragmentationPercent,
  freeSpaceComposition,
  segmentReclaimableBytes,
} from '@/libs/dbmap/dbMapMetrics';
import { searchDbMap } from '@/libs/dbmap/dbMapSearch';
import { buildFilterMask } from '@/libs/dbmap/dbMapFilter';
import { newBookmarkId } from '@/libs/dbmap/dbMapBookmarks';
import { exportRegionsCsv, exportViewPng, exportWholeMapPng } from '@/libs/dbmap/dbMapExport';
import {
  openComponentInSchema,
  registerDbMapCameraRestore,
  revealComponentInResourceTree,
} from '@/shell/commands/openDbMap';
import { useNavHistoryStore } from '@/stores/useNavHistoryStore';
import { DbMapToolbar } from './DbMapToolbar';
import { DbMapContextMenu } from './DbMapContextMenu';
import { DbMapSidePanel } from './sidebar/DbMapSidePanel';
import { LegendTab } from './sidebar/LegendTab';
import { RegionsTab } from './sidebar/RegionsTab';
import { BookmarksTab } from './sidebar/BookmarksTab';
import type { MetricsCardData } from './sidebar/MetricsCard';

const FIT_PADDING = 24;
const CLICK_SLOP_PX = 3;
/** Debounce before re-deriving the detail-fetch set after the camera settles. */
const DETAIL_SYNC_MS = 160;
/** Camera fly-to animation duration (§4.5). */
const FLY_DURATION_MS = 420;
/** Wheel-zoom glide duration — short so rapid notches stay responsive while each notch still eases. */
const WHEEL_ZOOM_DURATION_MS = 500;
/** Fit-whole-file glide duration — the camera eases from its current framing to the whole-file fit. */
const FIT_DURATION_MS = 600;
/** When flying to a page from a coarse zoom, frame roughly this many cells across the viewport. */
const FLY_CELLS_ACROSS = 32;

const EMPTY_REQUEST: DbDetailRequest = { tileNodes: [], pages: [], chunks: [] };

/** Drag-gesture state, held in a ref so high-frequency mouse events never trigger React renders. */
interface DragState {
  mode: 'pan' | 'region' | 'minimap' | 'strip';
  startX: number;
  startY: number;
  startCam: Camera;
  moved: boolean;
}

/** Right-click context-menu state (§4.6) — null when the menu is closed. */
interface CtxMenuState {
  x: number;
  y: number;
  pageIndex: number;
  byteOffset: number;
  /** Owning segment id, or -1 when the cell belongs to no segment. */
  segmentId: number;
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
 * area-proportional page grid (A1) with the deep L3 chunk / L4 content bands (A2) and the A3 analysis surface —
 * lenses, the side rail, search / fly-to. Owns the 2D camera, drives the on-demand detail-tile fetch, and
 * routes selections to the shared Detail panel. Gesture transients live in refs (the profiler's rAF-coalesced
 * pattern) so pan / zoom stay at 60 fps.
 */
export default function DbMapPanel(_props: IDockviewPanelProps) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const encoding = useDbMapStore((s) => s.encoding);
  const segmentOverlay = useDbMapStore((s) => s.segmentOverlay);
  const toggleSegmentOverlay = useDbMapStore((s) => s.toggleSegmentOverlay);
  const lens = useDbMapStore((s) => s.lens);
  const lensSegmentId = useDbMapStore((s) => s.lensSegmentId);
  const filter = useDbMapStore((s) => s.filter);
  const pendingFocusType = useDbMapStore((s) => s.pendingFocusType);
  const clearPendingFocus = useDbMapStore((s) => s.clearPendingFocus);
  const selectDbMap = useDbMapSelectionStore((s) => s.select);
  const clearDbMapSelection = useDbMapSelectionStore((s) => s.clear);
  const bookmarksByDb = useDbMapStore((s) => s.bookmarks);
  const addBookmark = useDbMapStore((s) => s.addBookmark);
  const removeBookmark = useDbMapStore((s) => s.removeBookmark);
  const renameBookmark = useDbMapStore((s) => s.renameBookmark);

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
  // Active camera fly-to (§4.5) — when set, the animation loop steps it; any user gesture clears it.
  const tweenRef = useRef<CameraTween | null>(null);
  const animationRef = useRef<number | null>(null);

  const [hover, setHover] = useState<HoverInfo | null>(null);
  const [regionRect, setRegionRect] = useState<{ x: number; y: number; w: number; h: number } | null>(null);
  const [themeTick, setThemeTick] = useState(0);
  const [detailReq, setDetailReq] = useState<DbDetailRequest>(EMPTY_REQUEST);
  const [lod, setLod] = useState<{ band: 'L1' | 'L3' | 'L4'; focusedPage: number | null }>({
    band: 'L1',
    focusedPage: null,
  });
  const [searchQuery, setSearchQuery] = useState('');
  const [searchIndex, setSearchIndex] = useState(0);
  const [ctxMenu, setCtxMenu] = useState<CtxMenuState | null>(null);

  // The focused segment's directory — fetched only while the fragmentation lens has a segment (AC1 metrics).
  const segmentQuery = useDbMapSegment(sessionId, lens === 'fragmentation' ? lensSegmentId : null);

  // The fragmentation lens needs detail tiles for its segment's pages so the metrics card can report fill
  // density / reclaimable bytes even when the base encoding is coarse — request them alongside the viewport.
  const lensTileNodes = useMemo<number[]>(() => {
    if (lens !== 'fragmentation' || !segmentQuery.data || !data) {
      return [];
    }
    const nodes = new Set<number>();
    for (const p of segmentQuery.data.pages) {
      nodes.add(Math.floor(p / data.detailTileSize));
    }
    return [...nodes];
  }, [lens, segmentQuery.data, data]);

  const tileNodes = useMemo(() => {
    if (lensTileNodes.length === 0) {
      return detailReq.tileNodes;
    }
    const merged = new Set(detailReq.tileNodes);
    for (const node of lensTileNodes) {
      merged.add(node);
    }
    return [...merged];
  }, [detailReq.tileNodes, lensTileNodes]);

  // On-demand detail data — TanStack Query caches each tile / page / chunk, so panning back never refetches.
  const tiles = useDbMapTiles(sessionId, tileNodes);
  const pageDetails = useDbMapPages(sessionId, detailReq.pages);
  const chunkContents = useDbMapChunks(sessionId, detailReq.chunks);

  // ── Derived analysis state (A3) — computed from the StructuralMap the client already holds ──────────────

  const regions = useMemo(() => (data ? buildRegions(data, tiles) : []), [data, tiles]);
  const pathologyFlags = useMemo(() => (data ? findUnderFilledPages(data, tiles) : []), [data, tiles]);
  const composition = useMemo(() => (data ? freeSpaceComposition(data) : null), [data]);
  const searchMatches = useMemo(() => (data ? searchDbMap(searchQuery, data) : []), [searchQuery, data]);
  const filterMask = useMemo(() => (data ? buildFilterMask(data, filter) : null), [data, filter]);

  const metrics = useMemo<MetricsCardData | null>(() => {
    if (lens !== 'fragmentation' || lensSegmentId == null || !data) {
      return null;
    }
    const segMeta = data.segments.find((s) => s.id === lensSegmentId);
    const label = segMeta
      ? segMeta.typeName.length > 0
        ? `${segMeta.kind} #${segMeta.id} · ${segMeta.typeName}`
        : `${segMeta.kind} #${segMeta.id}`
      : `segment #${lensSegmentId}`;
    const seg = segmentQuery.data;
    if (!seg) {
      return {
        segmentLabel: label,
        loading: segmentQuery.isLoading,
        fragmentation: 0,
        fillDensity: 0,
        fillSampled: 0,
        segmentPageCount: 0,
        reclaimableBytes: 0,
        runs: [],
      };
    }
    const fill = fillDensity(seg.pages, tiles, data.detailTileSize);
    const reclaimable = segmentReclaimableBytes(seg.pages, tiles, data.detailTileSize, seg.stride);
    return {
      segmentLabel: label,
      loading: false,
      fragmentation: fragmentationPercent(seg.pages),
      fillDensity: fill.value,
      fillSampled: fill.sampledPages,
      segmentPageCount: seg.pages.length,
      reclaimableBytes: reclaimable.value,
      runs: contiguousRuns(seg.pages),
    };
  }, [lens, lensSegmentId, data, segmentQuery.data, segmentQuery.isLoading, tiles]);

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

  // ── Camera fly-to (§4.5) ────────────────────────────────────────────────────────────────────────────────

  // Steps the active tween once per frame; the existing scheduleRender loop is for static redraws, so the
  // tween runs its own rAF chain and hands back to queueDetailSync when it lands.
  const runTween = useCallback(() => {
    if (animationRef.current != null) {
      return;
    }
    const tick = () => {
      const tween = tweenRef.current;
      const renderer = rendererRef.current;
      const surface = surfaceRef.current;
      if (!tween || !renderer || !surface) {
        animationRef.current = null;
        return;
      }
      const { width, height } = surface.getBoundingClientRect();
      const { camera, done } = tweenCamera(tween, performance.now(), width, height);
      cameraRef.current = camera;
      renderer.setCamera(camera);
      renderer.render();
      if (done) {
        tweenRef.current = null;
        animationRef.current = null;
        queueDetailSync();
      } else {
        animationRef.current = requestAnimationFrame(tick);
      }
    };
    animationRef.current = requestAnimationFrame(tick);
  }, [queueDetailSync]);

  // Cancels any in-flight fly-to — called the moment the user takes the camera themselves.
  const cancelTween = useCallback(() => {
    tweenRef.current = null;
    if (animationRef.current != null) {
      cancelAnimationFrame(animationRef.current);
      animationRef.current = null;
    }
  }, []);

  const flyTo = useCallback(
    (target: Camera, durationMs: number = FLY_DURATION_MS, anchorX?: number, anchorY?: number) => {
      tweenRef.current = { from: cameraRef.current, to: target, startMs: performance.now(), durationMs, anchorX, anchorY };
      runTween();
    },
    [runTween],
  );

  // Records a discrete map navigation in the Workbench nav history (§13 A4 AC2) — `Alt+←/→` retraces it.
  const pushNav = useCallback((camera: Camera, label: string) => {
    useNavHistoryStore.getState().push({ kind: 'dbmap-navigated', camera, label, timestamp: Date.now() });
  }, []);

  const flyToPage = useCallback(
    (page: number) => {
      const renderer = rendererRef.current;
      const surface = surfaceRef.current;
      const layout = renderer?.getLayout();
      if (!renderer || !surface || !layout || page < 0 || page >= layout.pageCount) {
        return;
      }
      const { x, y } = hilbertD2XY(layout.order, page);
      const { width, height } = surface.getBoundingClientRect();
      // Keep the current depth if already zoomed in; otherwise zoom to a comfortable cell-level framing.
      const targetScale = Math.max(cameraRef.current.scale, Math.min(width, height) / FLY_CELLS_ACROSS);
      const target = cameraCenteredOn(layout.dataRect.x + x + 0.5, layout.dataRect.y + y + 0.5, targetScale, width, height);
      pushNav(target, `Page ${page.toLocaleString()}`);
      flyTo(target);
    },
    [flyTo, pushNav],
  );

  const flyToRegion = useCallback(
    (startPage: number, pageCount: number) => {
      const renderer = rendererRef.current;
      const surface = surfaceRef.current;
      const layout = renderer?.getLayout();
      if (!renderer || !surface || !layout || pageCount <= 0) {
        return;
      }
      // Bounding box of the run's Hilbert cells — sample-capped so a huge run stays cheap.
      let minX = Infinity;
      let minY = Infinity;
      let maxX = -Infinity;
      let maxY = -Infinity;
      const step = Math.max(1, Math.floor(pageCount / 4096));
      for (let p = startPage; p < startPage + pageCount && p < layout.pageCount; p += step) {
        const { x, y } = hilbertD2XY(layout.order, p);
        if (x < minX) minX = x;
        if (y < minY) minY = y;
        if (x > maxX) maxX = x;
        if (y > maxY) maxY = y;
      }
      if (!Number.isFinite(minX)) {
        return;
      }
      const world = {
        x: layout.dataRect.x + minX,
        y: layout.dataRect.y + minY,
        w: maxX - minX + 1,
        h: maxY - minY + 1,
      };
      const { width, height } = surface.getBoundingClientRect();
      const target = zoomToWorldRect(world, width, height, FIT_PADDING);
      pushNav(target, `Region @${startPage.toLocaleString()}`);
      flyTo(target);
    },
    [flyTo, pushNav],
  );

  // Publish the camera fly-to so an `Alt+←/→` nav-history restore can drive it (§13 A4 AC2).
  useEffect(() => {
    registerDbMapCameraRestore((cam) => flyTo(cam));
    return () => registerDbMapCameraRestore(null);
  }, [flyTo]);

  // ── Bookmarks (§4.5 / A4 AC3) — persisted per database in useDbMapStore ─────────────────────────────────

  const bookmarks = data ? (bookmarksByDb[data.databaseName] ?? []) : [];

  const addCurrentBookmark = useCallback(() => {
    if (!data) {
      return;
    }
    const count = useDbMapStore.getState().bookmarks[data.databaseName]?.length ?? 0;
    addBookmark(data.databaseName, {
      id: newBookmarkId(),
      label: `View ${count + 1}`,
      camera: { ...cameraRef.current },
      createdAt: Date.now(),
    });
  }, [data, addBookmark]);

  // Cross-link entry (§7.3 / A4 AC1) — a Resource Explorer / Schema Inspector "Show in File Map" sets a
  // pending component type; once the map is loaded, focus that component's segment (fragmentation lens) and
  // fly there, then clear the request so a later panel re-render does not re-trigger it.
  useEffect(() => {
    if (!data || !pendingFocusType) {
      return;
    }
    const seg = data.segments.find((s) => s.typeName === pendingFocusType);
    clearPendingFocus();
    if (seg) {
      useDbMapStore.getState().focusSegment(seg.id);
      flyToPage(seg.rootPageIndex);
    }
  }, [data, pendingFocusType, clearPendingFocus, flyToPage]);

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

  // Lens change — recompute the per-page highlight mask and hand it to the renderer (§4.3). The mask is
  // O(pageCount) but rebuilt only on a lens / focus / data / tile change, never per frame.
  useEffect(() => {
    const renderer = rendererRef.current;
    if (!renderer) {
      return;
    }
    if (!data || lens === 'none') {
      renderer.setLens('none', null);
      scheduleRender();
      return;
    }
    const mask = new Uint8Array(data.pageCount);
    if (lens === 'fragmentation') {
      if (lensSegmentId != null) {
        for (let p = 0; p < data.pageCount; p++) {
          if (data.ownerSegmentId[p] === lensSegmentId) {
            mask[p] = 1;
          }
        }
      }
    } else if (lens === 'freeSpace') {
      for (let p = 0; p < data.pageCount; p++) {
        if (data.pageType[p] === DbPageType.Free) {
          mask[p] = 1;
        }
      }
      // Refine with internally-fragmented (low-fill) pages wherever a detail tile is resident.
      for (const tile of tiles.values()) {
        for (let i = 0; i < tile.pageCount; i++) {
          if (tile.chunkTotal[i] > 0 && tile.fillRatio[i] / 255 < LOW_FILL_THRESHOLD) {
            mask[tile.firstPage + i] = 1;
          }
        }
      }
    } else if (lens === 'pathology') {
      for (const flag of pathologyFlags) {
        mask[flag.pageIndex] = 1;
      }
    }
    renderer.setLens(lens, mask);
    scheduleRender();
  }, [lens, lensSegmentId, data, tiles, pathologyFlags, scheduleRender]);

  // Filter-to-dim changed — hand the renderer the new pass/fail mask (§4.6). The mask is O(pageCount) but
  // recomputed only on a filter / data change, and composes on top of the active lens inside the renderer.
  useEffect(() => {
    const renderer = rendererRef.current;
    if (!renderer) {
      return;
    }
    renderer.setFilter(filterMask);
    scheduleRender();
  }, [filterMask, scheduleRender]);

  // Search matches changed — mark them on the map; the camera fly-to is driven explicitly by Enter / cycle.
  useEffect(() => {
    const renderer = rendererRef.current;
    if (!renderer) {
      return;
    }
    renderer.setSearchHits(
      searchMatches.map((m) => m.pageIndex),
      searchMatches.length > 0 ? searchIndex : -1,
    );
    scheduleRender();
  }, [searchMatches, searchIndex, scheduleRender]);

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

  // Resize — keep the canvas backing store in sync with the surface (also fires when the side rail collapses).
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
      const step = e.ctrlKey ? 1.5 : 1.3;
      const factor = e.deltaY < 0 ? step : 1 / step;
      // Ease each wheel notch through the camera tween — the same mechanism as fly-to. A new notch zooms
      // relative to the in-flight tween's destination so rapid notches accumulate; the short glide keeps
      // it responsive (consecutive notches redirect the tween rather than restarting from a standstill).
      // The cursor is the tween anchor — without it the centre-anchored glide makes the focus point wobble.
      const base = tweenRef.current ? tweenRef.current.to : cameraRef.current;
      flyTo(zoomAt(base, pt.x, pt.y, factor), WHEEL_ZOOM_DURATION_MS, pt.x, pt.y);
    };
    canvas.addEventListener('wheel', onWheel, { passive: false });
    return () => canvas.removeEventListener('wheel', onWheel);
  }, [flyTo]);

  // Drop any pending detail-sync timer / fly-to on unmount.
  useEffect(
    () => () => {
      if (detailSyncRef.current != null) {
        window.clearTimeout(detailSyncRef.current);
      }
      if (animationRef.current != null) {
        cancelAnimationFrame(animationRef.current);
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
    // Ease from the current framing to the whole-file fit (centre-anchored — the natural fit anchor).
    flyTo(fitToRect(layout.worldBounds, width, height, FIT_PADDING), FIT_DURATION_MS);
  }, [flyTo]);

  // ── Export (§4.6 / A4 AC5) ──────────────────────────────────────────────────────────────────────────────

  const exportViewPngNow = useCallback(() => {
    if (canvasRef.current && data) {
      exportViewPng(canvasRef.current, data.databaseName);
    }
  }, [data]);

  const exportMapPngNow = useCallback(() => {
    const image = rendererRef.current?.getWholeMapImage();
    if (image && data) {
      exportWholeMapPng(image, data.databaseName);
    }
  }, [data]);

  const exportCsvNow = useCallback(() => {
    if (data) {
      exportRegionsCsv(regions, data.databaseName);
    }
  }, [data, regions]);

  // ── Search interaction ──────────────────────────────────────────────────────────────────────────────────

  const goToMatch = useCallback(
    (index: number) => {
      if (searchMatches.length === 0) {
        return;
      }
      const i = ((index % searchMatches.length) + searchMatches.length) % searchMatches.length;
      setSearchIndex(i);
      flyToPage(searchMatches[i].pageIndex);
    },
    [searchMatches, flyToPage],
  );

  // ── Mouse interaction ───────────────────────────────────────────────────────────────────────────────────
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
      cameraRef.current = cameraCenteredOn(world.x, world.y, cameraRef.current.scale, width, height);
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
      cameraRef.current = cameraCenteredOn(
        layout.dataRect.x + x + 0.5,
        layout.dataRect.y + y + 0.5,
        cameraRef.current.scale,
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
      // With the fragmentation lens active, a page click focuses its owning segment — the AC1 entry point.
      const store = useDbMapStore.getState();
      const segId = data.ownerSegmentId[page];
      if (store.lens === 'fragmentation' && segId !== NO_SEGMENT) {
        store.focusSegment(segId);
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
    // Middle button → fit the whole file (same as `f` / the Fit button). preventDefault suppresses the
    // browser's middle-click autoscroll cursor.
    if (e.button === 1) {
      e.preventDefault();
      fitWholeFile();
      return;
    }
    cancelTween();
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

  const handleContextMenu = (e: React.MouseEvent<HTMLCanvasElement>) => {
    e.preventDefault();
    const canvas = canvasRef.current;
    const renderer = rendererRef.current;
    if (!canvas || !renderer || !data) {
      return;
    }
    const pt = canvasPoint(canvas, e.clientX, e.clientY);
    const page = renderer.pageAt(pt.x, pt.y);
    if (page == null) {
      setCtxMenu(null);
      return;
    }
    const segId = data.ownerSegmentId[page];
    setCtxMenu({
      x: e.clientX,
      y: e.clientY,
      pageIndex: page,
      // A down-sampled cell stands for `downSampleFactor` pages — the offset is the first page of the cell.
      byteOffset: page * PAGE_SIZE * data.downSampleFactor,
      segmentId: segId === NO_SEGMENT ? -1 : segId,
    });
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
    } else if (e.key === 'b' || e.key === 'B') {
      addCurrentBookmark();
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
      <DbMapToolbar
        onFit={fitWholeFile}
        onRefresh={() => void refetch()}
        onExportViewPng={exportViewPngNow}
        onExportMapPng={exportMapPngNow}
        onExportCsv={exportCsvNow}
        search={searchQuery}
        onSearchChange={(v) => {
          setSearchQuery(v);
          setSearchIndex(0);
        }}
        onSearchSubmit={() => goToMatch(searchIndex)}
        onSearchPrev={() => goToMatch(searchIndex - 1)}
        onSearchNext={() => goToMatch(searchIndex + 1)}
        searchMatchCount={searchMatches.length}
        searchMatchIndex={searchIndex}
      />

      <div className="border-b border-border px-3 py-1 text-[11px] text-muted-foreground" data-testid="dbmap-breadcrumb">
        {data ? (
          <span>
            <span className="font-mono text-foreground">{data.databaseName}</span>
            {' · '}
            {data.pageCount.toLocaleString()} pages · {formatFileSize(data.dataFileBytes)}
            {data.walBytes > 0 ? (
              <>
                {' · '}
                <button
                  type="button"
                  disabled
                  className="cursor-not-allowed text-muted-foreground opacity-70"
                  title="Open in WAL Events — the WAL Events module (Module 08) is not yet available"
                >
                  WAL {formatFileSize(data.walBytes)}
                </button>
              </>
            ) : (
              ' · no WAL'
            )}
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

      <div className="flex min-h-0 flex-1">
        <div ref={surfaceRef} className="relative min-h-0 min-w-0 flex-1 overflow-hidden">
          <canvas
            ref={canvasRef}
            onMouseDown={handleMouseDown}
            onMouseMove={handleHoverMove}
            onMouseLeave={handleHoverLeave}
            onContextMenu={handleContextMenu}
            style={{ display: 'block', cursor: 'crosshair' }}
            data-testid="dbmap-canvas"
          />
          {regionRect && (
            <div
              className="pointer-events-none absolute border border-primary bg-primary/10"
              style={{ left: regionRect.x, top: regionRect.y, width: regionRect.w, height: regionRect.h }}
            />
          )}
          {isLoading && <p className="absolute left-3 top-2 text-[11px] text-muted-foreground">Loading map…</p>}
          {isError && (
            <p className="absolute left-3 top-2 text-[11px] text-destructive">Failed to load the file map.</p>
          )}
          {hover && <HoverTooltip info={hover} />}
          {ctxMenu &&
            (() => {
              // Reveal / open-in-schema work component-by-type — enabled only for a component segment.
              const ctxType = data?.segments.find((s) => s.id === ctxMenu.segmentId)?.typeName ?? '';
              return (
                <DbMapContextMenu
                  x={ctxMenu.x}
                  y={ctxMenu.y}
                  pageIndex={ctxMenu.pageIndex}
                  byteOffset={ctxMenu.byteOffset}
                  segmentId={ctxMenu.segmentId}
                  onClose={() => setCtxMenu(null)}
                  onReveal={ctxType ? () => revealComponentInResourceTree(ctxType) : undefined}
                  onOpenInSchema={ctxType ? () => openComponentInSchema(ctxType) : undefined}
                />
              );
            })()}
        </div>

        <DbMapSidePanel
          legend={
            <LegendTab
              downSampleFactor={data?.downSampleFactor ?? 1}
              metrics={metrics}
              composition={composition}
              pathologies={pathologyFlags}
              segments={data?.segments ?? []}
              onFlyToPage={flyToPage}
            />
          }
          regions={<RegionsTab regions={regions} segments={data?.segments ?? []} onFlyToRegion={flyToRegion} />}
          bookmarks={
            <BookmarksTab
              bookmarks={bookmarks}
              hasMap={!!data}
              onAddCurrent={addCurrentBookmark}
              onFlyTo={(b) => {
                pushNav(b.camera, b.label);
                flyTo(b.camera);
              }}
              onRemove={(id) => data && removeBookmark(data.databaseName, id)}
              onRename={(id, label) => data && renameBookmark(data.databaseName, id, label)}
            />
          }
        />
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

function canvasPoint(canvas: HTMLCanvasElement, clientX: number, clientY: number): { x: number; y: number } {
  const rect = canvas.getBoundingClientRect();
  return { x: clientX - rect.left, y: clientY - rect.top };
}

function pointIn(pt: { x: number; y: number }, r: { x: number; y: number; w: number; h: number }): boolean {
  return pt.x >= r.x && pt.x < r.x + r.w && pt.y >= r.y && pt.y < r.y + r.h;
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
