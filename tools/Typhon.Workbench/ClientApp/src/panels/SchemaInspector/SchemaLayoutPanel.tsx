import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { AlertTriangle, Info, Map } from 'lucide-react';
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from '@/components/ui/tooltip';
import { useSchemaInspectorStore } from '@/stores/useSchemaInspectorStore';
import { openDbMapForComponent } from '@/shell/commands/openDbMap';
import { useComponentSchema } from '@/hooks/schema/useComponentSchema';
import {
  DEFAULT_THEME,
  SchemaLayoutRenderer,
  type SchemaLayoutTheme,
} from '@/libs/SchemaLayoutRenderer';
import type { Field } from '@/hooks/schema/types';

/**
 * The Layout panel. Subscribes to <see cref="useSchemaInspectorStore"/>, fetches the full component
 * schema for the focused type, and renders it as a cache-line-aligned byte grid. Click a field →
 * updates the selection, which the shared Detail panel renders as a full field inspector. Hover a
 * field for a lightweight name/type/offset peek.
 */
export default function SchemaLayoutPanel(_props: IDockviewPanelProps) {
  const selectedType = useSchemaInspectorStore((s) => s.selectedComponentType);
  const selectedField = useSchemaInspectorStore((s) => s.selectedField);
  const selectField = useSchemaInspectorStore((s) => s.selectField);
  // Tick bumped every time <html>'s class list changes — see MutationObserver effect. Including it
  // in the render deps below triggers a redraw *after* ThemeProvider has flipped the .dark class,
  // which is the moment CSS variables actually reflect the new palette. Subscribing to the zustand
  // theme state directly would fire the effect too early (children run effects before parents).
  const [themeTick, setThemeTick] = useState(0);

  const { schema, isLoading, isError } = useComponentSchema(selectedType);

  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const containerRef = useRef<HTMLDivElement | null>(null);
  // Scrollable region that directly parents the canvas. Sizing against this avoids including the
  // header row in the canvas height — otherwise the canvas overflows the scroll area by exactly
  // header-height and the scrollbar hides empty space below the drawn content.
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const rendererRef = useRef<SchemaLayoutRenderer | null>(null);
  // Mouse-hover peek — lightweight, stays nearby the cursor. Full details live in the Detail panel
  // (driven by the zustand selection when the user clicks).
  const [hoverField, setHoverField] = useState<Field | null>(null);
  const [hoverPos, setHoverPos] = useState<{ x: number; y: number } | null>(null);

  // Recompute when schema changes; the renderer's method is called *after* setSchema() in the
  // effect below, so we read it here but intentionally gate on `schema` so the value updates in sync
  // with the drawn state.
  const crossBoundaryCount = useMemo(() => {
    if (!rendererRef.current || !schema) return 0;
    return rendererRef.current.computeCrossBoundary().length;
  }, [schema]);

  // Construct / reconstruct the renderer whenever the canvas element changes.
  useLayoutEffect(() => {
    if (!canvasRef.current) return;
    rendererRef.current = new SchemaLayoutRenderer(canvasRef.current);
    rendererRef.current.setTheme(getCurrentTheme());
    rendererRef.current.setDevicePixelRatio(window.devicePixelRatio || 1);
  }, []);

  // Push schema / selection / theme into the renderer and redraw.
  useEffect(() => {
    const renderer = rendererRef.current;
    const canvas = canvasRef.current;
    if (!renderer || !canvas) return;
    renderer.setSchema(schema ?? null);
    renderer.setSelection(selectedField);
    renderer.setTheme(getCurrentTheme());
    resizeCanvasToContainer(canvas, scrollRef.current);
    renderer.setDevicePixelRatio(window.devicePixelRatio || 1);
    renderer.render();
  }, [schema, selectedField, themeTick]);

  // Watch <html>'s class attribute — ThemeProvider toggles `.dark` there. Firing a tick on the
  // actual mutation dodges the "effect runs before parent's effect toggled the class" race, so the
  // redraw below reads the settled CSS variables.
  useEffect(() => {
    const observer = new MutationObserver(() => setThemeTick((n) => n + 1));
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
    return () => observer.disconnect();
  }, []);

  // Re-render on container resize (panel drag, window resize).
  useEffect(() => {
    const el = scrollRef.current;
    const canvas = canvasRef.current;
    const renderer = rendererRef.current;
    if (!el || !canvas || !renderer) return;
    const ro = new ResizeObserver(() => {
      resizeCanvasToContainer(canvas, el);
      renderer.render();
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // Canvas click → hit-test → update selection in the shared store (drives Detail panel).
  const handleClick = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current;
    const renderer = rendererRef.current;
    if (!canvas || !renderer) return;
    const rect = canvas.getBoundingClientRect();
    const hit = renderer.hitTest(e.clientX - rect.left, e.clientY - rect.top);
    selectField(hit ? hit.name : null);
  };

  const handleMouseMove = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current;
    const renderer = rendererRef.current;
    if (!canvas || !renderer) return;
    const rect = canvas.getBoundingClientRect();
    const hit = renderer.hitTest(e.clientX - rect.left, e.clientY - rect.top);
    if (hit) {
      setHoverField(hit);
      setHoverPos({ x: e.clientX, y: e.clientY });
    } else if (hoverField) {
      setHoverField(null);
      setHoverPos(null);
    }
  };

  const handleMouseLeave = () => {
    setHoverField(null);
    setHoverPos(null);
  };

  // Keyboard navigation when the panel has focus.
  const handleKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    if (!schema) return;
    if (e.key === 'Escape') {
      selectField(null);
      e.preventDefault();
      return;
    }
    const delta = arrowDelta(e.key);
    if (delta === 0) return;
    e.preventDefault();
    const fields = schema.fields;
    if (fields.length === 0) return;
    const currentIndex = selectedField ? fields.findIndex((f) => f.name === selectedField) : -1;
    const nextIndex =
      currentIndex === -1
        ? delta > 0
          ? 0
          : fields.length - 1
        : (currentIndex + delta + fields.length) % fields.length;
    selectField(fields[nextIndex].name);
  };

  return (
    <div
      ref={containerRef}
      tabIndex={0}
      onKeyDown={handleKeyDown}
      className="relative flex h-full w-full flex-col overflow-hidden bg-background outline-none"
      data-testid="schema-layout-panel"
    >
      <div className="flex items-center gap-2 border-b border-border px-3 py-1.5">
        {selectedType ? (
          <>
            <h3 className="font-mono text-[12px] font-semibold text-foreground">
              {schema?.typeName ?? selectedType}
            </h3>
            {schema && (
              <span className="text-[11px] text-muted-foreground">
                {schema.storageSize}B · {schema.fields.length} fields
                {schema.allowMultiple ? ' · multi-instance' : ''}
              </span>
            )}
            <button
              type="button"
              onClick={() => openDbMapForComponent(selectedType)}
              className="flex items-center gap-1 rounded border border-border bg-card px-1.5 py-0.5 text-[11px] text-muted-foreground hover:text-foreground"
              title="Show this component's segment in the Database File Map"
            >
              <Map className="h-3 w-3" /> File Map
            </button>
            {crossBoundaryCount > 0 && (
              <span className="ml-auto inline-flex items-center gap-1 text-[11px] text-amber-400">
                <AlertTriangle className="h-3.5 w-3.5" />
                {crossBoundaryCount} field{crossBoundaryCount > 1 ? 's' : ''} cross cache lines
              </span>
            )}
          </>
        ) : (
          <span className="text-[12px] text-muted-foreground">No component selected</span>
        )}
      </div>

      <div ref={scrollRef} className="relative min-h-0 flex-1 overflow-auto">
        {/* Canvas must stay mounted across schema changes — the renderer is bound to this exact DOM
            node. Unmounting on isLoading would leave the renderer pointing at a detached node after
            the remount, and subsequent draws would never hit a visible canvas. */}
        <canvas
          ref={canvasRef}
          onClick={handleClick}
          onMouseMove={handleMouseMove}
          onMouseLeave={handleMouseLeave}
          data-testid="schema-layout-canvas"
          style={{ display: 'block' }}
        />
        <ColorLegendBadge />
        {isLoading && (
          <p className="absolute inset-x-0 top-0 p-3 text-[12px] text-muted-foreground">
            Loading schema…
          </p>
        )}
        {isError && (
          <p className="absolute inset-x-0 top-0 p-3 text-[12px] text-destructive">
            Failed to load schema.
          </p>
        )}
      </div>

      {hoverField && hoverPos && <HoverPeek field={hoverField} pos={hoverPos} />}
    </div>
  );
}

function HoverPeek({ field, pos }: { field: Field; pos: { x: number; y: number } }) {
  // Appear ABOVE-RIGHT of the cursor, with translateY(-100%) so the tooltip's bottom sits ~8px
  // above the cursor hotspot. Dodges the mouse arrow entirely and matches the convention of most
  // IDE hover chips. position:fixed escapes the panel's overflow/clip.
  return (
    <div
      className="pointer-events-none z-50 rounded border border-border bg-popover px-2 py-1 text-[11px] text-popover-foreground shadow-md"
      style={{
        position: 'fixed',
        left: pos.x + 12,
        top: pos.y - 8,
        transform: 'translateY(-100%)',
      }}
    >
      <span className="font-mono font-semibold text-foreground">{field.name}</span>
      <span className="ml-2 text-muted-foreground">{field.typeName}</span>
      <span className="ml-2 font-mono tabular-nums text-muted-foreground">
        @ 0x{field.offset.toString(16).toUpperCase()} · {field.size}B
      </span>
    </div>
  );
}

/**
 * Pinned top-left overlay explaining the meaning of each color used in the byte grid. Tooltip
 * content mirrors the tokens in SchemaLayoutRenderer — update both if the palette meaning changes.
 */
function ColorLegendBadge() {
  return (
    <TooltipProvider delayDuration={150}>
      <Tooltip>
        <TooltipTrigger asChild>
          <button
            type="button"
            className="absolute left-2 top-2 z-10 flex h-6 w-6 items-center justify-center rounded-full border border-border bg-card/80 text-muted-foreground backdrop-blur-sm hover:text-foreground"
            aria-label="Show color legend"
          >
            <Info className="h-3.5 w-3.5" />
          </button>
        </TooltipTrigger>
        <TooltipContent side="right" align="start" className="max-w-xs p-0">
          <div className="space-y-1.5 p-2 text-[11px]">
            <LegendRow
              swatch={<span className="block h-3 w-3 border-2" style={{ borderColor: 'var(--primary)' }} />}
              label="Field border"
              detail="Fits within a single 64-byte cache line"
            />
            <LegendRow
              swatch={<span className="block h-3 w-3 border-2 border-amber-400" />}
              label="Field border (thick)"
              detail="Crosses a cache-line boundary — two cache misses per access"
            />
            <LegendRow
              swatch={<span className="block h-0.5 w-3" style={{ backgroundColor: 'var(--destructive)' }} />}
              label="Horizontal rule"
              detail="Cache-line boundary between rows (every 64 bytes)"
            />
            <LegendRow
              swatch={<IndexTagSwatch />}
              label="Bookmark tag"
              detail="Field has an [Index] attribute"
            />
            <LegendRow
              swatch={
                <span
                  className="block h-3 w-3 border"
                  style={{ borderColor: 'var(--ring)', backgroundColor: 'rgba(0, 0, 255, 0.15)' }}
                />
              }
              label="Selection"
              detail="Currently selected field — colored border + soft blue wash"
            />
            <LegendRow
              swatch={<span className="block h-3 w-3 border bg-muted" />}
              label="Diagonal stripes"
              detail="Padding bytes (unused between fields)"
            />
          </div>
        </TooltipContent>
      </Tooltip>
    </TooltipProvider>
  );
}

function IndexTagSwatch() {
  // SVG mirror of the bookmark drawn by SchemaLayoutRenderer.drawIndexIcon — 8×11 rectangle with a
  // V-notch cut out of the bottom edge. Kept here (not imported) so the legend stays in sync with
  // the canvas draw path at a glance.
  return (
    <svg width="8" height="11" viewBox="0 0 8 11" aria-hidden>
      <path d="M0 0 H8 V11 L4 8 L0 11 Z" fill="var(--secondary)" />
    </svg>
  );
}

function LegendRow({
  swatch,
  label,
  detail,
}: {
  swatch: React.ReactNode;
  label: string;
  detail: string;
}) {
  return (
    <div className="flex items-start gap-2">
      <div className="mt-1 flex h-3 w-3 shrink-0 items-center justify-center">{swatch}</div>
      <div className="min-w-0">
        <div className="font-medium text-foreground">{label}</div>
        <div className="text-muted-foreground">{detail}</div>
      </div>
    </div>
  );
}

function arrowDelta(key: string): number {
  if (key === 'ArrowRight' || key === 'ArrowDown') return 1;
  if (key === 'ArrowLeft' || key === 'ArrowUp') return -1;
  return 0;
}

function resizeCanvasToContainer(canvas: HTMLCanvasElement, container: HTMLElement | null) {
  if (!container) return;
  const dpr = window.devicePixelRatio || 1;
  const { width, height } = container.getBoundingClientRect();
  canvas.width = Math.max(1, Math.floor(width * dpr));
  canvas.height = Math.max(1, Math.floor(height * dpr));
  canvas.style.width = `${width}px`;
  canvas.style.height = `${height}px`;
}

function getCurrentTheme(): SchemaLayoutTheme {
  // Pull live values from the design-token CSS variables on <html>. ThemeProvider toggles the .dark
  // class which rewrites every --* token; reading computed styles yields whichever palette is
  // active right now. Canvas 2D accepts oklch() fillStyle on every browser the Workbench targets.
  if (typeof document === 'undefined') return DEFAULT_THEME;
  const root = document.documentElement;
  const read = (name: string, fallback: string): string => {
    const v = getComputedStyle(root).getPropertyValue(name).trim();
    return v.length > 0 ? v : fallback;
  };
  return {
    background: read('--background', DEFAULT_THEME.background),
    gridLine: read('--border', DEFAULT_THEME.gridLine),
    ruler: read('--muted-foreground', DEFAULT_THEME.ruler),
    label: read('--foreground', DEFAULT_THEME.label),
    fieldFill: read('--muted', DEFAULT_THEME.fieldFill),
    fieldStroke: read('--primary', DEFAULT_THEME.fieldStroke),
    paddingFill: read('--card', DEFAULT_THEME.paddingFill),
    paddingStroke: read('--border', DEFAULT_THEME.paddingStroke),
    cacheLine: read('--destructive', DEFAULT_THEME.cacheLine),
    // No dedicated warning token — amber-400 kept hardcoded since it stays legible on both palettes.
    warning: DEFAULT_THEME.warning,
    selection: read('--ring', DEFAULT_THEME.selection),
    indexedAccent: read('--secondary', DEFAULT_THEME.indexedAccent),
  };
}
