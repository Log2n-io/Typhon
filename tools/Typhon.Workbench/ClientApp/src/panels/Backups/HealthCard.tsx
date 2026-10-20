import { StatusBadge } from '@/components/ui/status-badge';
import { cardHealth, formatAge, formatBytes, healthLabel, healthTone, type Status } from './backupsModel';

/**
 * The health card (#1417 §2): one verdict and why — the engine's when its status file reports one, else the store's from its packs — then the facts
 * behind it: the newest point and its age, the next due point, failures, a running capture, the archive's lag and last gap, the last drill.
 */
export default function HealthCard({ status }: { status: Status }) {
  const verdict = cardHealth(status);
  const engine = status.engine;
  const latest = engine?.lastPoint ?? status.storeStatus?.latest ?? null;
  const now = Date.now();

  return (
    <div className="shrink-0 border-b border-border px-3 py-2" data-testid="backups-health">
      <div className="flex flex-wrap items-center gap-2">
        {verdict ? (
          <>
            <StatusBadge tone={healthTone(verdict.health)}>{healthLabel(verdict.health)}</StatusBadge>
            <span className="text-fs-base text-foreground" data-testid="backups-health-reason">
              {verdict.reason}
            </span>
            <span className="text-fs-sm text-muted-foreground">
              ({verdict.source === 'engine' ? 'engine status' : 'from the store’s packs'})
            </span>
          </>
        ) : (
          <span className="text-fs-base text-muted-foreground" data-testid="backups-health-none">
            {status.storeError ?? 'No backup status yet.'}
          </span>
        )}
      </div>

      <dl className="mt-1.5 grid grid-cols-[max-content_1fr] gap-x-3 gap-y-0.5 text-fs-sm">
        {latest && (
          <>
            <dt className="text-muted-foreground">Newest point</dt>
            <dd>
              #{latest.backupId} · {latest.kind} · P {latest.pointLsn.toLocaleString()} ·{' '}
              {formatAge(latest.freezeUtc, now)}
              {latest.suspect && <span className="ml-1 text-amber-500">suspect</span>}
            </dd>
          </>
        )}
        {status.storeStatus?.latestRestorable && status.storeStatus.latestRestorable.backupId !== latest?.backupId && (
          <>
            <dt className="text-muted-foreground">Newest restorable</dt>
            <dd>#{status.storeStatus.latestRestorable.backupId}</dd>
          </>
        )}
        {engine && (
          <>
            <dt className="text-muted-foreground">Engine</dt>
            <dd>
              {engine.live ? `running (PID ${engine.pid} on ${engine.machine})` : 'not running — last known state'}
              {engine.state === 'Running' &&
                ` · ${engine.phase} ${engine.progressOfPages > 0 ? `${engine.progressPages}/${engine.progressOfPages} pages` : ''}`}
            </dd>
            {engine.nextDueUtc && (
              <>
                <dt className="text-muted-foreground">Next point due</dt>
                <dd>{new Date(engine.nextDueUtc).toLocaleString()}</dd>
              </>
            )}
            {engine.consecutiveFailures > 0 && (
              <>
                <dt className="text-muted-foreground">Failures</dt>
                <dd className="text-destructive">
                  {engine.consecutiveFailures} in a row{engine.lastFailure ? ` — ${engine.lastFailure}` : ''}
                </dd>
              </>
            )}
            {engine.archive.state && engine.archive.state !== 'Off' && (
              <>
                <dt className="text-muted-foreground">WAL archive</dt>
                <dd>
                  {engine.archive.state} · lag {formatBytes(engine.archive.lagBytes)},{' '}
                  {engine.archive.lagSeconds.toFixed(0)} s
                  {engine.archive.lastGapToLsn > 0 && (
                    <span className="ml-1 text-amber-500">
                      · gap LSN {engine.archive.lastGapFromLsn.toLocaleString()}..
                      {engine.archive.lastGapToLsn.toLocaleString()}
                    </span>
                  )}
                  {engine.archive.lastError && (
                    <span className="ml-1 text-destructive">· {engine.archive.lastError}</span>
                  )}
                </dd>
              </>
            )}
            {engine.lastDrill && (
              <>
                <dt className="text-muted-foreground">Last drill</dt>
                <dd>
                  #{engine.lastDrill.backupId} {engine.lastDrill.outcome === 'Failed' ? '✗' : '✓'}{' '}
                  {formatAge(engine.lastDrill.utc, now)}
                </dd>
              </>
            )}
          </>
        )}
      </dl>
    </div>
  );
}
