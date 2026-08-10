import { Fragment, useState } from 'react';
import { ChevronDown, ChevronRight, MapPin } from 'lucide-react';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { StatusBadge } from '@/components/ui/status-badge';
import { severityTone, type Finding } from './integrityModel';

interface Props {
  findings: Finding[];
  /** Present only when a live session holds this same bundle — the File Map needs an engine. */
  onReveal?: (finding: Finding) => void;
}

/**
 * Findings, worst first, each expandable to its evidence.
 *
 * The finding **code** gets its own monospace column rather than being folded into the summary, because a
 * code is an API: alerts key on `CHK-PHY-01`, and someone reading this table is often here *because* an
 * alert fired. It has to be greppable by eye and copyable without selecting prose around it.
 *
 * The collapsed row carries the summary (one sentence, no jargon); the expanded row carries `detail` (the
 * evidence), the violated rule id, and — when repairing this finding would cost something — the loss.
 */
export default function FindingsTable({ findings, onReveal }: Props) {
  const [expanded, setExpanded] = useState<Set<string>>(new Set());

  const toggle = (key: string) =>
    setExpanded((prev) => {
      const next = new Set(prev);
      if (!next.delete(key)) {
        next.add(key);
      }
      return next;
    });

  if (findings.length === 0) {
    return (
      <div className="flex flex-1 items-center justify-center p-4">
        <p className="text-fs-base text-muted-foreground">No findings. Nothing in this database contradicts anything else in it.</p>
      </div>
    );
  }

  return (
    <div className="min-h-0 flex-1 overflow-auto">
      <Table className="text-fs-base">
        <TableHeader>
          <TableRow>
            <TableHead className="w-6" />
            <TableHead className="text-fs-sm">Severity</TableHead>
            <TableHead className="text-fs-sm">Code</TableHead>
            <TableHead className="text-fs-sm">Summary</TableHead>
            <TableHead className="text-fs-sm">Where</TableHead>
            <TableHead className="text-right text-fs-sm">Count</TableHead>
            <TableHead className="text-fs-sm">Repair</TableHead>
            <TableHead className="w-8" />
          </TableRow>
        </TableHeader>
        <TableBody>
          {findings.map((f, i) => {
            // Findings are not individually identified by the server, and the same code can legitimately
            // fire on several pages — so the row key is the tuple that actually distinguishes them.
            const key = `${f.code}@${f.locus.filePageIndex}#${i}`;
            const isOpen = expanded.has(key);
            return (
              <Fragment key={key}>
                <TableRow
                  className="cursor-pointer hover:bg-accent"
                  data-testid="integrity-finding-row"
                  data-finding-code={f.code}
                  data-severity={f.severity}
                  onClick={() => toggle(key)}
                >
                  <TableCell className="text-muted-foreground">
                    {isOpen ? <ChevronDown className="h-3 w-3" /> : <ChevronRight className="h-3 w-3" />}
                  </TableCell>
                  <TableCell>
                    <StatusBadge tone={severityTone(f.severity)}>{f.severity}</StatusBadge>
                  </TableCell>
                  <TableCell className="font-mono text-fs-sm">{f.code}</TableCell>
                  <TableCell>{f.summary}</TableCell>
                  <TableCell className="font-mono text-fs-sm text-muted-foreground">{f.locus.text}</TableCell>
                  <TableCell className="text-right tabular-nums">
                    {f.occurrences > 1 ? f.occurrences.toLocaleString() : ''}
                  </TableCell>
                  <TableCell className="text-fs-sm text-muted-foreground">{f.repair}</TableCell>
                  <TableCell className="text-right">
                    {onReveal && f.locus.filePageIndex >= 0 && (
                      <button
                        type="button"
                        onClick={(e) => {
                          e.stopPropagation();
                          onReveal(f);
                        }}
                        data-testid="integrity-finding-reveal"
                        title="Reveal this page in the File Map"
                        className="rounded border border-border px-1 py-0.5 text-muted-foreground hover:bg-accent hover:text-foreground"
                      >
                        <MapPin className="h-3 w-3" />
                      </button>
                    )}
                  </TableCell>
                </TableRow>

                {isOpen && (
                  <TableRow className="bg-muted/30 hover:bg-muted/30">
                    <TableCell />
                    <TableCell colSpan={7} className="py-2">
                      <div className="flex flex-col gap-1.5 text-fs-sm">
                        <p className="select-text whitespace-pre-wrap text-foreground">{f.detail}</p>
                        <div className="flex flex-wrap gap-x-4 gap-y-0.5 text-muted-foreground">
                          {f.ruleId && (
                            <span>
                              Rule <span className="font-mono text-foreground">{f.ruleId}</span>
                            </span>
                          )}
                          <span>
                            Confidence <span className="text-foreground">{f.confidence}</span>
                            {f.confidence !== 'Confirmed' && ' — the database was not quiescent when this was observed'}
                          </span>
                        </div>
                        {f.loss.kind !== 'None' && (
                          <p className="mt-0.5 rounded border border-border bg-background px-2 py-1">
                            <span className="text-foreground">Repairing this costs: </span>
                            {f.loss.count} {f.loss.kind}
                            {f.loss.component ? ` · ${f.loss.component}` : ''}
                            {f.loss.explanation ? ` — ${f.loss.explanation}` : ''}
                          </p>
                        )}
                      </div>
                    </TableCell>
                  </TableRow>
                )}
              </Fragment>
            );
          })}
        </TableBody>
      </Table>
    </div>
  );
}
