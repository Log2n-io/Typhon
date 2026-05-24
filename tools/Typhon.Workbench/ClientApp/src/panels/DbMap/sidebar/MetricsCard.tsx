import { formatFileSize } from '@/lib/formatters';

// The fragmentation-lens metrics card (Module 15, A3, §4.3) — shown in the side-rail Legend tab when the
// fragmentation lens has a segment focused. All figures are computed client-side from the StructuralMap +
// detail tiles; the panel does the computation and passes the results in.

/** Contiguous-run histogram buckets — run lengths grouped for a compact bar chart. */
const RUN_BUCKETS: { label: string; max: number }[] = [
  { label: '1', max: 1 },
  { label: '2–4', max: 4 },
  { label: '5–16', max: 16 },
  { label: '17–64', max: 64 },
  { label: '65+', max: Infinity },
];

/** The fragmentation-lens metrics — computed by the panel, rendered by {@link MetricsCard}. */
export interface MetricsCardData {
  /** Human label for the focused segment, e.g. "Component #7 · Position". */
  segmentLabel: string;
  /** Segment directory still loading — figures are not yet meaningful. */
  loading: boolean;
  /** Fragmentation ratio 0..1 (pages out of physical order). */
  fragmentation: number;
  /** Fill-density ratio 0..1 (live ÷ allocated chunk slots). */
  fillDensity: number;
  /** How many of the segment's pages had a resident detail tile. */
  fillSampled: number;
  /** The segment's total page count. */
  segmentPageCount: number;
  /** Estimated reclaimable bytes (free chunk slots × stride). */
  reclaimableBytes: number;
  /** Contiguous-run lengths of the segment directory. */
  runs: number[];
}

function bucketRuns(runs: number[]): number[] {
  const counts = new Array<number>(RUN_BUCKETS.length).fill(0);
  for (const len of runs) {
    const idx = RUN_BUCKETS.findIndex((b) => len <= b.max);
    counts[idx >= 0 ? idx : RUN_BUCKETS.length - 1]++;
  }
  return counts;
}

function Stat({ label, value, note }: { label: string; value: string; note?: string }) {
  return (
    <div className="flex items-baseline justify-between gap-2">
      <span className="text-fs-sm text-muted-foreground">{label}</span>
      <span className="font-mono text-fs-sm tabular-nums text-foreground">
        {value}
        {note && <span className="ml-1 text-fs-xs text-muted-foreground">{note}</span>}
      </span>
    </div>
  );
}

export function MetricsCard(props: MetricsCardData) {
  const { segmentLabel, loading, fragmentation, fillDensity, fillSampled, segmentPageCount, reclaimableBytes, runs } =
    props;

  if (loading) {
    return <p className="px-1 py-2 text-fs-sm text-muted-foreground">Measuring {segmentLabel}…</p>;
  }

  const buckets = bucketRuns(runs);
  const maxBucket = Math.max(1, ...buckets);
  const partial = fillSampled > 0 && fillSampled < segmentPageCount;
  const fillNote = fillSampled === 0 ? '(no tiles)' : partial ? `(${fillSampled}/${segmentPageCount})` : undefined;

  return (
    <div className="flex flex-col gap-1.5 rounded border border-border bg-card p-2">
      <div className="truncate text-fs-sm font-semibold text-foreground" title={segmentLabel}>
        {segmentLabel}
      </div>
      <Stat label="Fragmentation" value={`${(fragmentation * 100).toFixed(1)} %`} />
      <Stat
        label="Fill density"
        value={fillSampled === 0 ? '—' : `${(fillDensity * 100).toFixed(1)} %`}
        note={fillNote}
      />
      <Stat
        label="Reclaimable"
        value={fillSampled === 0 ? '—' : `~${formatFileSize(reclaimableBytes)}`}
        note="est."
      />

      <div className="mt-1 flex flex-col gap-0.5">
        <span className="text-fs-xs text-muted-foreground">Contiguous runs</span>
        <div className="flex items-end gap-1" style={{ height: 32 }}>
          {RUN_BUCKETS.map((b, i) => (
            <div key={b.label} className="flex flex-1 flex-col items-center gap-0.5">
              <div
                className="w-full rounded-sm bg-primary/70"
                style={{ height: `${(buckets[i] / maxBucket) * 24}px` }}
                title={`${buckets[i]} run(s) of length ${b.label}`}
              />
              <span className="text-fs-2xs text-muted-foreground">{b.label}</span>
            </div>
          ))}
        </div>
      </div>

      <button
        type="button"
        disabled
        className="mt-1 cursor-not-allowed rounded border border-border bg-card px-1.5 py-0.5 text-fs-sm text-muted-foreground opacity-60"
        title="Compaction launches the Admin Ops compact wizard — the Admin Ops module (Module 11) is not yet available"
      >
        Compact…
      </button>
    </div>
  );
}
