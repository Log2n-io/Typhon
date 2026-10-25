import { X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { StatusBadge } from '@/components/ui/status-badge';
import type {
  BackupCompactionResultDto,
  BackupCreateResultDto,
  BackupDrillReportDto,
  BackupRestoreResultDto,
  BackupRetentionResultDto,
  BackupVerifyReportDto,
} from '@/api/generated/model';
import {
  compactionFailures,
  drillTone,
  formatBytes,
  isTerminal,
  jobProgress,
  normalizePoint,
  verdictTone,
  verifyFindings,
  type Job,
} from './backupsModel';

const KIND_LABEL: Readonly<Record<string, string>> = {
  create: 'Back up now',
  verify: 'Verify',
  drill: 'Drill',
  compact: 'Compact',
  retention: 'Retention',
  restore: 'Restore',
};

const ROUTE_LABEL: Readonly<Record<string, string>> = {
  inProcess: 'online, through this Workbench’s session',
  request: 'by request to the engine holding the database',
  offline: 'offline, from the closed bundle',
};

/** What a cancel left, per kind: each is honoured at a different point (07 §5, §3.5). */
const CANCELLED: Readonly<Record<string, string>> = {
  create: 'Cancelled: no point was published.',
  verify: 'Cancelled: the verify stopped before its verdict; it changes nothing.',
  drill: 'Cancelled: the drill stopped and its scratch was removed; no drill is recorded.',
  compact: 'Cancelled: no base was published; the chain is as it was.',
  retention:
    'Cancelled between deletions: the packs deleted before it stay deleted, and every point kept is whole (newest first).',
  restore: 'Cancelled before the publish: nothing was put at the target.',
};

const num = (v: number | string | null | undefined) => (v == null ? 0 : Number(v));

interface JobCardProps {
  job: Job | null;
  lost: boolean;
  onCancel: (jobId: string) => void;
  onDismiss: () => void;
  onOpenRestored: (bundle: string) => void;
}

/**
 * The running or finished job (#1417 §2 Reports): progress while it runs (a percentage only when the operation knows its total), then its result with
 * the facts the CLI prints for the same verb. A job no longer kept says so rather than vanishing.
 */
export default function JobCard({ job, lost, onCancel, onDismiss, onOpenRestored }: JobCardProps) {
  if (lost) {
    return (
      <div
        className="shrink-0 border-b border-border px-3 py-2 text-fs-sm text-muted-foreground"
        data-testid="backups-job-lost"
      >
        That job is no longer known: finished jobs are kept an hour, and none survives a Workbench restart.
        <Button variant="ghost" onClick={onDismiss} className="ml-2 h-6 px-1.5 text-fs-sm">
          Dismiss
        </Button>
      </div>
    );
  }
  if (!job) {
    return null;
  }

  const terminal = isTerminal(job.state);
  const progress = jobProgress(job);
  const raw = job.raw;

  return (
    <div className="shrink-0 border-b border-border px-3 py-2" data-testid="backups-job" data-state={job.state}>
      <div className="flex items-center gap-2">
        <span className="text-fs-base font-medium">{KIND_LABEL[job.kind] ?? job.kind}</span>
        {job.route && <span className="text-fs-sm text-muted-foreground">{ROUTE_LABEL[job.route] ?? job.route}</span>}
        <StatusBadge
          tone={
            job.state === 'done'
              ? 'success'
              : job.state === 'error'
                ? 'error'
                : job.state === 'cancelled'
                  ? 'neutral'
                  : 'info'
          }
        >
          {job.state}
        </StatusBadge>
        <span className="ml-auto" />
        {!terminal ? (
          <Button
            variant="outline"
            onClick={() => onCancel(job.jobId)}
            className="h-6 px-2 text-fs-sm"
            data-testid="backups-job-cancel"
          >
            Cancel
          </Button>
        ) : (
          <Button
            variant="ghost"
            onClick={onDismiss}
            aria-label="Dismiss"
            className="h-6 px-1.5"
            data-testid="backups-job-dismiss"
          >
            <X className="h-3.5 w-3.5" />
          </Button>
        )}
      </div>

      {!terminal && (
        <div className="mt-1" data-testid="backups-job-progress">
          <div className="text-fs-sm text-muted-foreground">{progress.text}</div>
          <div className="mt-0.5 h-1.5 w-full overflow-hidden rounded bg-muted">
            {progress.determinate ? (
              <div className="h-full bg-primary" style={{ width: `${progress.fraction * 100}%` }} />
            ) : (
              <div className="h-full w-1/3 animate-pulse bg-primary/60" />
            )}
          </div>
          {job.kind === 'restore' && (
            <div className="mt-0.5 text-fs-xs text-muted-foreground">A cancel is honoured before the publish only.</div>
          )}
        </div>
      )}

      {job.state === 'error' && job.error && (
        <div className="mt-1 text-fs-sm text-destructive" data-testid="backups-job-error">
          <b>{job.error.category}</b> — {job.error.message}
        </div>
      )}
      {job.state === 'cancelled' && (
        <div className="mt-1 text-fs-sm text-muted-foreground" data-testid="backups-job-cancelled">
          {CANCELLED[job.kind] ?? 'Cancelled.'}
        </div>
      )}
      {job.note && <div className="mt-1 text-fs-sm text-amber-600">{job.note}</div>}

      {job.state === 'done' && raw.create && <CreateReceipt r={raw.create} />}
      {job.state === 'done' && raw.verify && <VerifyReport r={raw.verify} />}
      {job.state === 'done' && raw.drill && <DrillReport r={raw.drill} />}
      {job.state === 'done' && raw.compact && <CompactReceipt r={raw.compact} />}
      {job.state === 'done' && raw.retention && <RetentionReceipt r={raw.retention} />}
      {job.state === 'done' && raw.restore && <RestoreReceipt r={raw.restore} onOpen={onOpenRestored} />}
    </div>
  );
}

function CreateReceipt({ r }: { r: BackupCreateResultDto }) {
  const p = normalizePoint(r.point);
  if (!p) {
    return null;
  }
  return (
    <div className="mt-1 text-fs-sm" data-testid="backups-create-receipt">
      Point <b>#{p.backupId}</b> · {p.kind} · C {p.checkpointLsn.toLocaleString()} · P {p.pointLsn.toLocaleString()} ·{' '}
      {p.pageCount.toLocaleString()} pages · {formatBytes(p.packBytes)}
      {p.suspect && <span className="ml-1 text-amber-600">· suspect (the live database had damaged pages)</span>}
      {r.requestId && <span className="ml-1 text-muted-foreground">· request {r.requestId}</span>}
    </div>
  );
}

function VerifyReport({ r }: { r: BackupVerifyReportDto }) {
  const findings = verifyFindings(r);
  const broken = (r.points ?? []).filter((p) => !p.restorable);
  return (
    <div className="mt-1 text-fs-sm" data-testid="backups-verify-report">
      <div className="flex items-center gap-2">
        <StatusBadge tone={verdictTone(r.verdict ?? '')}>{r.verdict}</StatusBadge>
        <span>
          {r.level} · {(r.packs ?? []).length} pack(s) · {formatBytes(num(r.bytesRead))} read ·{' '}
          {(num(r.durationMs) / 1000).toFixed(1)} s
        </span>
      </div>
      {broken.length > 0 && (
        <div className="mt-0.5 text-destructive">
          Not restorable:{' '}
          {broken
            .map(
              (p) =>
                `#${p.backupId}${(p.brokenBy ?? []).length ? ` (broken by #${(p.brokenBy ?? []).join(', #')})` : ''}`,
            )
            .join(' · ')}
        </div>
      )}
      {findings.length > 0 && (
        <table className="mt-1 w-full text-fs-xs" data-testid="backups-verify-findings">
          <tbody>
            {findings.map((f, i) => (
              <tr key={i} className={f.severity === 'Damage' ? 'text-destructive' : 'text-muted-foreground'}>
                <td className="pr-2 font-mono">{f.code}</td>
                <td className="pr-2">{f.severity}</td>
                <td className="pr-2 font-mono">{f.file}</td>
                <td className="pr-2">{f.pageId >= 0 ? `page ${f.pageId}` : ''}</td>
                <td>{f.message}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

function DrillReport({ r }: { r: BackupDrillReportDto }) {
  const p = normalizePoint(r.point);
  return (
    <div className="mt-1 text-fs-sm" data-testid="backups-drill-report">
      <div className="flex items-center gap-2">
        <StatusBadge tone={drillTone(r)}>{r.outcome}</StatusBadge>
        {p && (
          <span>
            #{p.backupId} · P {p.pointLsn.toLocaleString()}
          </span>
        )}
        <span className="text-muted-foreground">
          restore {(num(r.restoreMs) / 1000).toFixed(1)} s · open {(num(r.openMs) / 1000).toFixed(1)} s · check{' '}
          {(num(r.checkMs) / 1000).toFixed(1)} s
        </span>
      </div>
      <div className="mt-0.5">
        Recovery replayed {num(r.recovery?.recordsReplayed).toLocaleString()} record(s) · check {r.verdict} (
        {num(r.findings)} finding(s))
        {num(r.suspectPages) > 0 && (
          <span className="ml-1 text-amber-600">· {num(r.suspectPages)} suspect page(s)</span>
        )}
      </div>
      {r.error && (
        <div className={r.outcome === 'Error' ? 'mt-0.5 text-muted-foreground' : 'mt-0.5 text-destructive'}>
          {r.error}
        </div>
      )}
      {r.needsSchema && (
        <div className="mt-0.5 text-amber-600" data-testid="backups-drill-needs-schema">
          This point needs the application’s schema to settle: its WAL tail holds records of archetypes this drill could
          not register. Open the database in this Workbench with its schema, then drill again — the drill loads the
          session’s schema. This says nothing about the point’s health.
        </div>
      )}
    </div>
  );
}

function CompactReceipt({ r }: { r: BackupCompactionResultDto }) {
  const p = normalizePoint(r.point);
  return (
    <div className="mt-1 text-fs-sm" data-testid="backups-compact-receipt">
      {r.alreadyBase
        ? `#${p?.backupId} already is a base: nothing written.`
        : `#${p?.backupId} compacted: ${num(r.pages).toLocaleString()} pages (${num(r.holePages)} holes) from ${num(r.chainDepth)} pack(s), ` +
          `${formatBytes(num(r.bytesRead))} read, ${formatBytes(num(r.bytesWritten))} written, ${(num(r.durationMs) / 1000).toFixed(1)} s.`}
    </div>
  );
}

function RetentionReceipt({ r }: { r: BackupRetentionResultDto }) {
  const failed = compactionFailures(r);
  return (
    <div className="mt-1 text-fs-sm" data-testid="backups-retention-receipt">
      {(r.compactions ?? []).length} compaction(s), {(r.deleted ?? []).length} pack(s) deleted,{' '}
      {formatBytes(num(r.bytesFreed))} freed.
      {(r.deleted ?? []).length > 0 && (
        <div className="font-mono text-fs-xs text-muted-foreground">{(r.deleted ?? []).join(' · ')}</div>
      )}
      {failed.map((f) =>
        f.damaged ? (
          <div key={f.backupId} className="text-destructive" data-testid={`backups-retention-damaged-${f.backupId}`}>
            #{f.backupId} was not compacted: a pack of its chain is damaged, so this kept point may not restore — verify
            full. {f.message}
          </div>
        ) : (
          <div key={f.backupId} className="text-amber-600" data-testid={`backups-retention-failed-${f.backupId}`}>
            #{f.backupId} was not compacted (nothing published; its chain keeps every pack): {f.message}
          </div>
        ),
      )}
    </div>
  );
}

function RestoreReceipt({ r, onOpen }: { r: BackupRestoreResultDto; onOpen: (bundle: string) => void }) {
  const settle = r.settle;
  const settled = !!settle?.ran && !settle.error;
  return (
    <div className="mt-1 text-fs-sm" data-testid="backups-restore-receipt">
      <div>
        Restored into <span className="font-mono">{r.target}</span> · {num(r.pagesWritten).toLocaleString()} pages from{' '}
        {num(r.chainDepth)} pack(s) · {num(r.tailSegments)} tail segment(s)
        {num(r.archivedSegments) > 0 && ` · ${num(r.archivedSegments)} archived segment(s)`} · ends at LSN{' '}
        {num(r.effectiveLsn).toLocaleString()}
      </div>
      {r.displacedBundle && (
        <div className="text-muted-foreground">
          The bundle that was there is now <span className="font-mono">{r.displacedBundle}</span> (moved aside, never
          deleted).
        </div>
      )}
      {(r.suspectPages ?? []).length > 0 && (
        <div className="text-amber-600">
          {(r.suspectPages ?? []).length} page(s) the live database had damaged at capture.
        </div>
      )}
      {(r.damagedPages ?? []).length > 0 && (
        <div className="text-destructive">
          {(r.damagedPages ?? []).length} page(s) written despite failing their check (forensic restore).
        </div>
      )}
      {r.newTimelineId && <div className="text-muted-foreground">Opens on a new WAL timeline {r.newTimelineId}.</div>}
      {settled ? (
        <div className="mt-1 flex items-center gap-2">
          <span className="text-emerald-600">
            Settled: opened
            {settle.crashRecoveryRan
              ? `, ${num(settle.recovery?.recordsReplayed).toLocaleString()} record(s) replayed,`
              : ''}{' '}
            and closed cleanly{settle.withSchema ? ' with the session’s schema' : ''}.
          </span>
          <Button
            onClick={() => r.target && onOpen(r.target)}
            className="h-7 px-2 text-fs-sm"
            data-testid="backups-open-restored"
          >
            Open restored database
          </Button>
        </div>
      ) : settle?.needsSchema ? (
        <div className="mt-1 text-amber-600" data-testid="backups-settle-needs-schema">
          This point needs the application’s schema to settle: its WAL tail holds records of archetypes this Workbench
          could not register. It is not damage. Open the restored bundle where the application’s schema is available
          (register its directory in Options → Schema, then File → Open); that open replays the tail.
        </div>
      ) : (
        <div className="mt-1 text-destructive" data-testid="backups-settle-failed">
          The restored database did not settle{settle?.error ? `: ${settle.error}` : ''}. It is published, but do not
          open it until it is.
        </div>
      )}
    </div>
  );
}
