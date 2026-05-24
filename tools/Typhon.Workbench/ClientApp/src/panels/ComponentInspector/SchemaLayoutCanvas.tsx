import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Info } from 'lucide-react';
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip';
import { DEFAULT_THEME, SchemaLayoutRenderer, type SchemaLayoutTheme } from '@/libs/SchemaLayoutRenderer';
import type { ComponentSchema, Field } from '@/hooks/schema/types';

/**
 * Presentational byte-grid canvas for a component's memory layout — the reusable body extracted from the
 * former SchemaLayoutPanel so the Component Inspector's Layout tab can render it without the panel's chrome
 * or its store coupling (it took selection from useSchemaInspectorStore and shipped a File-Map stub).
 * Fully prop-driven: schema in, field-click out. The renderer is canvas-2D; construction is guarded so a
 * headless mount (jsdom, no 2D context) degrades to a blank canvas instead of throwing.
 */
export interface SchemaLayoutCanvasProps {
  schema: ComponentSchema | null;
  isLoading?: boolean;
  isError?: boolean;
  /** Currently highlighted field (by name), or null. */
  selectedField: string | null;
  /** Field clicked in the grid (name), or null to clear (Escape). Drives the right-rail field detail. */
  onSelectField: (name: string | null) => void;
}

export default function SchemaLayoutCanvas({ schema, isLoading, isError, selectedField, onSelectField }: SchemaLayoutCanvasProps) {
  const [themeTick, setThemeTick] = useState(0);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const rendererRef = useRef<SchemaLayoutRenderer | null>(null);
  const [hoverField, setHoverField] = useState<Field | null>(null);
  const [hoverPos, setHoverPos] = useState<{ x: number; y: number } | null>(null);

  useLayoutEffect(() => {
    if (!canvasRef.current) return;
    try {
      rendererRef.current = new SchemaLayoutRenderer(canvasRef.current);
      rendererRef.current.setTheme(getCurrentTheme());
      rendererRef.current.setDevicePixelRatio(window.devicePixelRatio || 1);
    } catch {
      rendererRef.current = null; // no 2D context (headless) — degrade to a blank canvas
    }
  }, []);

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

  useEffect(() => {
    const observer = new MutationObserver(() => setThemeTick((n) => n + 1));
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
    return () => observer.disconnect();
  }, []);

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

  const handleClick = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current;
    const renderer = rendererRef.current;
    if (!canvas || !renderer) return;
    const rect = canvas.getBoundingClientRect();
    const hit = renderer.hitTest(e.clientX - rect.left, e.clientY - rect.top);
    onSelectField(hit ? hit.name : null);
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

  const handleKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    if (!schema) return;
    if (e.key === 'Escape') {
      onSelectField(null);
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
      currentIndex === -1 ? (delta > 0 ? 0 : fields.length - 1) : (currentIndex + delta + fields.length) % fields.length;
    onSelectField(fields[nextIndex].name);
  };

  return (
    <div tabIndex={0} onKeyDown={handleKeyDown} className="relative flex h-full w-full flex-col overflow-hidden bg-background outline-none">
      <div ref={scrollRef} className="relative min-h-0 flex-1 overflow-auto">
        <canvas
          ref={canvasRef}
          onClick={handleClick}
          onMouseMove={handleMouseMove}
          onMouseLeave={handleMouseLeave}
          data-testid="schema-layout-canvas"
          style={{ display: 'block' }}
        />
        <ColorLegendBadge />
        {isLoading && <p className="absolute inset-x-0 top-0 p-3 text-[12px] text-muted-foreground">Loading schema…</p>}
        {isError && <p className="absolute inset-x-0 top-0 p-3 text-[12px] text-destructive">Failed to load schema.</p>}
      </div>
      {hoverField && hoverPos && <HoverPeek field={hoverField} pos={hoverPos} />}
    </div>
  );
}

function HoverPeek({ field, pos }: { field: Field; pos: { x: number; y: number } }) {
  return (
    <div
      className="pointer-events-none z-50 rounded border border-border bg-popover px-2 py-1 text-[11px] text-popover-foreground shadow-md"
      style={{ position: 'fixed', left: pos.x + 12, top: pos.y - 8, transform: 'translateY(-100%)' }}
    >
      <span className="font-mono font-semibold text-foreground">{field.name}</span>
      <span className="ml-2 text-muted-foreground">{field.typeName}</span>
      <span className="ml-2 font-mono tabular-nums text-muted-foreground">
        @ 0x{field.offset.toString(16).toUpperCase()} · {field.size}B
      </span>
    </div>
  );
}

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
            <LegendRow swatch={<span className="block h-3 w-3 border-2" style={{ borderColor: 'var(--primary)' }} />} label="Field border" detail="Fits within a single 64-byte cache line" />
            <LegendRow swatch={<span className="block h-3 w-3 border-2 border-amber-400" />} label="Field border (thick)" detail="Crosses a cache-line boundary — two cache misses per access" />
            <LegendRow swatch={<span className="block h-0.5 w-3" style={{ backgroundColor: 'var(--destructive)' }} />} label="Horizontal rule" detail="Cache-line boundary between rows (every 64 bytes)" />
            <LegendRow swatch={<IndexTagSwatch />} label="Bookmark tag" detail="Field has an [Index] attribute" />
            <LegendRow swatch={<span className="block h-3 w-3 border" style={{ borderColor: 'var(--ring)', backgroundColor: 'rgba(0, 0, 255, 0.15)' }} />} label="Selection" detail="Currently selected field — colored border + soft blue wash" />
            <LegendRow swatch={<span className="block h-3 w-3 border bg-muted" />} label="Diagonal stripes" detail="Padding bytes (unused between fields)" />
          </div>
        </TooltipContent>
      </Tooltip>
    </TooltipProvider>
  );
}

function IndexTagSwatch() {
  return (
    <svg width="8" height="11" viewBox="0 0 8 11" aria-hidden>
      <path d="M0 0 H8 V11 L4 8 L0 11 Z" fill="var(--secondary)" />
    </svg>
  );
}

function LegendRow({ swatch, label, detail }: { swatch: React.ReactNode; label: string; detail: string }) {
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
    warning: DEFAULT_THEME.warning,
    selection: read('--ring', DEFAULT_THEME.selection),
    indexedAccent: read('--secondary', DEFAULT_THEME.indexedAccent),
  };
}
