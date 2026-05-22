import { useDbMapStore } from '@/stores/useDbMapStore';
import { formatFileSize } from '@/lib/formatters';
import {
  BYTE_CLASS_RGB,
  CRC_RGB,
  FREE_RGB,
  PAGE_TYPE_RGB,
  RESIDENCY_RGB,
  USED_RGB,
  entropyRgb,
  fillDensityRgb,
  rgbCss,
  writeAgeRgb,
} from '@/libs/dbmap/dbMapColors';
import {
  DbPageType,
  PAGE_TYPE_LABELS,
  isDetailEncoding,
  type DbMapEncoding,
  type StorageSegmentDto,
} from '@/libs/dbmap/types';
import type { FreeSpaceComposition } from '@/libs/dbmap/dbMapMetrics';
import type { PathologyFlag } from '@/libs/dbmap/dbMapPathology';
import { MetricsCard } from './MetricsCard';
import type { MetricsCardData } from './MetricsCard';

// The side-rail Legend tab (Module 15, A3, §6.4): the active encoding's colour key, a per-band symbol guide
// that explains every visual cue at the current zoom, plus the lens-driven readouts (fragmentation metrics,
// free-space composition, pathology list). The panel computes every figure; this tab only lays them out.

interface LegendTabProps {
  /** Currently-dominant display band, used to pick which symbol guide to render. */
  displayBand: 'L0' | 'L1' | 'L3' | 'L4';
  /** Coarse down-sample factor (§5.5) — > 1 marks the map (and its encodings) as approximate. */
  downSampleFactor: number;
  /** Fragmentation-lens metrics, or null when no segment is focused. */
  metrics: MetricsCardData | null;
  /** Free-space composition, or null when the free-space lens is inactive. */
  composition: FreeSpaceComposition | null;
  /** Pathology flags (under-filled pages) for the pathology lens. */
  pathologies: PathologyFlag[];
  /** Segment table — for resolving owner labels in the pathology list. */
  segments: StorageSegmentDto[];
  /** Flies the camera to a page (pathology-list row click). */
  onFlyToPage: (page: number) => void;
}

export function LegendTab(props: LegendTabProps) {
  const encoding = useDbMapStore((s) => s.encoding);
  const lens = useDbMapStore((s) => s.lens);

  return (
    <div className="flex flex-col gap-3 p-2">
      <section className="flex flex-col gap-1">
        <h3 className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">Encoding</h3>
        <EncodingLegend encoding={encoding} />
        {props.downSampleFactor > 1 && (
          <span
            className="mt-0.5 self-start rounded bg-amber-500/15 px-1.5 py-0.5 text-[10px] font-medium text-amber-600 dark:text-amber-400"
            title={`This database exceeds the coarse-cell budget — each cell aggregates ${props.downSampleFactor} pages (§5.5). Colours and metrics are approximate.`}
          >
            Approximate · down-sampled ×{props.downSampleFactor}
          </span>
        )}
      </section>

      <SymbolGuide band={props.displayBand} />

      {lens === 'fragmentation' && (
        <section className="flex flex-col gap-1">
          <h3 className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">
            Fragmentation lens
          </h3>
          {props.metrics ? (
            <MetricsCard {...props.metrics} />
          ) : (
            <p className="text-[11px] text-muted-foreground">Select a segment to measure its fragmentation.</p>
          )}
        </section>
      )}

      {lens === 'freeSpace' && (
        <section className="flex flex-col gap-1">
          <h3 className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">
            Free-space lens
          </h3>
          {props.composition ? (
            <CompositionBar composition={props.composition} />
          ) : (
            <p className="text-[11px] text-muted-foreground">No map loaded.</p>
          )}
        </section>
      )}

      {lens === 'pathology' && (
        <section className="flex flex-col gap-1">
          <h3 className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">
            Pathology flags
          </h3>
          <PathologyList flags={props.pathologies} segments={props.segments} onFlyToPage={props.onFlyToPage} />
        </section>
      )}
    </div>
  );
}

// ── The encoding colour key ───────────────────────────────────────────────────────────────────────────────

function EncodingLegend({ encoding }: { encoding: DbMapEncoding }) {
  if (encoding === 'segment') {
    return (
      <div className="flex flex-col gap-1.5">
        <span className="text-[11px] text-muted-foreground">One stable hue per segment id.</span>
        <SwatchColumn entries={[TAIL_ENTRY]} />
      </div>
    );
  }
  if (isDetailEncoding(encoding)) {
    return <DetailLegend encoding={encoding} />;
  }
  const pageEntries =
    encoding === 'freeUsed'
      ? [
          { label: 'Free (allocated, unused)', color: rgbCss(FREE_RGB) },
          { label: 'Used', color: rgbCss(USED_RGB) },
        ]
      : [
          DbPageType.Free,
          DbPageType.Root,
          DbPageType.Occupancy,
          DbPageType.Component,
          DbPageType.Revision,
          DbPageType.Index,
          DbPageType.Cluster,
          DbPageType.Vsbs,
          DbPageType.StringTable,
          DbPageType.Spatial,
          DbPageType.EntityMap,
          DbPageType.System,
        ].map((t) => ({
          label: t === DbPageType.Free ? 'Free (allocated, unused)' : PAGE_TYPE_LABELS[t],
          color: rgbCss(PAGE_TYPE_RGB[t]),
        }));
  return <SwatchColumn entries={[...pageEntries, TAIL_ENTRY]} />;
}

/**
 * Tail = Hilbert-grid cells past `pageCount`, not pages of the file at all. Rendered as a transparent
 * cell + crosshatch overlay (the canvas background shows through); listed in the legend so the user
 * doesn't read "lots of dark space" as "lots of free pages".
 */
const TAIL_ENTRY = { label: 'Tail (outside file)', color: 'transparent', hatched: true } as const;

function DetailLegend({ encoding }: { encoding: DbMapEncoding }) {
  if (encoding === 'crc') {
    return (
      <SwatchColumn
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
      <SwatchColumn
        entries={[
          { label: 'On disk only', color: rgbCss(RESIDENCY_RGB[0]) },
          { label: 'Resident clean', color: rgbCss(RESIDENCY_RGB[1]) },
          { label: 'Resident dirty', color: rgbCss(RESIDENCY_RGB[2]) },
        ]}
      />
    );
  }
  if (encoding === 'byteClass') {
    return (
      <SwatchColumn
        entries={[
          { label: '0x00 (zero)', color: rgbCss(BYTE_CLASS_RGB[0]) },
          { label: '0xFF', color: rgbCss(BYTE_CLASS_RGB[1]) },
          { label: 'ASCII', color: rgbCss(BYTE_CLASS_RGB[2]) },
          { label: 'Binary', color: rgbCss(BYTE_CLASS_RGB[3]) },
        ]}
      />
    );
  }
  // Sequential ramp — fill density / write age / entropy.
  const ramp = encoding === 'writeAge' ? writeAgeRgb : encoding === 'entropy' ? entropyRgb : fillDensityRgb;
  const lo = encoding === 'writeAge' ? 'old' : encoding === 'entropy' ? 'low' : 'empty';
  const hi = encoding === 'writeAge' ? 'new' : encoding === 'entropy' ? 'high' : 'full';
  return (
    <div className="flex items-center gap-1 text-[10px] text-muted-foreground">
      <span>{lo}</span>
      {[0, 0.25, 0.5, 0.75, 1].map((s) => (
        <span key={s} className="inline-block h-3 w-5" style={{ backgroundColor: rgbCss(ramp(s)) }} />
      ))}
      <span>{hi}</span>
    </div>
  );
}

function SwatchColumn({ entries }: { entries: readonly { label: string; color: string; hatched?: boolean }[] }) {
  return (
    <div className="flex flex-col gap-0.5">
      {entries.map((e) => (
        <span key={e.label} className="flex items-center gap-1.5 text-[11px] text-muted-foreground">
          <Swatch color={e.color} hatched={e.hatched} />
          {e.label}
        </span>
      ))}
    </div>
  );
}

/** A 10×10 swatch — solid fill, or an SVG crosshatch when `hatched` (the tail / outside-file legend). */
function Swatch({ color, hatched }: { color: string; hatched?: boolean }) {
  if (hatched) {
    return (
      <svg
        width="10"
        height="10"
        viewBox="0 0 10 10"
        className="rounded-sm border border-border text-muted-foreground"
        aria-hidden
      >
        <line x1="0" y1="0" x2="10" y2="10" stroke="currentColor" strokeWidth="1" opacity="0.7" />
        <line x1="10" y1="0" x2="0" y2="10" stroke="currentColor" strokeWidth="1" opacity="0.7" />
      </svg>
    );
  }
  return <span className="inline-block h-2.5 w-2.5 rounded-sm" style={{ backgroundColor: color }} />;
}

// ── Per-band symbol guide — what every visual cue at the current zoom means ───────────────────────────────

function SymbolGuide({ band }: { band: 'L0' | 'L1' | 'L3' | 'L4' }) {
  const items = band === 'L0' ? L0_ITEMS : band === 'L1' ? L1_ITEMS : band === 'L3' ? L3_ITEMS : L4_ITEMS;
  return (
    <section className="flex flex-col gap-1">
      <h3 className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">
        Symbols at this zoom ({band})
      </h3>
      <ul className="flex flex-col gap-1.5">
        {items.map((item) => (
          <li key={item.title} className="flex items-start gap-2">
            <span className="mt-[1px] shrink-0">{item.icon}</span>
            <span className="text-[11px] leading-tight">
              <span className="font-semibold text-foreground">{item.title}</span>
              <span className="text-muted-foreground">{' — '}{item.body}</span>
            </span>
          </li>
        ))}
      </ul>
    </section>
  );
}

interface SymbolItem {
  title: string;
  body: string;
  icon: React.ReactNode;
}

// ── Icon primitives — SVG so the legend swatches visually match what the renderer draws. ─────────────────

function HeaderStripIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden>
      <rect x="0" y="0" width="18" height="18" fill="rgb(59, 130, 246)" rx="2" />
      <rect x="0" y="0" width="18" height="4" fill="rgb(59, 130, 246)" />
      <defs>
        <pattern id="hp" width="4" height="4" patternUnits="userSpaceOnUse">
          <line x1="0" y1="0" x2="4" y2="4" stroke="rgb(15, 23, 42)" strokeWidth="1" />
        </pattern>
      </defs>
      <rect x="0" y="0" width="18" height="4" fill="url(#hp)" />
    </svg>
  );
}

function FreeHatchIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden>
      <rect x="0" y="0" width="18" height="18" fill="rgb(30, 41, 59)" rx="2" />
      <line x1="0" y1="0" x2="18" y2="18" stroke="rgb(148, 163, 184)" strokeWidth="1" opacity="0.6" />
    </svg>
  );
}

function TailCrosshatchIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" className="text-muted-foreground" aria-hidden>
      <rect x="0" y="0" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="0.5" opacity="0.3" rx="2" />
      <line x1="0" y1="0" x2="18" y2="18" stroke="currentColor" strokeWidth="1" opacity="0.6" />
      <line x1="18" y1="0" x2="0" y2="18" stroke="currentColor" strokeWidth="1" opacity="0.6" />
    </svg>
  );
}

function CornerDotIcon({ color, corner }: { color: string; corner: 'tl' | 'tr' | 'bl' | 'br' }) {
  const cx = corner === 'tl' || corner === 'bl' ? 4 : 14;
  const cy = corner === 'tl' || corner === 'tr' ? 4 : 14;
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden>
      <rect x="0" y="0" width="18" height="18" fill="rgb(59, 130, 246)" rx="2" />
      <circle cx={cx} cy={cy} r="2.5" fill={color} />
    </svg>
  );
}

function CornerTriangleIcon({ color }: { color: string }) {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden>
      <rect x="0" y="0" width="18" height="18" fill="rgb(59, 130, 246)" rx="2" />
      <polygon points="11,0 18,0 18,7" fill={color} />
    </svg>
  );
}

function StripeStackIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden>
      <rect x="0" y="0" width="18" height="6" fill="rgb(59, 130, 246)" />
      <rect x="0" y="6" width="18" height="4" fill="rgb(16, 185, 129)" />
      <rect x="0" y="10" width="18" height="3" fill="rgb(236, 72, 153)" />
      <rect x="0" y="13" width="18" height="5" fill="rgb(30, 41, 59)" />
    </svg>
  );
}

function PillIcon({ color }: { color: string }) {
  return (
    <svg width="22" height="14" viewBox="0 0 22 14" aria-hidden>
      <rect x="0.5" y="0.5" width="21" height="13" rx="3" fill={color} stroke="rgb(100, 116, 139)" strokeWidth="0.5" />
      <text x="11" y="10" textAnchor="middle" fontSize="8" fill="rgb(226, 232, 240)" fontFamily="sans-serif">
        Aa
      </text>
    </svg>
  );
}

function BadgeIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden>
      <rect x="0" y="0" width="18" height="18" fill="rgb(59, 130, 246)" rx="2" />
      <text x="2" y="8" fontSize="6" fontFamily="monospace" fill="rgb(226, 232, 240)">
        #7
      </text>
    </svg>
  );
}

function ChunkGridIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden>
      <rect x="0.5" y="0.5" width="17" height="17" fill="rgb(56, 189, 248)" stroke="rgb(100, 116, 139)" strokeWidth="0.5" />
      {[6, 12].map((p) => (
        <g key={p}>
          <line x1={p} y1="0" x2={p} y2="18" stroke="rgb(100, 116, 139)" strokeWidth="0.5" />
          <line x1="0" y1={p} x2="18" y2={p} stroke="rgb(100, 116, 139)" strokeWidth="0.5" />
        </g>
      ))}
      <rect x="12.5" y="12.5" width="5" height="5" fill="rgb(30, 41, 59)" />
    </svg>
  );
}

function ContentCellsIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden>
      <rect x="0" y="0" width="6" height="6" fill="rgb(59, 130, 246)" />
      <rect x="6" y="0" width="6" height="6" fill="rgb(16, 185, 129)" />
      <rect x="12" y="0" width="6" height="6" fill="rgb(245, 158, 11)" />
      <rect x="0" y="6" width="6" height="6" fill="rgb(236, 72, 153)" />
      <rect x="6" y="6" width="6" height="6" fill="rgb(6, 182, 212)" />
      <rect x="12" y="6" width="6" height="6" fill="rgb(139, 92, 246)" />
      <rect x="0" y="12" width="18" height="6" fill="rgb(148, 163, 184)" />
    </svg>
  );
}

function UnknownTileIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" className="text-muted-foreground" aria-hidden>
      <rect x="0" y="0" width="18" height="18" fill="rgb(15, 23, 42)" rx="2" />
      {[-9, -3, 3, 9].map((d) => (
        <line key={d} x1={d} y1="0" x2={d + 18} y2="18" stroke="currentColor" strokeWidth="1" opacity="0.5" />
      ))}
    </svg>
  );
}

/** Three chunk cells tinted along the fill ramp (dark → blue → amber) — the intra-chunk fill heat (A6). */
function FillHeatIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden>
      <rect x="0" y="0" width="6" height="18" fill="rgb(30, 41, 59)" />
      <rect x="6" y="0" width="6" height="18" fill="rgb(59, 130, 246)" />
      <rect x="12" y="0" width="6" height="18" fill="rgb(245, 158, 11)" />
      <line x1="6" y1="0" x2="6" y2="18" stroke="rgb(100, 116, 139)" strokeWidth="0.5" />
      <line x1="12" y1="0" x2="12" y2="18" stroke="rgb(100, 116, 139)" strokeWidth="0.5" />
    </svg>
  );
}

/** Two cells — a green leaf and an amber internal node — the B-tree leaf/internal two-tone (A6). */
function IndexNodesIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden>
      <rect x="0" y="0" width="9" height="18" fill="rgb(16, 185, 129)" />
      <rect x="9" y="0" width="9" height="18" fill="rgb(245, 158, 11)" />
      <line x1="9" y1="0" x2="9" y2="18" stroke="rgb(15, 23, 42)" strokeWidth="0.5" />
    </svg>
  );
}

/** A dim-slate chunk with a backslash hatch — a structural (hashmap meta / directory) chunk, not data (A6). */
function NonDataHatchIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" className="text-muted-foreground" aria-hidden>
      <rect x="0" y="0" width="18" height="18" fill="rgb(51, 65, 85)" rx="2" />
      <line x1="0" y1="0" x2="18" y2="18" stroke="currentColor" strokeWidth="1" opacity="0.45" />
    </svg>
  );
}

/** A free chunk slot with an amber diagonal — a reclaimable hole before the fill frontier (proposal 3). */
function HoleDiagonalIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden>
      <rect x="0" y="0" width="18" height="18" fill="rgb(30, 41, 59)" rx="2" />
      <line x1="0" y1="0" x2="18" y2="18" stroke="rgb(245, 158, 11)" strokeWidth="1.2" opacity="0.85" />
    </svg>
  );
}

/** An X-crosshatched dark cell — dead space (stride-alignment padding / the square grid's surplus cells). */
function DeadSpaceIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" className="text-muted-foreground" aria-hidden>
      <rect x="0" y="0" width="18" height="18" fill="rgb(15, 23, 42)" rx="2" />
      <line x1="0" y1="0" x2="18" y2="18" stroke="currentColor" strokeWidth="1" opacity="0.4" />
      <line x1="18" y1="0" x2="0" y2="18" stroke="currentColor" strokeWidth="1" opacity="0.4" />
    </svg>
  );
}

/** Stacked hatched bands at the top of a data cell — page header (\), root directory (/), stride padding (X) (A6). */
function OverheadBandsIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden>
      <rect x="0" y="0" width="18" height="18" fill="rgb(56, 189, 248)" rx="2" />
      <rect x="0" y="0" width="18" height="4" fill="rgb(59, 130, 246)" />
      {[0, 5, 10, 15].map((x) => (
        <line key={`h${x}`} x1={x} y1="0" x2={x + 4} y2="4" stroke="rgb(15, 23, 42)" strokeWidth="0.9" />
      ))}
      <rect x="0" y="4" width="18" height="3" fill="rgb(139, 92, 246)" opacity="0.6" />
      {[5, 10, 15, 20].map((x) => (
        <line key={`d${x}`} x1={x} y1="4" x2={x - 3} y2="7" stroke="rgb(15, 23, 42)" strokeWidth="0.9" />
      ))}
      <g stroke="rgb(15, 23, 42)" strokeWidth="0.8" opacity="0.7">
        <line x1="0" y1="7" x2="18" y2="10" />
        <line x1="18" y1="7" x2="0" y2="10" />
      </g>
    </svg>
  );
}

/** A violet page filled with a mini allocation grid (cyan used / dark free) — an occupancy region-map (A6 §10.2). */
function OccupancyMapIcon() {
  const cells = [1, 1, 0, 1, 1, 0, 1, 1, 0, 1, 1, 0, 1, 1, 1, 0];
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden>
      <rect x="0" y="0" width="18" height="18" fill="rgb(139, 92, 246)" rx="2" />
      {cells.map((v, i) => (
        <rect
          key={i}
          x={(i % 4) * 4 + 1}
          y={Math.floor(i / 4) * 4 + 1}
          width="3"
          height="3"
          fill={v ? 'rgb(56, 189, 248)' : 'rgb(30, 41, 59)'}
        />
      ))}
    </svg>
  );
}

/** An N-slot grid lit by occupancy (cyan = occupied, dark = free) — the cluster entity sub-grid (A6 §10.1). */
function EntitySubGridIcon() {
  const cells = [1, 1, 1, 0, 1, 0, 1, 1, 1, 1, 0, 1, 0, 1, 1, 1];
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden>
      <rect x="0" y="0" width="18" height="18" fill="rgb(236, 72, 153)" rx="2" />
      {cells.map((v, i) => (
        <rect
          key={i}
          x={(i % 4) * 4 + 1}
          y={Math.floor(i / 4) * 4 + 1}
          width="3"
          height="3"
          fill={v ? 'rgb(56, 189, 248)' : 'rgb(30, 41, 59)'}
        />
      ))}
    </svg>
  );
}

/** Entity slots recoloured by the selected component: green = enabled, dim red = disabled, dark = free (A6). */
function ComponentOverlayIcon() {
  // 2 = enabled (green), 1 = occupied-but-disabled (dim red), 0 = free (dark)
  const cells = [2, 2, 1, 0, 2, 1, 2, 2, 2, 2, 0, 1, 0, 2, 2, 1];
  const fill = (v: number) => (v === 2 ? 'rgb(34, 197, 94)' : v === 1 ? 'rgb(120, 53, 53)' : 'rgb(30, 41, 59)');
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden>
      <rect x="0" y="0" width="18" height="18" fill="rgb(236, 72, 153)" rx="2" />
      {cells.map((v, i) => (
        <rect key={i} x={(i % 4) * 4 + 1} y={Math.floor(i / 4) * 4 + 1} width="3" height="3" fill={fill(v)} />
      ))}
    </svg>
  );
}

/** A small stack of rows — the Detail-panel inspector that decodes a chunk to label/value pairs. */
function InspectorIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" className="text-muted-foreground" aria-hidden>
      <rect x="0" y="0" width="18" height="18" fill="rgb(30, 41, 59)" rx="2" />
      {[4, 8, 12, 16].map((y) => (
        <line key={y} x1="3" y1={y} x2="15" y2={y} stroke="currentColor" strokeWidth="1.2" opacity="0.6" />
      ))}
    </svg>
  );
}

/** A bright outline inside a cell — the hover / selection marker (A5 proposal 2). */
function OutlineIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden>
      <rect x="0" y="0" width="18" height="18" fill="rgb(30, 41, 59)" rx="2" />
      <rect x="2.5" y="2.5" width="13" height="13" fill="none" stroke="rgb(56, 189, 248)" strokeWidth="1.5" />
    </svg>
  );
}

// ── Per-band item lists ───────────────────────────────────────────────────────────────────────────────────

const L0_ITEMS: readonly SymbolItem[] = [
  {
    title: 'Composition stripes',
    body: 'Vertical bands inside the file rect, height ∝ bytes. Stable order; Free always last.',
    icon: <StripeStackIcon />,
  },
  {
    title: 'Click a stripe',
    body: 'Type stripe → switches encoding to pageType; segment stripe → focuses the fragmentation lens.',
    icon: <span className="inline-block h-[18px] w-[18px] text-center text-[14px]">▾</span>,
  },
  {
    title: 'Header band (top)',
    body: 'DB name · size · pages · segments · LSN. The ⚠ badge appears when pathology flags are present.',
    icon: <span className="inline-block h-[18px] w-[18px] text-center text-[14px]">≡</span>,
  },
];

const L1_ITEMS: readonly SymbolItem[] = [
  {
    title: 'Header strip',
    body: 'Top 2.34% of each cell — the page header (192 B). Hatched = engine overhead, not user data.',
    icon: <HeaderStripIcon />,
  },
  {
    title: 'Free-page diagonal',
    body: 'A page allocated in the file but currently unused — single hatch line over FREE_RGB.',
    icon: <FreeHatchIcon />,
  },
  {
    title: 'Tail crosshatch',
    body: 'Hilbert-grid cells past pageCount — not part of the file. No tooltip; canvas background shows through.',
    icon: <TailCrosshatchIcon />,
  },
  {
    title: 'CRC failure',
    body: 'Red triangle, top-right corner — live CRC check failed for this page.',
    icon: <CornerTriangleIcon color="rgb(239, 68, 68)" />,
  },
  {
    title: 'Pathology',
    body: 'Amber dot, bottom-left — under-filled or otherwise flagged by the pathology heuristic.',
    icon: <CornerDotIcon color="rgb(245, 158, 11)" corner="bl" />,
  },
  {
    title: 'Cache resident',
    body: 'Bottom-right: green = resident-clean, amber = resident-dirty, absent = on-disk only. Toggle: r.',
    icon: <CornerDotIcon color="rgb(34, 197, 94)" corner="br" />,
  },
  {
    title: 'Run label',
    body: 'A pill on each contiguous block of a segment, sitting on cells it owns. Reads `Name k/M` — the k-th of M blocks — so a fragmented segment shows 1/3 · 2/3 · 3/3 across the map.',
    icon: <PillIcon color="rgb(30, 41, 59)" />,
  },
  {
    title: 'Verbose labels (c)',
    body: 'Press c to switch run labels to Type · Name · N pages · size.',
    icon: <PillIcon color="rgb(30, 41, 59)" />,
  },
  {
    title: 'Segment-id badge',
    body: "Top-left of each run's first cell, shown once cell ≥ 50 px — one per contiguous block, not per page.",
    icon: <BadgeIcon />,
  },
  {
    title: 'Page index',
    body: 'Centered text, shown once cell ≥ 100 px.',
    icon: <span className="inline-block h-[18px] w-[18px] text-center text-[10px] text-muted-foreground">#NNN</span>,
  },
];

const L3_ITEMS: readonly SymbolItem[] = [
  {
    title: 'Chunk grid',
    body: 'Each page subdivides into its chunk slots. Occupied slots take the page colour; a free slot stays dark slate.',
    icon: <ChunkGridIcon />,
  },
  {
    title: 'Intra-chunk fill heat',
    body: 'Container chunks (cluster, VSBS, string, hashmap bucket) tint dark → blue → amber by how full they are — a half-empty structure reads cooler than a full one, not just allocated vs free.',
    icon: <FillHeatIcon />,
  },
  {
    title: 'B-tree nodes',
    body: 'On an index segment, leaf nodes take the page colour and internal nodes are amber — the sparse internal skeleton over a sea of leaves shows the tree shape (fanout, height). The shared B-tree directory chunks render as Structural (below).',
    icon: <IndexNodesIcon />,
  },
  {
    title: 'Overflow marker',
    body: 'Red dot, top-right — a hashmap bucket that overflowed (full and chained) or an overflow chunk: hash-collision / load pressure.',
    icon: <CornerDotIcon color="rgb(244, 63, 94)" corner="tr" />,
  },
  {
    title: 'Structural chunk',
    body: 'Dim slate + backslash hatch — a hashmap meta / directory chunk: structure, not data, so it is never coloured (or mis-read) as a bucket.',
    icon: <NonDataHatchIcon />,
  },
  {
    title: 'Reclaimable hole',
    body: 'Amber diagonal — a free chunk before the last allocated one (internal fragmentation). Free chunks after the frontier are growth headroom and stay unmarked.',
    icon: <HoleDiagonalIcon />,
  },
  {
    title: 'Overhead bands',
    body: 'Byte-proportional bands above the grid: the page header (backslash) and, on a segment root page, the segment directory / page-index table (slash).',
    icon: <OverheadBandsIcon />,
  },
  {
    title: 'Dead space',
    body: 'X-crosshatch — bytes that hold no data: stride-alignment padding before chunk 0, and the surplus cells of the near-square grid past the real chunk count.',
    icon: <DeadSpaceIcon />,
  },
  {
    title: 'Occupancy region-map',
    body: 'An occupancy page is not chunked — it draws a mini allocation-map of the file-page range it governs, each sub-cell the allocated fraction (dark → cyan).',
    icon: <OccupancyMapIcon />,
  },
  {
    title: 'Chunk index',
    body: 'A `#globalId [inPageIndex]` label once a chunk is large enough; it fades out as the L4 content fades in.',
    icon: <span className="inline-block h-[18px] w-[18px] text-center text-[9px] text-muted-foreground">#7</span>,
  },
];

const L4_ITEMS: readonly SymbolItem[] = [
  {
    title: 'Content cells',
    body: 'A decodable chunk laid out as its payload — one cell per field / row / key, coloured by content semantics (e.g. a component instance, a page directory).',
    icon: <ContentCellsIcon />,
  },
  {
    title: 'Cluster entity sub-grid',
    body: 'A cluster chunk draws one cell per entity slot, lit by the live-entity bitmap: cyan = occupied, dark = free — the “level underneath” a cluster.',
    icon: <EntitySubGridIcon />,
  },
  {
    title: 'Component overlay',
    body: 'On a cluster, pick a component in the chunk’s Detail card: slots recolour green = component enabled, dim red = occupied but disabled, dark = free.',
    icon: <ComponentOverlayIcon />,
  },
  {
    title: 'Inspector-only kinds',
    body: 'VSBS, string-table and hashmap chunks keep their L3 fill on the map; their decode (chain links, element / byte counts, string preview, bucket entries + overflow link) shows in the Detail panel.',
    icon: <InspectorIcon />,
  },
  {
    title: 'Hover / selection outline',
    body: 'The hovered (and the double-click-selected) chunk or content cell gets a bright outline; double-click also zooms it to fit.',
    icon: <OutlineIcon />,
  },
  {
    title: 'Unknown tile',
    body: 'A chunk we cannot decode (unsupported type or unparsed region). Diagonal hatch, never blank.',
    icon: <UnknownTileIcon />,
  },
];

// ── Free-space composition bar ────────────────────────────────────────────────────────────────────────────

function CompositionBar({ composition }: { composition: FreeSpaceComposition }) {
  const { totalBytes, liveBytes, overheadBytes, freeBytes } = composition;
  const parts = [
    { label: 'Live', bytes: liveBytes, color: rgbCss(USED_RGB) },
    { label: 'Overhead', bytes: overheadBytes, color: rgbCss(PAGE_TYPE_RGB[DbPageType.Root]) },
    { label: 'Free', bytes: freeBytes, color: rgbCss(FREE_RGB) },
  ];
  return (
    <div className="flex flex-col gap-1.5">
      <div className="flex h-4 w-full overflow-hidden rounded border border-border">
        {parts.map((p) => (
          <div
            key={p.label}
            style={{ width: `${totalBytes > 0 ? (p.bytes / totalBytes) * 100 : 0}%`, backgroundColor: p.color }}
            title={`${p.label}: ${formatFileSize(p.bytes)}`}
          />
        ))}
      </div>
      {parts.map((p) => (
        <div key={p.label} className="flex items-center justify-between gap-2 text-[11px]">
          <span className="flex items-center gap-1.5 text-muted-foreground">
            <span className="inline-block h-2.5 w-2.5 rounded-sm" style={{ backgroundColor: p.color }} />
            {p.label}
          </span>
          <span className="font-mono tabular-nums text-foreground">{formatFileSize(p.bytes)}</span>
        </div>
      ))}
      <div className="flex items-center justify-between gap-2 border-t border-border pt-1 text-[11px]">
        <span className="text-muted-foreground">File size</span>
        <span className="font-mono tabular-nums text-foreground">{formatFileSize(totalBytes)}</span>
      </div>
    </div>
  );
}

// ── Pathology flag list ───────────────────────────────────────────────────────────────────────────────────

const PATHOLOGY_LIST_CAP = 200;

function PathologyList({
  flags,
  segments,
  onFlyToPage,
}: {
  flags: PathologyFlag[];
  segments: StorageSegmentDto[];
  onFlyToPage: (page: number) => void;
}) {
  if (flags.length === 0) {
    return (
      <p className="text-[11px] text-muted-foreground">
        No under-filled pages in the scanned region. Zoom across the map to scan more.
      </p>
    );
  }
  const shown = flags.slice(0, PATHOLOGY_LIST_CAP);
  return (
    <div className="flex flex-col gap-0.5">
      <p className="text-[11px] text-muted-foreground">
        {flags.length} under-filled page{flags.length === 1 ? '' : 's'} (chunk fill below 25 %).
      </p>
      {shown.map((f) => {
        const seg = segments.find((s) => s.id === f.ownerSegmentId);
        const label = seg ? (seg.typeName.length > 0 ? seg.typeName : `${seg.kind} #${seg.id}`) : 'no segment';
        return (
          <button
            key={f.pageIndex}
            type="button"
            onClick={() => onFlyToPage(f.pageIndex)}
            className="flex items-center justify-between gap-2 rounded px-1 py-0.5 text-left text-[11px] hover:bg-muted/60"
          >
            <span className="truncate text-muted-foreground">
              <span className="font-mono text-foreground">#{f.pageIndex}</span> {label}
            </span>
            <span className="font-mono tabular-nums text-destructive">{(f.fillRatio * 100).toFixed(0)} %</span>
          </button>
        );
      })}
      {flags.length > PATHOLOGY_LIST_CAP && (
        <p className="text-[10px] text-muted-foreground">…and {flags.length - PATHOLOGY_LIST_CAP} more.</p>
      )}
    </div>
  );
}
