import { Check, CircleSlash, X } from 'lucide-react';
import { StatusBadge } from '@/components/ui/status-badge';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { verdictTone, type RepairOutcome } from './integrityModel';

/**
 * What actually happened, step by step.
 *
 * The receipt reports the *actual* result rather than restating the plan, because the two legitimately
 * differ: a step can be skipped when its finding turns out to have healed, and the realised loss can be
 * **smaller** than the estimate. A receipt that echoed the plan would hide both.
 *
 * The post-repair verification scan is embedded rather than linked. A repair tool that says "done" without
 * re-reading what it wrote is asking to be trusted on exactly the operation least deserving of trust.
 */
export default function RepairReceipt({ outcome, rehearsal }: { outcome: RepairOutcome; rehearsal?: boolean }) {
  const v = outcome.verification;

  return (
    <div className="flex min-h-0 flex-1 flex-col overflow-auto" data-testid={rehearsal ? 'integrity-receipt-dry' : 'integrity-receipt'}>
      {!rehearsal && (
        <div className="flex shrink-0 flex-wrap items-center gap-2 border-b border-border px-3 py-2">
          <StatusBadge tone={outcome.succeeded ? 'success' : 'error'}>
            {outcome.succeeded ? 'Repair completed' : 'Repair incomplete'}
          </StatusBadge>
          {outcome.backupPath && (
            <span className="select-text text-fs-sm text-muted-foreground" title={outcome.backupPath}>
              Pre-repair copy: <span className="font-mono text-foreground">{outcome.backupPath}</span>
            </span>
          )}
        </div>
      )}

      <Table className="text-fs-base">
        <TableHeader>
          <TableRow>
            <TableHead className="w-8 text-right text-fs-sm">#</TableHead>
            <TableHead className="text-fs-sm">Action</TableHead>
            <TableHead className="text-fs-sm">Outcome</TableHead>
            <TableHead className="text-fs-sm">Detail</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {outcome.results.map((r) => (
            <TableRow key={r.order} data-testid="integrity-result-row" data-outcome={r.outcome}>
              <TableCell className="text-right tabular-nums text-muted-foreground">{r.order}</TableCell>
              <TableCell className="font-mono text-fs-sm">{r.action}</TableCell>
              <TableCell>
                <span className="flex items-center gap-1">
                  <OutcomeIcon outcome={r.outcome} />
                  {r.outcome}
                </span>
              </TableCell>
              <TableCell className="text-fs-sm text-muted-foreground">{r.detail}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>

      {v && (
        <div className="mt-2 shrink-0 border-t border-border px-3 py-2" data-testid="integrity-verification">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-fs-base font-medium text-foreground">
              {rehearsal ? 'Verification the repair would produce' : 'Verified after repair'}
            </span>
            <StatusBadge tone={verdictTone(v.verdict)}>{v.verdict}</StatusBadge>
            <span className="text-fs-sm tabular-nums text-muted-foreground">
              {v.findings.length} finding{v.findings.length === 1 ? '' : 's'} · {v.totals.pagesScanned.toLocaleString()} pages
              re-read
            </span>
          </div>
          {v.findings.length > 0 && (
            <ul className="mt-1 list-inside list-disc text-fs-sm text-muted-foreground">
              {v.findings.slice(0, 8).map((f, i) => (
                <li key={`${f.code}-${i}`}>
                  <span className="font-mono text-foreground">{f.code}</span> {f.summary}
                </li>
              ))}
              {v.findings.length > 8 && <li>… re-scan for the full list</li>}
            </ul>
          )}
        </div>
      )}
    </div>
  );
}

function OutcomeIcon({ outcome }: { outcome: string }) {
  if (outcome === 'Succeeded') {
    return <Check className="h-3.5 w-3.5 text-emerald-500" />;
  }
  if (outcome === 'Skipped') {
    return <CircleSlash className="h-3.5 w-3.5 text-muted-foreground" />;
  }
  return <X className="h-3.5 w-3.5 text-destructive" />;
}
