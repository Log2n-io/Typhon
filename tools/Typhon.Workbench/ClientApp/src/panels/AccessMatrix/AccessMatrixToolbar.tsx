import { Separator } from '@/components/ui/separator';
import type { GranularityLevel } from '@/panels/DataFlow/useDataFlowViewStore';
import { type ColumnSort, type RowSort, useAccessMatrixViewStore } from './useAccessMatrixViewStore';

/**
 * Top toolbar for the Access Matrix. Three controls:
 * - Granularity slider (L0–L4) — mirrors the Data Flow Timeline's slider so the two panels stay in sync.
 * - Row sort (Topology / Cluster) — re-orders the Y axis.
 * - Column sort (Phase + Dependency / Cluster) — re-orders the X axis.
 *
 * Phase-collapse menu is deferred to Phase D (where cross-panel `phase` selection lands and makes the affordance
 * meaningful — a collapse with no cross-panel feedback is just a per-panel hide).
 */
export default function AccessMatrixToolbar() {
  const granularityLevel = useAccessMatrixViewStore((s) => s.granularityLevel);
  const rowSort = useAccessMatrixViewStore((s) => s.rowSort);
  const colSort = useAccessMatrixViewStore((s) => s.colSort);
  const setGranularity = useAccessMatrixViewStore((s) => s.setGranularityLevel);
  const setRowSort = useAccessMatrixViewStore((s) => s.setRowSort);
  const setColSort = useAccessMatrixViewStore((s) => s.setColSort);

  return (
    <div className="flex shrink-0 items-center gap-2 border-b border-border bg-card px-2 py-1">
      <span className="text-xs text-muted-foreground">Granularity</span>
      <GranularitySegmented value={granularityLevel} onChange={setGranularity} />

      <Separator orientation="vertical" className="h-6" />

      <span className="text-xs text-muted-foreground">Rows</span>
      <RowSortSegmented value={rowSort} onChange={setRowSort} />

      <Separator orientation="vertical" className="h-6" />

      <span className="text-xs text-muted-foreground">Columns</span>
      <ColumnSortSegmented value={colSort} onChange={setColSort} />
    </div>
  );
}

const GRANULARITY_LABELS: Record<GranularityLevel, string> = {
  L0: 'L0', L1: 'L1', L2: 'L2', L3: 'L3', L4: 'L4',
};

const GRANULARITY_DESCRIPTIONS: Record<GranularityLevel, string> = {
  L0: 'Domain — Components / Queues / Resources',
  L1: 'Phase × Domain',
  L2: 'Component-family (default)',
  L3: 'Component type',
  L4: 'Archetype × component (finest)',
};

function GranularitySegmented({
  value,
  onChange,
}: {
  value: GranularityLevel;
  onChange: (level: GranularityLevel) => void;
}) {
  const levels: GranularityLevel[] = ['L0', 'L1', 'L2', 'L3', 'L4'];
  return (
    <div className="flex overflow-hidden rounded-md border border-border">
      {levels.map((level) => (
        <button
          key={level}
          type="button"
          className={
            'h-7 px-2 text-xs leading-none ' +
            (value === level
              ? 'bg-primary text-primary-foreground'
              : 'bg-background text-foreground hover:bg-muted')
          }
          title={GRANULARITY_DESCRIPTIONS[level]}
          onClick={() => onChange(level)}
        >
          {GRANULARITY_LABELS[level]}
        </button>
      ))}
    </div>
  );
}

function RowSortSegmented({ value, onChange }: { value: RowSort; onChange: (s: RowSort) => void }) {
  const opts: { id: RowSort; label: string; tip: string }[] = [
    { id: 'topology', label: 'Topo', tip: 'Declaration order — matches Data Flow Timeline' },
    { id: 'cluster', label: 'Cluster', tip: 'Cosine-similarity cluster — groups rows touched by similar systems' },
  ];
  return (
    <div className="flex overflow-hidden rounded-md border border-border">
      {opts.map((o) => (
        <button
          key={o.id}
          type="button"
          className={
            'h-7 px-2 text-xs leading-none ' +
            (value === o.id ? 'bg-primary text-primary-foreground' : 'bg-background text-foreground hover:bg-muted')
          }
          title={o.tip}
          onClick={() => onChange(o.id)}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

function ColumnSortSegmented({ value, onChange }: { value: ColumnSort; onChange: (s: ColumnSort) => void }) {
  const opts: { id: ColumnSort; label: string; tip: string }[] = [
    {
      id: 'phase-then-dependency',
      label: 'Phase + dep',
      tip: 'Group by phase, sort by dependency order — matches System DAG swim-lanes',
    },
    {
      id: 'cluster',
      label: 'Cluster',
      tip: 'Cosine-similarity cluster — adjacent systems use similar data',
    },
  ];
  return (
    <div className="flex overflow-hidden rounded-md border border-border">
      {opts.map((o) => (
        <button
          key={o.id}
          type="button"
          className={
            'h-7 px-2 text-xs leading-none ' +
            (value === o.id ? 'bg-primary text-primary-foreground' : 'bg-background text-foreground hover:bg-muted')
          }
          title={o.tip}
          onClick={() => onChange(o.id)}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}
