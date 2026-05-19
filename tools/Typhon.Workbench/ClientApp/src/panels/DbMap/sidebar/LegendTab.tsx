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

// The side-rail Legend tab (Module 15, A3, §6.4): the active encoding's colour key, plus — when a lens is
// active — its analytical readout (the fragmentation metrics card, the free-space composition bar, or the
// pathology list). The panel computes every figure; this tab only lays them out.

interface LegendTabProps {
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
    return <span className="text-[11px] text-muted-foreground">One stable hue per segment id.</span>;
  }
  if (isDetailEncoding(encoding)) {
    return <DetailLegend encoding={encoding} />;
  }
  const entries =
    encoding === 'freeUsed'
      ? [
          { label: 'Free', color: rgbCss(FREE_RGB) },
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
        ].map((t) => ({ label: PAGE_TYPE_LABELS[t], color: rgbCss(PAGE_TYPE_RGB[t]) }));
  return <SwatchColumn entries={entries} />;
}

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

function SwatchColumn({ entries }: { entries: { label: string; color: string }[] }) {
  return (
    <div className="flex flex-col gap-0.5">
      {entries.map((e) => (
        <span key={e.label} className="flex items-center gap-1.5 text-[11px] text-muted-foreground">
          <span className="inline-block h-2.5 w-2.5 rounded-sm" style={{ backgroundColor: e.color }} />
          {e.label}
        </span>
      ))}
    </div>
  );
}

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
