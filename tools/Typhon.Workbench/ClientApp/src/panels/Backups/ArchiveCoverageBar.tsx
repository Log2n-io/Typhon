import { cn } from '@/lib/utils';
import { coverageOf, type ArchiveTimeline, type Point } from './backupsModel';

/**
 * The Layer 2 bar (#1417 §2, AC-W2): per timeline, the archived LSN ranges, the gaps in red (lighter when a later point covers them), and a tick at
 * each point's P. A timeline restored from another says where it forked.
 */
export default function ArchiveCoverageBar({ timelines, points }: { timelines: ArchiveTimeline[]; points: Point[] }) {
  if (timelines.length === 0) {
    return null;
  }

  return (
    <div className="shrink-0 border-b border-border px-3 py-2" data-testid="backups-archive">
      <div className="mb-1 text-fs-sm font-medium text-foreground">WAL archive</div>
      <div className="flex flex-col gap-2">
        {timelines.map((t) => {
          const c = coverageOf(t, points);
          return (
            <div key={t.timelineId} data-testid="backups-archive-timeline">
              <div className="flex items-baseline gap-2 text-fs-xs text-muted-foreground">
                <span className="font-mono">timeline {t.timelineId.slice(0, 8)}</span>
                <span>{t.reason}</span>
                {t.parentTimelineId && (
                  <span>
                    from {t.parentTimelineId.slice(0, 8)} at LSN {t.originLsn.toLocaleString()}
                  </span>
                )}
                <span className="ml-auto">
                  archived through LSN {t.archivedThroughLsn.toLocaleString()} · {t.segments.length} segment(s)
                </span>
              </div>
              <div className="relative mt-0.5 h-3 w-full rounded bg-muted">
                {c.runs.map((r, i) => (
                  <div
                    key={i}
                    title={`${r.kind === 'gap' ? `gap${r.covered ? ' (covered by a later point)' : ''}` : 'archived'} LSN ${r.fromLsn.toLocaleString()}..${r.toLsn.toLocaleString()}`}
                    data-testid={r.kind === 'gap' ? 'backups-archive-gap' : undefined}
                    className={cn(
                      'absolute top-0 h-full',
                      r.kind === 'archived' ? 'bg-emerald-500/70' : r.covered ? 'bg-red-400/50' : 'bg-red-500',
                    )}
                    style={{ left: `${r.from * 100}%`, width: `${Math.max(0.5, (r.to - r.from) * 100)}%` }}
                  />
                ))}
                {c.ticks.map((tick) => (
                  <div
                    key={tick.backupId}
                    title={`#${tick.backupId} at P = LSN ${tick.lsn.toLocaleString()}`}
                    className="absolute -top-0.5 h-4 w-0.5 bg-foreground"
                    style={{ left: `${tick.at * 100}%` }}
                  />
                ))}
              </div>
              {t.gaps.some((g) => !g.covered) && (
                <p className="mt-0.5 text-fs-xs text-destructive">
                  An uncovered gap: nothing after it can be restored to an LSN or a time until the next point.
                </p>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}
