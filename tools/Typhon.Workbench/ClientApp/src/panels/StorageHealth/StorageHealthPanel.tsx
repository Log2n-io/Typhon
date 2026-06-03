import { useMemo, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { RefreshCw } from 'lucide-react';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useSessionStore } from '@/stores/useSessionStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useDbMapHealth } from '@/hooks/dbmap/useDbMapHealth';
import { useComponentNames } from '@/hooks/queryConsole/useComponentNames';
import { openDbMapForComponent } from '@/shell/commands/openDbMap';
import { segmentRgb, rgbCss } from '@/libs/dbmap/dbMapColors';
import { sortHealthSegments, type HealthSortKey } from './storageHealthModel';
import { formatBytes } from '@/libs/formatBytes';

/**
 * Storage Health (Stage 2 Phase 3, GAP-16) — the *aggregate* storage dashboard, the non-spatial complement to
 * the File Map. A whole-DB summary + a sortable per-segment table (worst offenders rise to the top). A row
 * selects the segment on the bus (→ Inspector segment card) and offers the spatial pivot, Reveal in File Map.
 * Data comes from the server-side `GET dbmap/health` rollup (one call, not per-segment harvesting).
 */
export default function StorageHealthPanel(_props: IDockviewPanelProps) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const { data, isLoading, isError, refetch, isFetching } = useDbMapHealth(sessionId);
  const select = useSelectionStore((s) => s.select);
  // Friendly component name for component-table segments (matches the File Map's segment labels); non-component
  // segments carry no typeName and fall back to `#<id>`.
  const { label: componentName } = useComponentNames();

  const [sortKey, setSortKey] = useState<HealthSortKey>('occupancyPct');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('desc');
  const sorted = useMemo(
    () => (data ? sortHealthSegments(data.segments, sortKey, sortDir) : []),
    [data, sortKey, sortDir],
  );

  const onSort = (key: HealthSortKey) => {
    if (key === sortKey) {
      setSortDir((d) => (d === 'desc' ? 'asc' : 'desc'));
    } else {
      setSortKey(key);
      setSortDir('desc');
    }
  };

  if (isError) {
    return <div data-testid="storage-health" className="p-3 text-fs-base text-destructive">Failed to load storage health.</div>;
  }
  if (isLoading || !data) {
    return (
      <div data-testid="storage-health" className="flex h-full items-center justify-center bg-background p-4 text-center">
        <p className="text-fs-base text-muted-foreground">Loading storage health…</p>
      </div>
    );
  }

  const usedPct = data.dataFilePageCount > 0 ? (data.usedPageCount / data.dataFilePageCount) * 100 : 0;

  return (
    <div data-testid="storage-health" className="flex h-full w-full flex-col overflow-hidden bg-background">
      {/* DB-level summary */}
      <div className="wb-pane-header flex flex-wrap items-center gap-x-3 gap-y-0.5 border-b border-border px-3 py-1.5 text-fs-sm text-muted-foreground">
        <span className="font-mono text-fs-base text-foreground">{data.databaseName}</span>
        <span className="tabular-nums">{formatBytes(data.dataFileBytes)}</span>
        <span>·</span>
        <span className="tabular-nums">{data.dataFilePageCount.toLocaleString()} pages</span>
        <span>·</span>
        <span className="tabular-nums">{usedPct.toFixed(0)}% used</span>
        <span>·</span>
        <span className="tabular-nums">reclaimable {formatBytes(data.reclaimableBytes)}</span>
        <span>·</span>
        <span className="tabular-nums">frag {data.fragmentationPct.toFixed(0)}%</span>
        <span>·</span>
        <span className="tabular-nums" title="WAL tail / checkpoint LSN">WAL @{data.checkpointLsn.toLocaleString()}</span>
        <button
          type="button"
          onClick={() => void refetch()}
          title="Refresh storage health"
          aria-label="Refresh storage health"
          className="ml-auto flex h-5 w-5 items-center justify-center rounded text-muted-foreground hover:bg-muted hover:text-foreground"
        >
          <RefreshCw className={`h-3 w-3 ${isFetching ? 'animate-spin' : ''}`} />
        </button>
      </div>

      {/* Per-segment table */}
      <div className="min-h-0 flex-1 overflow-auto">
        {data.segments.length === 0 ? (
          <p className="p-3 text-fs-base text-muted-foreground">Empty database — no segments.</p>
        ) : (
          <Table className="text-fs-base">
            <TableHeader>
              <TableRow>
                <SortHead label="Segment" col="typeName" sortKey={sortKey} sortDir={sortDir} onSort={onSort} />
                <SortHead label="Kind" col="kind" sortKey={sortKey} sortDir={sortDir} onSort={onSort} />
                <SortHead label="Pages" col="pageCount" sortKey={sortKey} sortDir={sortDir} onSort={onSort} numeric />
                <SortHead label="Occ%" col="occupancyPct" sortKey={sortKey} sortDir={sortDir} onSort={onSort} numeric />
                <SortHead label="Fill%" col="chunkFillPct" sortKey={sortKey} sortDir={sortDir} onSort={onSort} numeric />
                <SortHead label="Recl." col="reclaimableBytes" sortKey={sortKey} sortDir={sortDir} onSort={onSort} numeric />
                <TableHead className="text-right text-fs-sm"></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {sorted.map((s) => (
                <TableRow
                  key={s.id}
                  className="cursor-pointer hover:bg-accent"
                  data-testid="storage-health-segment-row"
                  data-segment-id={s.id}
                  onClick={() => select('segment', { kind: 'segment', segmentId: s.id, typeName: s.typeName || undefined })}
                >
                  <TableCell className="font-mono">
                    {/* DS-2: the same stable per-segment hue the File Map paints, so a segment reads identically across both views. */}
                    <span
                      className="mr-1.5 inline-block h-2 w-2 rounded-sm align-middle"
                      style={{ backgroundColor: rgbCss(segmentRgb(s.id)) }}
                    />
                    {s.typeName ? componentName(s.typeName) : `#${s.id}`}
                  </TableCell>
                  <TableCell>{s.kind}</TableCell>
                  <TableCell className="text-right tabular-nums">{s.pageCount.toLocaleString()}</TableCell>
                  <TableCell className="text-right tabular-nums">{s.occupancyPct.toFixed(0)}%</TableCell>
                  <TableCell className="text-right tabular-nums">{s.chunkCapacity > 0 ? `${s.chunkFillPct.toFixed(0)}%` : '—'}</TableCell>
                  <TableCell className="text-right tabular-nums">{formatBytes(s.reclaimableBytes)}</TableCell>
                  <TableCell className="text-right">
                    {s.typeName && (
                      <button
                        type="button"
                        onClick={(e) => {
                          e.stopPropagation();
                          openDbMapForComponent(s.typeName);
                        }}
                        data-testid="storage-health-reveal"
                        title="Reveal this segment in the File Map"
                        className="rounded border border-border px-1.5 py-0.5 text-fs-xs text-foreground hover:bg-accent"
                      >
                        Map →
                      </button>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </div>
    </div>
  );
}

function SortHead({
  label,
  col,
  sortKey,
  sortDir,
  onSort,
  numeric,
}: {
  label: string;
  col: HealthSortKey;
  sortKey: HealthSortKey;
  sortDir: 'asc' | 'desc';
  onSort: (key: HealthSortKey) => void;
  numeric?: boolean;
}) {
  const active = col === sortKey;
  return (
    <TableHead className={`text-fs-sm ${numeric ? 'text-right' : ''}`}>
      <button
        type="button"
        onClick={() => onSort(col)}
        className={`hover:text-foreground ${active ? 'text-foreground' : 'text-muted-foreground'}`}
      >
        {label}
        {active ? (sortDir === 'desc' ? ' ↓' : ' ↑') : ''}
      </button>
    </TableHead>
  );
}
