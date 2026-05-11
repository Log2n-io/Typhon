import type { QueryExecutionDto } from '@/api/generated/model/queryExecutionDto';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { buildPhaseRows, formatCount, formatDelta, formatNs, totalWallNs, type PhaseRow } from './phaseRows';

interface Props {
  execution: QueryExecutionDto;
}

/**
 * Per-phase breakdown table for a single execution. Headers + rows match design doc §5.2 layout —
 * Phase / Estimate / Actual / Δ / Wall / Notes — with a "Total" footer summing wall-time.
 */
export function ExecutionInspectorTable({ execution }: Props) {
  const rows = buildPhaseRows(execution);
  const total = totalWallNs(rows);

  if (rows.length === 0) {
    return (
      <div className="flex h-full items-center justify-center bg-muted/10 text-[11px] text-muted-foreground">
        This execution carries no phase breakdown.
      </div>
    );
  }

  return (
    <Table className="text-[12px]">
      <TableHeader>
        <TableRow>
          <TableHead className="py-1 text-[11px]">Phase</TableHead>
          <TableHead className="py-1 text-right text-[11px]">Estimate</TableHead>
          <TableHead className="py-1 text-right text-[11px]">Actual</TableHead>
          <TableHead className="py-1 text-right text-[11px]">Δ</TableHead>
          <TableHead className="py-1 text-right text-[11px]">Wall</TableHead>
          <TableHead className="py-1 text-[11px]">Notes</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((r, i) => (
          <PhaseRowView key={i} row={r} />
        ))}
        <TableRow>
          <TableCell className="py-1 font-semibold">Total</TableCell>
          <TableCell />
          <TableCell />
          <TableCell />
          <TableCell className="py-1 text-right font-mono font-semibold tabular-nums">{formatNs(total)}</TableCell>
          <TableCell />
        </TableRow>
      </TableBody>
    </Table>
  );
}

function PhaseRowView({ row }: { row: PhaseRow }) {
  const deltaClass = deltaToneClass(row.delta);
  return (
    <TableRow data-testid="execution-phase-row" data-phase-name={row.phaseName}>
      <TableCell className="py-1 font-mono text-[11px] text-foreground">{row.phaseName}</TableCell>
      <TableCell className="py-1 text-right font-mono tabular-nums text-muted-foreground">{formatCount(row.estimate)}</TableCell>
      <TableCell className="py-1 text-right font-mono tabular-nums text-foreground">{formatCount(row.actual)}</TableCell>
      <TableCell className={`py-1 text-right font-mono tabular-nums ${deltaClass}`}>{formatDelta(row.delta)}</TableCell>
      <TableCell className="py-1 text-right font-mono tabular-nums">{formatNs(row.wallNs)}</TableCell>
      <TableCell className="py-1 text-[11px] text-muted-foreground">{row.notes || ''}</TableCell>
    </TableRow>
  );
}

/** Colorize Δ to flag planner-quality issues — large absolute deltas in either direction stand out. */
function deltaToneClass(delta: number | null): string {
  if (delta == null) return 'text-muted-foreground';
  const abs = Math.abs(delta);
  if (abs < 0.1) return 'text-muted-foreground';
  if (abs < 0.3) return 'text-amber-500';
  return 'text-destructive';
}
