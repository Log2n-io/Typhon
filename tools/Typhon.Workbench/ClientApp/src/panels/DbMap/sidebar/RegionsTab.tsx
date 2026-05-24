import { useMemo, useState } from 'react';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { formatFileSize } from '@/lib/formatters';
import { sortRegions, type DbMapRegion, type RegionSortKey } from '@/libs/dbmap/dbMapRegions';
import { NO_SEGMENT, PAGE_TYPE_LABELS, type StorageSegmentDto } from '@/libs/dbmap/types';

// The side-rail Regions tab (Module 15, A3, §4.5) — the RLE region bands as a sortable "what is using my
// space" table. Rows are contiguous same-type / same-segment runs; a row click flies the camera to the run.

/** Cap on rendered rows — a churned database can coalesce into thousands of runs; the rest are summarised. */
const ROW_CAP = 500;

interface RegionsTabProps {
  regions: DbMapRegion[];
  segments: StorageSegmentDto[];
  onFlyToRegion: (startPage: number, pageCount: number) => void;
  /** Selects a run's owning segment so the Detail panel shows its A6 harvest summary card. */
  onSelectSegment: (segmentId: number) => void;
}

const COLUMNS: { key: RegionSortKey; label: string; align: 'left' | 'right' }[] = [
  { key: 'start', label: 'Start', align: 'right' },
  { key: 'size', label: 'Size', align: 'right' },
  { key: 'type', label: 'Type', align: 'left' },
  { key: 'fragmentation', label: 'Segment', align: 'left' },
  { key: 'fill', label: 'Fill', align: 'right' },
];

export function RegionsTab({ regions, segments, onFlyToRegion, onSelectSegment }: RegionsTabProps) {
  const [sortKey, setSortKey] = useState<RegionSortKey>('start');
  const [ascending, setAscending] = useState(true);

  const sorted = useMemo(() => sortRegions(regions, sortKey, ascending), [regions, sortKey, ascending]);
  const segLabel = useMemo(() => {
    const byId = new Map(segments.map((s) => [s.id, s.typeName.length > 0 ? s.typeName : `${s.kind} #${s.id}`]));
    return (id: number) => (id === NO_SEGMENT ? '—' : byId.get(id) ?? `#${id}`);
  }, [segments]);

  const toggleSort = (key: RegionSortKey) => {
    if (key === sortKey) {
      setAscending((a) => !a);
    } else {
      setSortKey(key);
      // Size / fill / fragmentation read most usefully largest-first; start ascending.
      setAscending(key === 'start' || key === 'type');
    }
  };

  if (regions.length === 0) {
    return <p className="p-2 text-fs-sm text-muted-foreground">No regions — open a database.</p>;
  }

  return (
    <div className="flex flex-col">
      <p className="px-2 py-1 text-fs-sm text-muted-foreground">
        {regions.length} region{regions.length === 1 ? '' : 's'}
        {regions.length > ROW_CAP ? ` · showing ${ROW_CAP}` : ''}
      </p>
      <Table className="text-fs-sm">
        <TableHeader>
          <TableRow>
            {COLUMNS.map((c) => (
              <TableHead
                key={c.key}
                onClick={() => toggleSort(c.key)}
                className={`h-6 cursor-pointer px-2 py-0 text-fs-xs hover:bg-muted/50 ${
                  c.align === 'right' ? 'text-right' : 'text-left'
                }`}
              >
                {c.label}
                {sortKey === c.key ? (ascending ? ' ▲' : ' ▼') : ''}
              </TableHead>
            ))}
          </TableRow>
        </TableHeader>
        <TableBody>
          {sorted.slice(0, ROW_CAP).map((r) => (
            <TableRow
              key={r.startPage}
              onClick={() => {
                onFlyToRegion(r.startPage, r.pageCount);
                if (r.ownerSegmentId !== NO_SEGMENT) {
                  onSelectSegment(r.ownerSegmentId);
                }
              }}
              className="cursor-pointer"
              data-testid="dbmap-region-row"
            >
              <TableCell className="px-2 py-0.5 text-right font-mono tabular-nums">#{r.startPage}</TableCell>
              <TableCell className="px-2 py-0.5 text-right font-mono tabular-nums">
                {formatFileSize(r.byteSize)}
              </TableCell>
              <TableCell className="px-2 py-0.5">{PAGE_TYPE_LABELS[r.pageType] ?? 'Unknown'}</TableCell>
              <TableCell className="max-w-[90px] truncate px-2 py-0.5" title={segLabel(r.ownerSegmentId)}>
                {segLabel(r.ownerSegmentId)}
              </TableCell>
              <TableCell className="px-2 py-0.5 text-right font-mono tabular-nums">
                {r.fillAvg == null ? '—' : `${(r.fillAvg * 100).toFixed(0)} %`}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
