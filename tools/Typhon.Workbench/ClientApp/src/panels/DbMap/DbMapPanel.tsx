import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useDbMapStore } from '@/stores/useDbMapStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { isDbMapLeafType } from '@/libs/dbmap/dbMapSelection';
import { useDbMapOverlayStore } from '@/stores/useDbMapOverlayStore';
import { useDbMap } from '@/hooks/dbmap/useDbMap';
import { useDbMapChunks, useDbMapPages, useDbMapTiles } from '@/hooks/dbmap/useDbMapDetail';
import { useDbMapSegment } from '@/hooks/dbmap/useDbMapSegment';
import { formatFileSize } from '@/lib/formatters';
import { DbMapRenderer, SEGMENT_PULSE_MS, type DbDetailRequest, type DbMapTheme } from '@/libs/dbmap/dbMapRenderer';
import type { L0Stripe } from '@/libs/dbmap/dbMapL0';
import { rgbCss } from '@/libs/dbmap/dbMapColors';
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
import { shouldFitViewport } from '@/libs/dbmap/initialFit';
import { hilbertD2XY } from '@/libs/dbmap/hilbert';
import {
  DbPageType,
  NO_SEGMENT,
  PAGE_SIZE,
  PAGE_TYPE_LABELS,
  type DbChunkContent,
  type DbMapData,
  type DbPageDetail,
} from '@/libs/dbmap/types';
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
import { emptyCameraHistory, pushCameraHistory, stepCameraHistory, type CameraHistory } from '@/libs/dbmap/dbMapNavHistory';
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
/** Idle window after the last wheel notch before the settled framing is committed as one nav-history entry. */
const WHEEL_NAV_SETTLE_MS = 250;
/** Fit-whole-file glide duration — the camera eases from its current framing to the whole-file fit. */
const FIT_DURATION_MS = 600;
/** When flying to a page from a coarse zoom, frame roughly this many cells across the viewport. */
const FLY_CELLS_ACROSS = 32;
/** Mouse must stay still this long before the on-surface tooltip appears (any motion resets the timer). */
const TOOLTIP_DELAY_MS = 3000;
/**
 * Lead time before a fly-to's destination detail tier is prefetched. Short — well under any fly duration —
 * so the fetch overlaps most of the camera flight, but long enough that rapid wheel notches (which redirect
 * the in-flight tween) coalesce onto the final destination instead of firing a request per notch.
 */
const PREFETCH_LEAD_MS = 80;

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
  /**
   * Chunk under the cursor at L3/L4 (proposal 5). `chunkId` is the global id; `byteOffset` / `sizeBytes` come
   * from the decoded L4 content and are present only when that chunk's content is resident.
   */
  chunk?: {
    chunkId: number;
    indexInPage: number;
    occupied: boolean;
    sizeBytes?: number;
    byteOffset?: number;
    /** Intra-chunk fill 0..255 for container-kind chunks (A6, e.g. a cluster); absent for slot-like chunks. */
    fill?: number;
    /** Live / total entity slots when the hovered chunk is a cluster (A6); absent otherwise. */
    slotsLive?: number;
    slotsTotal?: number;
  };
  /** Governed file-page range when the hovered page is an occupancy page (A6, §10.2). */
  occupancy?: { first: number; count: number };
}

/** Transient L0 stripe hover — drives the L0 tooltip variant. */
interface L0HoverInfo {
  stripe: L0Stripe;
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
  const pageOrder = useDbMapStore((s) => s.pageOrder);
  const segmentOverlay = useDbMapStore((s) => s.segmentOverlay);
  const toggleSegmentOverlay = useDbMapStore((s) => s.toggleSegmentOverlay);
  const residencyOverlay = useDbMapStore((s) => s.residencyOverlay);
  const toggleResidencyOverlay = useDbMapStore((s) => s.toggleResidencyOverlay);
  const regionCaptions = useDbMapStore((s) => s.regionCaptions);
  const toggleRegionCaptions = useDbMapStore((s) => s.toggleRegionCaptions);
  const lens = useDbMapStore((s) => s.lens);
  const lensSegmentId = useDbMapStore((s) => s.lensSegmentId);
  const filter = useDbMapStore((s) => s.filter);
  const pendingFocusType = useDbMapStore((s) => s.pendingFocusType);
  const clearPendingFocus = useDbMapStore((s) => s.clearPendingFocus);
  const select = useSelectionStore((s) => s.select);
  const clearLeaf = useSelectionStore((s) => s.clearLeaf);
  // Clicking empty space (or Esc) deselects the map — but only clear the bus leaf when it currently holds
  // a File-Map object, so we never wipe a leaf another panel owns (e.g. a component picked in Schema).
  const clearDbMapSelection = useCallback(() => {
    const leaf = useSelectionStore.getState().leaf;
    if (leaf && isDbMapLeafType(leaf.type)) {
      clearLeaf();
    }
  }, [clearLeaf]);
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
  // Predictive-prefetch timer — debounces the destination detail fetch kicked off when a fly-to begins.
  const prefetchRef = useRef<number | null>(null);
  // Tooltip dwell — refs hold the latest hover intent + timer so the React tooltip state only flips on after
  // {@link TOOLTIP_DELAY_MS} of mouse stillness. Any move resets the timer; mouseleave / drag clear both.
  const tooltipTimerRef = useRef<number | null>(null);
  const hoverIntentRef = useRef<{ kind: 'page'; data: HoverInfo } | { kind: 'l0'; data: L0HoverInfo } | null>(null);
  // The camera is fit to the file only on first load — a later refresh (or refetch) keeps the user's viewport.
  const fittedRef = useRef(false);
  // Active camera fly-to (§4.5) — when set, the animation loop steps it; any user gesture clears it.
  const tweenRef = useRef<CameraTween | null>(null);
  const animationRef = useRef<number | null>(null);
  // Panel-local camera back/forward history (mouse thumb buttons). Independent of the global Workbench nav
  // history, which records only cross-panel jumps. A wheel burst settles into one entry via wheelSettleRef.
  const historyRef = useRef<CameraHistory>(emptyCameraHistory());
  const wheelSettleRef = useRef<number | null>(null);
  // rAF chain that keeps repainting while freshly-arrived L4 chunk content is fading in (so it eases, not pops).
  const contentFadeRef = useRef<number | null>(null);
  // rAF chain that repaints for the lifetime of a post-reveal segment pulse (the renderer is pure-draw).
  const pulseRafRef = useRef<number | null>(null);

  const [hover, setHover] = useState<HoverInfo | null>(null);
  const [l0Hover, setL0Hover] = useState<L0HoverInfo | null>(null);
  const [regionRect, setRegionRect] = useState<{ x: number; y: number; w: number; h: number } | null>(null);
  const [themeTick, setThemeTick] = useState(0);
  const [detailReq, setDetailReq] = useState<DbDetailRequest>(EMPTY_REQUEST);
  const [lod, setLod] = useState<{
    /** Underlying renderer band (used for detail-tier orchestration). */
    band: 'L1' | 'L3' | 'L4';
    /** Visually dominant band — L0 is folded in for the per-band legend chrome. */
    displayBand: 'L0' | 'L1' | 'L3' | 'L4';
    focusedPage: number | null;
  }>({
    band: 'L1',
    displayBand: 'L0',
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
      const displayBand = renderer.getDisplayBand();
      setLod((prev) =>
        prev.band === lodState.band && prev.displayBand === displayBand && prev.focusedPage === focused
          ? prev
          : { band: lodState.band, displayBand, focusedPage: focused },
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

  // Repaints each frame while any L4 chunk content is mid fade-in, then stops. Separate from the tween loop because
  // content typically arrives *after* the camera has settled (its decode is a two-hop fetch), so there's no tween
  // running to drive the fade. No-op while a tween is active — that loop already repaints every frame.
  const runContentFade = useCallback(() => {
    if (contentFadeRef.current != null || animationRef.current != null) {
      return;
    }
    const tick = () => {
      const renderer = rendererRef.current;
      if (!renderer) {
        contentFadeRef.current = null;
        return;
      }
      renderer.setCamera(cameraRef.current);
      renderer.render();
      if (renderer.hasFadingContent()) {
        contentFadeRef.current = requestAnimationFrame(tick);
      } else {
        contentFadeRef.current = null;
      }
    };
    contentFadeRef.current = requestAnimationFrame(tick);
  }, []);

  // Cancels any in-flight fly-to — called the moment the user takes the camera themselves.
  const cancelTween = useCallback(() => {
    tweenRef.current = null;
    if (animationRef.current != null) {
      cancelAnimationFrame(animationRef.current);
      animationRef.current = null;
    }
  }, []);

  // Predictive prefetch (§5 — smooth L1→L3): when a fly-to begins, fetch the *destination's* detail tier now,
  // while the camera is still animating toward it, so the deep bands are resident (or in flight) by the time
  // the tween lands rather than blank-then-popping after it settles + the DETAIL_SYNC_MS debounce. Debounced
  // so a burst of wheel notches that keep redirecting the tween coalesces onto the final destination. Only
  // detailReq is touched — the LOD band / breadcrumb still track the live camera and update at settle time.
  const prefetchForCamera = useCallback((target: Camera) => {
    if (prefetchRef.current != null) {
      window.clearTimeout(prefetchRef.current);
    }
    prefetchRef.current = window.setTimeout(() => {
      prefetchRef.current = null;
      const renderer = rendererRef.current;
      if (!renderer) {
        return;
      }
      const req = renderer.getDetailRequestForCamera(target);
      setDetailReq((prev) => (sameRequest(prev, req) ? prev : req));
    }, PREFETCH_LEAD_MS);
  }, []);

  const flyTo = useCallback(
    (target: Camera, durationMs: number = FLY_DURATION_MS, anchorX?: number, anchorY?: number) => {
      const from = cameraRef.current;
      // When the caller doesn't pin an anchor (the cursor, for a wheel zoom), default to the zoom's *invariant point*
      // — the screen point whose world coordinate is identical in `from` and `to`. Anchoring the glide there makes it
      // a clean monotonic zoom; the centre-anchored default otherwise swoops (the focus bulges off-and-back) whenever
      // the move both zooms and pans, e.g. double-click-to-fit. Pure pans (equal scale) keep the centre anchor.
      if (anchorX === undefined && anchorY === undefined) {
        const denom = from.scale - target.scale;
        if (Math.abs(denom) > 1e-6) {
          anchorX = (target.x * from.scale - from.x * target.scale) / denom;
          anchorY = (target.y * from.scale - from.y * target.scale) / denom;
        }
      }
      tweenRef.current = { from, to: target, startMs: performance.now(), durationMs, anchorX, anchorY };
      runTween();
      prefetchForCamera(target);
    },
    [runTween, prefetchForCamera],
  );

  // Records a discrete map navigation in the Workbench nav history (§13 A4 AC2) — `Alt+←/→` retraces it.
  // Reserved for cross-panel entry points (the Schema/Data "Show in File Map" cross-link); in-panel pan/zoom
  // goes to the panel-local stack below instead, so the global history isn't flooded with map exploration.
  // The first fly after a reveal folds into the just-opened File Map Back stop (recordDbMapNav coalesces with
  // the reveal's panel-opened entry) so a reveal is ONE Back, not two. 'dbmap' is this panel's dock id.
  const pushNav = useCallback((camera: Camera, label: string) => {
    useNavHistoryStore.getState().recordDbMapNav(camera, label, 'dbmap');
  }, []);

  // Records a camera state in the panel-local back/forward stack (the mouse-thumb-button history).
  const pushHistory = useCallback((camera: Camera) => {
    historyRef.current = pushCameraHistory(historyRef.current, camera);
  }, []);

  // Walks the panel-local history by `dir` (−1 back, +1 forward) and eases to that camera. No-op at either end.
  // A pending wheel-settle push would otherwise land on the wrong slot once we move the pointer — drop it.
  const stepHistory = useCallback(
    (dir: -1 | 1) => {
      if (wheelSettleRef.current != null) {
        window.clearTimeout(wheelSettleRef.current);
        wheelSettleRef.current = null;
      }
      const stepped = stepCameraHistory(historyRef.current, dir);
      if (stepped === historyRef.current) {
        return; // at an end — nothing to navigate to
      }
      historyRef.current = stepped;
      flyTo(stepped.entries[stepped.pointer]);
    },
    [flyTo],
  );

  const flyToPage = useCallback(
    (page: number, globalLabel?: string) => {
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
      pushHistory(target);
      // Only a cross-panel entry point passes a label — it also records in the global history so `Alt+←/→`
      // retraces the panel-to-panel jump. In-panel jumps (search, sidebar lists) stay local-only.
      if (globalLabel) {
        pushNav(target, globalLabel);
      }
      flyTo(target);
    },
    [flyTo, pushNav, pushHistory],
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
      pushHistory(target);
      flyTo(target);
    },
    [flyTo, pushHistory],
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
  // pending component type; once the map is loaded, fly to that component's segment and select it (so the
  // Inspector shows it), then clear the request so a later panel re-render does not re-trigger it. A reveal is
  // *spatial* (file-map.md §6: "fly to its segment") — it deliberately does NOT switch the analytical lens;
  // Fragmentation stays a user choice via the Lens combo.
  // Flash the just-revealed segment's pages so the matched zone is obvious. The renderer draws the pulse from
  // its start time; this rAF chain keeps frames coming for the pulse window. While a camera fly-to is animating
  // its own loop already repaints (don't double-render); once it settles, this keeps the pulse alive at rest.
  const pulseSegment = useCallback((segmentId: number) => {
    const renderer = rendererRef.current;
    if (!renderer) {
      return;
    }
    const start = performance.now();
    renderer.setSegmentPulse(segmentId, start);
    if (pulseRafRef.current != null) {
      cancelAnimationFrame(pulseRafRef.current);
    }
    const step = () => {
      const r = rendererRef.current;
      if (!r) {
        pulseRafRef.current = null;
        return;
      }
      if (tweenRef.current == null) {
        r.render();
      }
      if (performance.now() - start < SEGMENT_PULSE_MS) {
        pulseRafRef.current = requestAnimationFrame(step);
      } else {
        r.setSegmentPulse(null, 0);
        r.render();
        pulseRafRef.current = null;
      }
    };
    pulseRafRef.current = requestAnimationFrame(step);
  }, []);

  useEffect(() => {
    if (!data || !pendingFocusType) {
      return;
    }
    const seg = data.segments.find((s) => s.typeName === pendingFocusType);
    clearPendingFocus();
    if (seg) {
      select('segment', { kind: 'segment', segmentId: seg.id, typeName: seg.typeName || undefined });
      flyToPage(seg.rootPageIndex, `Component ${pendingFocusType}`);
      pulseSegment(seg.id);
    }
  }, [data, pendingFocusType, clearPendingFocus, flyToPage, select, pulseSegment]);

  // Construct the renderer once the canvas element exists.
  useLayoutEffect(() => {
    if (!canvasRef.current) {
      return;
    }
    rendererRef.current = new DbMapRenderer(canvasRef.current);
    rendererRef.current.setTheme(readDbMapTheme());
    // Cursor is driven imperatively by the gesture/hover handlers (default at rest); not a React-managed style
    // prop, so a re-render mid-gesture can't reset it.
    canvasRef.current.style.cursor = 'default';
  }, []);

  // Normalize a stale persisted lens on mount: `lens` is persisted but `lensSegmentId` is not, so a fragmentation
  // lens restored without a segment would leave the Lens combo stuck on "Fragmentation" with no actual overlay
  // (and reappear after every remount, e.g. a pane resize). Reset it to "None" so the combo reflects reality.
  useEffect(() => {
    const s = useDbMapStore.getState();
    if (s.lens === 'fragmentation' && s.lensSegmentId == null) {
      s.setLens('none');
    }
  }, []);

  // Track <html>'s class attribute — ThemeProvider toggles `.dark` there; a tick triggers the redraw.
  useEffect(() => {
    const observer = new MutationObserver(() => setThemeTick((n) => n + 1));
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
    return () => observer.disconnect();
  }, []);

  // Frame the whole file the first time we have BOTH data and a real surface. Fits exactly once (`fittedRef`)
  // so later resizes / refreshes preserve the user's framing, and never fights an in-flight fly-to (a
  // cross-link reveal owns the camera via its tween). Mounting a dockview panel while it is the *inactive*
  // tab gives it a 0×0 box, so the data-driven call below can't fit then — the ResizeObserver retries this
  // when the panel gets its first real size on activation. Without that retry the camera stays at its
  // `{scale:1, x:0, y:0}` default and the file renders ~90% off the top-left ([shouldFitViewport]).
  const applyInitialFit = useCallback(() => {
    const renderer = rendererRef.current;
    const surface = surfaceRef.current;
    if (!renderer || !surface) {
      return;
    }
    const layout = renderer.getLayout();
    if (!layout) {
      return;
    }
    const { width, height } = surface.getBoundingClientRect();
    if (!shouldFitViewport({ hasData: !!data, alreadyFitted: fittedRef.current, flying: !!tweenRef.current, width, height })) {
      return;
    }
    cameraRef.current = fitToRect(layout.worldBounds, width, height, FIT_PADDING);
    fittedRef.current = true;
    // Seed the local back/forward stack with the initial fit so the user can always step back to it — but only
    // if nothing landed first (a cross-link open flies before this runs and must keep its entry).
    if (historyRef.current.pointer < 0) {
      historyRef.current = pushCameraHistory(historyRef.current, cameraRef.current);
    }
    renderer.setCamera(cameraRef.current);
    renderer.render();
  }, [data]);

  // Latest fit fn behind a ref so the (never-resubscribed) ResizeObserver can call it without a stale `data`.
  const applyInitialFitRef = useRef(applyInitialFit);
  useEffect(() => {
    applyInitialFitRef.current = applyInitialFit;
  }, [applyInitialFit]);

  // Push the decoded map into the renderer and frame it on data change. The encoding / overlay are applied by
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
    if (!data) {
      fittedRef.current = false;
      historyRef.current = emptyCameraHistory();
    } else {
      applyInitialFit();
    }
    renderer.setCamera(cameraRef.current);
    renderer.render();
  }, [data, applyInitialFit]);

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

  // Page-layout ordering (Hilbert vs sequential) — re-lays out the grid in place. Pages move under the camera, so
  // re-sync detail tiles for the newly-visible set; no reframe (page-index selection / hover stay valid).
  useEffect(() => {
    const renderer = rendererRef.current;
    if (!renderer) {
      return;
    }
    renderer.setPageOrder(pageOrder);
    scheduleRender();
    queueDetailSync();
  }, [pageOrder, scheduleRender, queueDetailSync]);

  // Per-component overlay (A6) — push the picker's selection to the renderer; it recolours the L4 cluster slots
  // of the chosen segment by the component's enabled bit. Cleared on DB change so a stale segment id can't carry over.
  const overlaySegmentId = useDbMapOverlayStore((s) => s.segmentId);
  const overlayComponentSlot = useDbMapOverlayStore((s) => s.componentSlot);
  const clearOverlay = useDbMapOverlayStore((s) => s.clear);
  useEffect(() => {
    const renderer = rendererRef.current;
    if (!renderer) {
      return;
    }
    renderer.setComponentOverlay(
      overlaySegmentId != null && overlayComponentSlot != null
        ? { segmentId: overlaySegmentId, componentSlot: overlayComponentSlot }
        : null,
    );
    scheduleRender();
  }, [overlaySegmentId, overlayComponentSlot, scheduleRender]);

  useEffect(() => {
    clearOverlay();
  }, [data?.databaseName, clearOverlay]);

  // Lens change — recompute the per-page highlight mask and hand it to the renderer (§4.3). The mask is
  // O(pageCount) but rebuilt only on a lens / focus / data / tile change, never per frame.
  useEffect(() => {
    const renderer = rendererRef.current;
    if (!renderer) {
      return;
    }
    // The fragmentation lens needs a focused segment; without one (e.g. restored from a persisted session where
    // the segment id no longer applies) treat it as no lens so the map isn't dimmed-to-nothing on open.
    if (!data || lens === 'none' || (lens === 'fragmentation' && lensSegmentId == null)) {
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

  // Pathology flags drive both the L0 badge (count) and the L1 per-page marker (page lookup against the
  // set) — fed in one shot.
  useEffect(() => {
    const renderer = rendererRef.current;
    if (!renderer) {
      return;
    }
    renderer.setPathologyFlags(pathologyFlags);
    scheduleRender();
  }, [pathologyFlags, scheduleRender]);

  // L1 enhancement #3 + #7 — feed the residency-overlay toggle, region-caption toggle, and the regions
  // array (already computed by the buildRegions memo) into the renderer.
  useEffect(() => {
    const renderer = rendererRef.current;
    if (!renderer) {
      return;
    }
    renderer.setResidencyOverlay(residencyOverlay);
    renderer.setRegionCaptions(regionCaptions);
    scheduleRender();
  }, [residencyOverlay, regionCaptions, scheduleRender]);

  useEffect(() => {
    const renderer = rendererRef.current;
    if (!renderer) {
      return;
    }
    renderer.setRegions(regions);
    scheduleRender();
  }, [regions, scheduleRender]);

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
    // Newly-arrived chunk content fades in; drive a repaint chain until the fade completes (else a single repaint).
    if (renderer.hasFadingContent()) {
      runContentFade();
    } else {
      scheduleRender();
    }
    // Re-derive the request *immediately* on data arrival (no debounce): the L4 chunk fetch depends on page details,
    // so the instant those land we issue the chunk request — waiting out DETAIL_SYNC_MS here was the main source of
    // the zoom-in content lag. Crucially, derive it for the tween *destination* (a fixed point), not the live camera:
    // syncing against the moving camera churns the visible chunk set frame-to-frame, which flickers content on/off.
    const target = tweenRef.current?.to ?? cameraRef.current;
    const req = renderer.getDetailRequestForCamera(target);
    setDetailReq((prev) => (sameRequest(prev, req) ? prev : req));
  }, [tiles, pageDetails, chunkContents, scheduleRender, runContentFade]);

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
      // Retry the one-time fit here: when the panel was mounted as an inactive (0×0) tab, this is where it
      // first gets real dimensions — without it the file stays framed ~90% off the top-left on activation.
      applyInitialFitRef.current();
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
      // Coalesce a wheel burst into one nav-history entry — record the settled framing once notches stop.
      if (wheelSettleRef.current != null) {
        window.clearTimeout(wheelSettleRef.current);
      }
      wheelSettleRef.current = window.setTimeout(() => {
        wheelSettleRef.current = null;
        pushHistory(tweenRef.current?.to ?? cameraRef.current);
      }, WHEEL_NAV_SETTLE_MS);
    };
    canvas.addEventListener('wheel', onWheel, { passive: false });
    return () => canvas.removeEventListener('wheel', onWheel);
  }, [flyTo, pushHistory]);

  // Mouse thumb buttons walk the panel-local history while the cursor is over the map: 3 = back, 4 = forward,
  // Ctrl/Shift+3 = forward (for mice without a forward button). `stopPropagation` keeps the global mouse-nav
  // handler (useKeyboardShortcuts) from also firing, so back/forward acts on whichever panel the cursor is over —
  // the Critical Path model. `auxclick`/`mousedown` preventDefault suppresses the browser's own back/forward.
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) {
      return;
    }
    const onDown = (e: MouseEvent) => {
      if (e.button === 3) {
        e.preventDefault();
        e.stopPropagation();
        stepHistory(e.ctrlKey || e.shiftKey ? 1 : -1);
      } else if (e.button === 4) {
        e.preventDefault();
        e.stopPropagation();
        stepHistory(1);
      }
    };
    const onAux = (e: MouseEvent) => {
      if (e.button === 1 || e.button === 3 || e.button === 4) {
        e.preventDefault();
      }
    };
    canvas.addEventListener('mousedown', onDown);
    canvas.addEventListener('auxclick', onAux);
    return () => {
      canvas.removeEventListener('mousedown', onDown);
      canvas.removeEventListener('auxclick', onAux);
    };
  }, [stepHistory]);

  // Drop any pending detail-sync timer / fly-to / tooltip dwell on unmount.
  useEffect(
    () => () => {
      if (detailSyncRef.current != null) {
        window.clearTimeout(detailSyncRef.current);
      }
      if (prefetchRef.current != null) {
        window.clearTimeout(prefetchRef.current);
      }
      if (animationRef.current != null) {
        cancelAnimationFrame(animationRef.current);
      }
      if (contentFadeRef.current != null) {
        cancelAnimationFrame(contentFadeRef.current);
      }
      if (pulseRafRef.current != null) {
        cancelAnimationFrame(pulseRafRef.current);
      }
      if (tooltipTimerRef.current != null) {
        window.clearTimeout(tooltipTimerRef.current);
      }
      if (wheelSettleRef.current != null) {
        window.clearTimeout(wheelSettleRef.current);
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
    const target = fitToRect(layout.worldBounds, width, height, FIT_PADDING);
    pushHistory(target);
    flyTo(target, FIT_DURATION_MS);
  }, [flyTo, pushHistory]);

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

      // L0 — a stripe click activates that slice. Type stripe → encoding stays/becomes pageType so the L1
      // colours line up with the clicked band; segment stripe → focuses that segment under the fragmentation
      // lens (matches the in-flight L1 convention at line 762 below). The "Other" bucket is intentionally
      // inert — it collapses many segments, so there's no single target to focus.
      const stripe = renderer.l0HitTest(screenX, screenY);
      if (stripe) {
        const store = useDbMapStore.getState();
        // Only *re-focus* the segment when the fragmentation lens is already active (matching the L1 page-click
        // path below). A bare click — including the first click of a double-click-to-zoom, which at L0 always lands
        // on a stripe — must not silently switch the Lens combo to fragmentation. Fragmentation is a deliberate
        // user choice via the Lens combo; the "Show in File Map" cross-link is purely spatial (it flies to + selects
        // the segment without touching the lens).
        if (stripe.kind === 'segment' && stripe.segmentId != null && stripe.segmentId !== NO_SEGMENT) {
          if (store.lens === 'fragmentation') {
            store.focusSegment(stripe.segmentId);
          }
        } else if (stripe.kind === 'type') {
          if (store.encoding !== 'pageType') {
            store.setEncoding('pageType');
          }
        }
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
            renderer.setSelectionChunk({ page: hit.page, chunkInPage: hit.chunkInPage });
            renderer.setSelectionCell({ page: hit.page, chunkInPage: hit.chunkInPage, cellIndex: hit.cellIndex });
            scheduleRender();
            select('cell', {
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
            renderer.setSelectionChunk({ page: hit.page, chunkInPage: hit.chunkInPage });
            renderer.setSelectionCell(null);
            scheduleRender();
            select('chunk', {
              kind: 'chunk',
              pageIndex: hit.page,
              segmentId: detail.ownerSegmentId,
              chunkId: detail.firstChunkId + hit.chunkInPage,
            });
            return;
          }
        }
      }

      // L1 — a page. A page-level selection drops any chunk-level marker so a later zoom-in shows the page,
      // not a stale chunk outline.
      const page = renderer.pageAt(screenX, screenY);
      renderer.setSelection(page);
      renderer.setSelectionChunk(null);
      renderer.setSelectionCell(null);
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
      // Carry the owning segment so the Inspector shows the `Segment ⊃ Page` ancestor section (IA §2.5),
      // matching chunk/cell. A free page (NO_SEGMENT) carries none → no bogus parent in the chain.
      select('page', {
        kind: 'page',
        pageIndex: page,
        segmentId: segId !== NO_SEGMENT ? segId : undefined,
      });
    },
    [data, pageDetails, chunkContents, scheduleRender, select, clearDbMapSelection],
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
          // Ease into the selection like every other navigation (double-click / fit) — the marquee zoom snapping
          // was the odd one out. flyTo drives its own rAF render + detail sync on landing.
          const target = zoomToWorldRect(world, width, height, FIT_PADDING);
          pushHistory(target);
          flyTo(target, FIT_DURATION_MS);
        }
      } else if (drag.mode === 'pan' && !drag.moved) {
        selectAt(pt.x, pt.y);
      } else if (drag.mode === 'pan' && drag.moved) {
        queueDetailSync();
        pushHistory(cameraRef.current);
      } else if (drag.mode === 'minimap' || drag.mode === 'strip') {
        queueDetailSync();
        pushHistory(cameraRef.current);
      }
      setRegionRect(null);
      canvas.style.cursor = 'default';
    },
    [handleWindowMouseMove, queueDetailSync, selectAt, pushHistory, flyTo],
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
    // Thumb buttons (3 back / 4 forward) drive the local history via a dedicated native listener — never start a drag.
    if (e.button !== 0) {
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
    // Cursor reflects the active gesture: grabbing while panning, crosshair while marqueeing a zoom rectangle,
    // pointer while scrubbing a widget. Reset to default on mouse-up.
    canvas.style.cursor = mode === 'pan' ? 'grabbing' : mode === 'region' ? 'crosshair' : 'pointer';
    dragRef.current = { mode, startX: pt.x, startY: pt.y, startCam: cameraRef.current, moved: false };
    // A drag in progress should never leave a stale tooltip-dwell timer running — the user is intentionally
    // mid-gesture, not parked over a target.
    cancelTooltip();
    window.addEventListener('mousemove', handleWindowMouseMove);
    window.addEventListener('mouseup', handleWindowMouseUp);
  };

  // Double-click — select the hovered element and ease the camera to fit it (cell @L4 / chunk @L3 / page @L1), the
  // per-element analogue of the Fit button. The two preceding single-clicks have already selected it; this adds the zoom.
  const handleDoubleClick = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current;
    const renderer = rendererRef.current;
    const surface = surfaceRef.current;
    if (!canvas || !renderer || !surface || !data) {
      return;
    }
    const pt = canvasPoint(canvas, e.clientX, e.clientY);
    // Leave the minimap / offset strip to their own click behaviour.
    if (pointIn(pt, renderer.getMinimapScreenRect()) || pointIn(pt, renderer.getOffsetStripScreenRect())) {
      return;
    }
    const rect = renderer.elementWorldRectAt(pt.x, pt.y);
    if (!rect) {
      return;
    }
    selectAt(pt.x, pt.y);
    const { width, height } = surface.getBoundingClientRect();
    const target = zoomToWorldRect(rect, width, height, FIT_PADDING);
    pushHistory(target);
    flyTo(target, FIT_DURATION_MS);
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

  // Latches the latest hover intent and (re)starts the dwell timer. The tooltip only renders after the timer
  // fires — any subsequent move calls this again and resets the countdown, so the user sees a tooltip only
  // after holding the mouse still over the same target for TOOLTIP_DELAY_MS.
  const scheduleTooltip = useCallback(() => {
    if (tooltipTimerRef.current != null) {
      window.clearTimeout(tooltipTimerRef.current);
    }
    tooltipTimerRef.current = window.setTimeout(() => {
      tooltipTimerRef.current = null;
      const intent = hoverIntentRef.current;
      if (!intent) {
        return;
      }
      if (intent.kind === 'page') {
        setHover(intent.data);
      } else {
        setL0Hover(intent.data);
      }
    }, TOOLTIP_DELAY_MS);
  }, []);

  // Cancels the dwell timer and hides whatever tooltip is currently up — used on mouse-leave, drag start,
  // and unmount.
  const cancelTooltip = useCallback(() => {
    if (tooltipTimerRef.current != null) {
      window.clearTimeout(tooltipTimerRef.current);
      tooltipTimerRef.current = null;
    }
    hoverIntentRef.current = null;
    setHover(null);
    setL0Hover(null);
  }, []);

  const handleHoverMove = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current;
    const renderer = rendererRef.current;
    if (!canvas || !renderer || dragRef.current || !data) {
      return;
    }
    const pt = canvasPoint(canvas, e.clientX, e.clientY);

    // Cursor affordance: pointer over the minimap/offset-strip widgets, crosshair while Shift advertises the
    // zoom-rectangle gesture, plain arrow otherwise. (Active-drag cursors are set by the drag handlers.)
    const overWidget = pointIn(pt, renderer.getMinimapScreenRect()) || pointIn(pt, renderer.getOffsetStripScreenRect());
    canvas.style.cursor = overWidget ? 'pointer' : e.shiftKey ? 'crosshair' : 'default';

    // Any motion hides the visible tooltip — it reappears only after the dwell period.
    if (hover) {
      setHover(null);
    }
    if (l0Hover) {
      setL0Hover(null);
    }

    // At L0 a stripe under the cursor takes priority — the renderer returns null past L0 so we cleanly fall
    // through to page hover above the L0→L1 crossfade.
    const stripe = renderer.l0HitTest(pt.x, pt.y);
    if (stripe) {
      hoverIntentRef.current = { kind: 'l0', data: { stripe, clientX: e.clientX, clientY: e.clientY } };
      // Stripe is the active target — clear the L1 cell outline so the page outline doesn't follow the cursor
      // around the stripe column.
      renderer.setHover(null);
      renderer.setHoverChunk(null);
      renderer.setHoverCell(null);
      scheduleRender();
      scheduleTooltip();
      return;
    }

    const page = renderer.pageAt(pt.x, pt.y);
    renderer.setHover(page);
    // At L3/L4 also track the chunk under the cursor so the hover outline tightens to the individual chunk; at L4
    // tighten further to the decoded content cell (entity slot) so the deepest level has its own hover feedback.
    const band = renderer.getLodState().band;
    const chunkHit = band === 'L1' ? null : renderer.pickChunk(pt.x, pt.y);
    renderer.setHoverChunk(chunkHit);
    renderer.setHoverCell(band === 'L4' ? renderer.pickContentCell(pt.x, pt.y) : null);
    scheduleRender();
    if (page == null) {
      hoverIntentRef.current = null;
      if (tooltipTimerRef.current != null) {
        window.clearTimeout(tooltipTimerRef.current);
        tooltipTimerRef.current = null;
      }
      return;
    }
    hoverIntentRef.current = {
      kind: 'page',
      data: {
        pageIndex: page,
        typeLabel: PAGE_TYPE_LABELS[data.pageType[page]] ?? 'Unknown',
        segmentLabel: segmentLabel(data, page),
        byteOffset: page * PAGE_SIZE,
        clientX: e.clientX,
        clientY: e.clientY,
        chunk: chunkHit ? chunkHoverInfo(chunkHit, pageDetails, chunkContents) : undefined,
        occupancy: occupancyHoverInfo(page, pageDetails),
      },
    };
    scheduleTooltip();
  };

  const handleHoverLeave = () => {
    cancelTooltip();
    rendererRef.current?.setHover(null);
    rendererRef.current?.setHoverChunk(null);
    rendererRef.current?.setHoverCell(null);
    scheduleRender();
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    // The panel-level shortcuts (s/f/b/r/c/Esc) must not hijack keystrokes meant for an editable control — the
    // search box, the toolbar selects, a bookmark-rename field. Without this, typing those letters in the Find box
    // toggled overlays and `preventDefault` swallowed the character. Let the event through when a control is focused.
    const target = e.target as HTMLElement;
    if (target.isContentEditable || target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT') {
      return;
    }
    if (e.key === 's' || e.key === 'S') {
      toggleSegmentOverlay();
      e.preventDefault();
    } else if (e.key === 'f' || e.key === 'F') {
      fitWholeFile();
      e.preventDefault();
    } else if (e.key === 'b' || e.key === 'B') {
      addCurrentBookmark();
      e.preventDefault();
    } else if (e.key === 'r' || e.key === 'R') {
      toggleResidencyOverlay();
      e.preventDefault();
    } else if (e.key === 'c' || e.key === 'C') {
      toggleRegionCaptions();
      e.preventDefault();
    } else if (e.key === 'Escape') {
      rendererRef.current?.setSelection(null);
      rendererRef.current?.setSelectionChunk(null);
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

      <div className="border-b border-border px-3 py-1 text-fs-sm text-muted-foreground" data-testid="dbmap-breadcrumb">
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
            onDoubleClick={handleDoubleClick}
            onMouseMove={handleHoverMove}
            onMouseLeave={handleHoverLeave}
            onContextMenu={handleContextMenu}
            style={{ display: 'block' }}
            data-testid="dbmap-canvas"
          />
          {regionRect && (
            <div
              className="pointer-events-none absolute border border-primary bg-primary/10"
              style={{ left: regionRect.x, top: regionRect.y, width: regionRect.w, height: regionRect.h }}
            />
          )}
          {isLoading && <p className="absolute left-3 top-2 text-fs-sm text-muted-foreground">Loading map…</p>}
          {isError && (
            <p className="absolute left-3 top-2 text-fs-sm text-destructive">Failed to load the file map.</p>
          )}
          {hover && <HoverTooltip info={hover} />}
          {l0Hover && <L0StripeTooltip info={l0Hover} data={data} />}
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
              displayBand={lod.displayBand}
              downSampleFactor={data?.downSampleFactor ?? 1}
              metrics={metrics}
              composition={composition}
              pathologies={pathologyFlags}
              segments={data?.segments ?? []}
              onFlyToPage={flyToPage}
            />
          }
          regions={
            <RegionsTab
              regions={regions}
              segments={data?.segments ?? []}
              onFlyToRegion={flyToRegion}
              onSelectSegment={(segId) =>
                select('segment', { kind: 'segment', segmentId: segId, typeName: data?.segments.find((s) => s.id === segId)?.typeName || undefined })
              }
            />
          }
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
      className="pointer-events-none z-50 rounded border border-border bg-popover px-2 py-1 text-fs-sm text-popover-foreground shadow-md"
      style={{ position: 'fixed', left: info.clientX + 12, top: info.clientY - 8, transform: 'translateY(-100%)' }}
    >
      <div>
        <span className="font-mono font-semibold text-foreground">#{info.pageIndex}</span>
        <span className="ml-2 text-muted-foreground">{info.typeLabel}</span>
        <span className="ml-2 text-muted-foreground">{info.segmentLabel}</span>
        <span className="ml-2 font-mono tabular-nums text-muted-foreground">
          @ 0x{info.byteOffset.toString(16).toUpperCase()}
        </span>
      </div>
      {info.chunk && (
        <div className="mt-0.5">
          <span className="font-mono font-semibold text-foreground">chunk #{info.chunk.chunkId}</span>
          <span className="ml-2 text-muted-foreground">[{info.chunk.indexInPage}]</span>
          <span className={`ml-2 ${info.chunk.occupied ? 'text-foreground' : 'text-muted-foreground'}`}>
            {info.chunk.occupied ? 'occupied' : 'free'}
          </span>
          {info.chunk.sizeBytes != null && (
            <span className="ml-2 font-mono tabular-nums text-muted-foreground">{formatFileSize(info.chunk.sizeBytes)}</span>
          )}
          {info.chunk.byteOffset != null && (
            <span className="ml-2 font-mono tabular-nums text-muted-foreground">
              @ 0x{info.chunk.byteOffset.toString(16).toUpperCase()}
            </span>
          )}
          {info.chunk.slotsTotal != null && (
            <span className="ml-2 font-mono tabular-nums text-muted-foreground">
              {info.chunk.slotsLive}/{info.chunk.slotsTotal} slots
            </span>
          )}
          {info.chunk.fill != null && (
            <span className="ml-2 font-mono tabular-nums text-muted-foreground">
              {Math.round((info.chunk.fill / 255) * 100)}% full
            </span>
          )}
        </div>
      )}
      {info.occupancy && (
        <div className="mt-0.5 text-muted-foreground">
          governs pages{' '}
          <span className="font-mono tabular-nums text-foreground">
            {info.occupancy.first.toLocaleString()}–{(info.occupancy.first + info.occupancy.count).toLocaleString()}
          </span>
        </div>
      )}
    </div>
  );
}

function L0StripeTooltip({ info, data }: { info: L0HoverInfo; data: DbMapData | null | undefined }) {
  const s = info.stripe;
  const pct = (s.fraction * 100).toFixed(1);
  // Segment stripes can append the kind / typeName from the StorageSegmentDto for extra context.
  let detail: string | null = null;
  if (s.kind === 'segment' && s.segmentId != null && data) {
    const seg = data.segments.find((x) => x.id === s.segmentId);
    if (seg && seg.typeName.length > 0) {
      detail = `${seg.kind} · ${seg.typeName}`;
    } else if (seg) {
      detail = seg.kind;
    }
  } else if (s.kind === 'other' && s.bucketed && data) {
    // "Other" bucket — list up to 5 of the largest segment ids it collapsed.
    const sample = s.bucketed
      .slice(0, 5)
      .map((id) => {
        const seg = data.segments.find((x) => x.id === id);
        return seg ? `#${seg.id}` : `#${id}`;
      })
      .join(', ');
    detail = s.bucketed.length > 5 ? `${sample}, +${s.bucketed.length - 5} more` : sample;
  }
  return (
    <div
      className="pointer-events-none z-50 rounded border border-border bg-popover px-2 py-1 text-fs-sm text-popover-foreground shadow-md"
      style={{ position: 'fixed', left: info.clientX + 12, top: info.clientY - 8, transform: 'translateY(-100%)' }}
    >
      <div className="flex items-center gap-2">
        <span className="inline-block h-2.5 w-2.5 rounded-sm" style={{ backgroundColor: rgbCss(s.color) }} />
        <span className="font-semibold text-foreground">{s.label}</span>
      </div>
      <div className="mt-0.5 font-mono tabular-nums text-muted-foreground">
        {formatFileSize(s.byteCount)} · {s.pageCount.toLocaleString()} pages · {pct}%
      </div>
      {detail && <div className="mt-0.5 text-muted-foreground">{detail}</div>}
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

/**
 * Builds the chunk line of the hover tooltip (proposal 5). chunk id / index / occupancy come from the page
 * detail (resident at L3); byte offset + size are added only when the L4 content for that chunk is resident.
 */
function chunkHoverInfo(
  hit: { page: number; chunkInPage: number },
  pageDetails: Map<number, DbPageDetail>,
  chunkContents: Map<string, DbChunkContent>,
): HoverInfo['chunk'] {
  const detail = pageDetails.get(hit.page);
  if (!detail || detail.chunkTotal <= 0 || hit.chunkInPage >= detail.chunkTotal) {
    return undefined;
  }
  const chunkId = detail.firstChunkId + hit.chunkInPage;
  const content = chunkContents.get(`${detail.ownerSegmentId}:${chunkId}`);
  // Cluster chunks (A6): the resident L4 content is the N entity slots — count the lit ones for the tooltip.
  const isCluster = content?.decoder === 'cluster';
  const slotsTotal = isCluster ? content!.cells.length : undefined;
  const slotsLive = isCluster ? content!.cells.filter((c) => c.colorKey > 0).length : undefined;
  return {
    chunkId,
    indexInPage: hit.chunkInPage,
    occupied: detail.chunkOccupancy[hit.chunkInPage] === 1,
    sizeBytes: content?.size,
    byteOffset: content?.byteOffset,
    fill: detail.chunkFill?.[hit.chunkInPage],
    slotsLive,
    slotsTotal,
  };
}

/** Governed-range info for an occupancy page's hover tooltip (A6, §10.2); undefined for non-occupancy pages. */
function occupancyHoverInfo(page: number, pageDetails: Map<number, DbPageDetail>): HoverInfo['occupancy'] {
  const detail = pageDetails.get(page);
  if (!detail || detail.occupancyGovernedCount == null || detail.occupancyGovernedCount <= 0) {
    return undefined;
  }
  return { first: detail.occupancyFirstPage ?? 0, count: detail.occupancyGovernedCount };
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
