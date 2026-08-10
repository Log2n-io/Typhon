import { StatusBadge } from '@/components/ui/status-badge';
import { formatBytes } from '@/libs/formatBytes';
import {
  countBySeverity,
  severityTone,
  verdictBlurb,
  verdictTone,
  SEVERITY_RANK,
  type Report,
  type Severity,
} from './integrityModel';

/**
 * The one-word answer, its gloss, and the identity of what was scanned.
 *
 * The verdict is the headline because that is the first question an operator has — but the "clean shutdown"
 * flag sits right beside it deliberately. A `Sound` verdict on a database that did *not* shut down cleanly
 * means recovery worked; the same verdict after a clean close means nothing was ever at risk. Same word,
 * different amount of reassurance, and the flag is what distinguishes them.
 */
export default function VerdictBanner({ report }: { report: Report }) {
  const counts = countBySeverity(report.findings);
  const present = (Object.keys(SEVERITY_RANK) as Severity[])
    .filter((s) => counts[s] > 0)
    .sort((a, b) => SEVERITY_RANK[a] - SEVERITY_RANK[b]);

  const id = report.identity;

  return (
    <div className="shrink-0 border-b border-border px-3 py-2" data-testid="integrity-verdict">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <span data-testid="integrity-verdict-badge">
          <StatusBadge tone={verdictTone(report.verdict)} className="text-fs-lg">
            {report.verdict}
          </StatusBadge>
        </span>

        {present.length === 0 ? (
          <span className="text-fs-base text-muted-foreground">no findings</span>
        ) : (
          <span className="flex flex-wrap items-center gap-1">
            {present.map((s) => (
              <StatusBadge key={s} tone={severityTone(s)} title={`${counts[s]} ${s} finding(s)`}>
                {counts[s]} {s}
              </StatusBadge>
            ))}
          </span>
        )}

        <span className="ml-auto text-fs-sm tabular-nums text-muted-foreground">
          {report.depth} scan · {report.durationMs.toFixed(0)} ms
        </span>
      </div>

      <p className="mt-1 text-fs-base text-muted-foreground">{verdictBlurb(report.verdict)}</p>

      <div className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-0.5 text-fs-sm text-muted-foreground">
        <span className="font-mono text-foreground" title={report.source}>
          {id.name || '(unnamed)'}
        </span>
        <span>·</span>
        <span>format v{id.formatRevision}</span>
        <span>·</span>
        <span className="tabular-nums">{id.pageCount.toLocaleString()} pages</span>
        <span>·</span>
        <span className="tabular-nums">{formatBytes(id.sizeBytes)}</span>
        <span>·</span>
        <span className="tabular-nums" title="Last checkpoint LSN">
          LSN {id.checkpointLsn.toLocaleString()}
        </span>
        <span>·</span>
        {/* Not a finding on its own — an unclean flag is normal after a crash and the whole point of recovery.
            It is shown because it changes how much a green verdict is worth. */}
        <span className={id.cleanShutdown ? '' : 'text-amber-500'}>
          clean shutdown: {id.cleanShutdown ? 'yes' : 'NO'}
        </span>
        {id.walSegmentCount > 0 && (
          <>
            <span>·</span>
            <span className="tabular-nums">
              WAL {id.walSegmentCount} seg / {formatBytes(id.walBytes)}
            </span>
          </>
        )}
      </div>
    </div>
  );
}
