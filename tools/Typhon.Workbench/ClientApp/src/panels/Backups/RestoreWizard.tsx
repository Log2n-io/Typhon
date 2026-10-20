import { useState } from 'react';
import { FolderOpen } from 'lucide-react';
import { deleteApiSessionsId } from '@/api/generated/sessions/sessions';
import type { BackupLogDto, BackupTimeResolutionDto } from '@/api/generated/model';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import FileBrowser from '@/shell/components/FileBrowser';
import { useBackupActions } from '@/hooks/backups/useBackupActions';
import { useBackupsStore, type PointTab } from '@/stores/useBackupsStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { cn } from '@/lib/utils';
import { formatBytes, localInputToUtc, lostAfterText, type ArchiveTimeline, type Listing } from './backupsModel';
import { canEnter, canRun, restoreRequestOf } from './restoreWizardModel';

const TABS: readonly { id: PointTab; label: string }[] = [
  { id: 'point', label: 'Point' },
  { id: 'time', label: 'Time' },
  { id: 'lsn', label: 'LSN' },
];

interface RestoreWizardProps {
  store: string;
  bundle: string;
  listing: Listing | undefined;
  timelines: ArchiveTimeline[];
  busy: boolean;
}

/**
 * The restore wizard (#1417 §4.4), in the panel rather than a modal (a long chain list in a scrolling dialog is the anti-pattern the Integrity view
 * names): point / time / LSN → target → preview → run. The preview is the engine's own plan (every refusal before anything is written, what is lost after
 * the restored LSN); the run carries its fingerprint and the engine refuses, writing nothing, if what it would restore has changed. Any input change drops
 * the preview. The run and its result live in the job card, which offers "Open restored database" once the restored bundle has settled.
 */
export default function RestoreWizard({ store, bundle, listing, timelines, busy }: RestoreWizardProps) {
  const draft = useBackupsStore((s) => s.restore);
  const plan = useBackupsStore((s) => s.restorePlan);
  const update = useBackupsStore((s) => s.updateRestore);
  const close = useBackupsStore((s) => s.closeWizard);
  const { planRestore, restore, resolveTime, readLog } = useBackupActions();
  const sessionId = useSessionStore((s) => s.sessionId);
  const clearSession = useSessionStore((s) => s.clearSession);
  const [showUnrestorable, setShowUnrestorable] = useState(false);
  const [browseOpen, setBrowseOpen] = useState(false);
  const [browsePath, setBrowsePath] = useState('');
  const [resolved, setResolved] = useState<BackupTimeResolutionDto | null>(null);
  const [log, setLog] = useState<BackupLogDto | null>(null);

  const points = listing?.points ?? [];
  const ids = [...new Set(points.map((p) => p.backupId))].sort((a, b) => b - a);
  const restorable = (id: number) => listing?.restorable == null || listing.restorable.includes(id);
  const offered = ids.filter((id) => showUnrestorable || restorable(id));

  const preview = () => {
    const request = restoreRequestOf(store, bundle, draft, null);
    if (request) {
      planRestore.mutate(request);
    }
  };

  const run = () => {
    const request = plan?.fingerprint ? restoreRequestOf(store, bundle, draft, plan.fingerprint) : null;
    if (request) {
      restore.mutate(request, { onSuccess: () => close() });
    }
  };

  const closeHolder = async (id: string) => {
    try {
      await deleteApiSessionsId(id);
    } catch {
      /* gone either way */
    }
    if (id === sessionId) {
      clearSession();
    }
    preview();
  };

  return (
    <div className="shrink-0 border-b border-border px-3 py-2" data-testid="backups-restore-wizard">
      <div className="mb-1 flex items-center gap-2">
        <span className="text-fs-base font-medium">Restore</span>
        <span className="text-fs-sm text-muted-foreground">Nothing is written until you run the reviewed plan.</span>
        <Button variant="ghost" onClick={close} className="ml-auto h-6 px-1.5 text-fs-sm">
          Close
        </Button>
      </div>

      {/* 1 · What */}
      <div className="flex items-center gap-1">
        {TABS.map((t) => (
          <button
            key={t.id}
            type="button"
            onClick={() => update({ tab: t.id })}
            aria-pressed={draft.tab === t.id}
            data-testid={`backups-restore-tab-${t.id}`}
            className={cn(
              'rounded px-2 py-0.5 text-fs-sm',
              draft.tab === t.id ? 'bg-accent text-foreground' : 'text-muted-foreground hover:bg-muted/60',
            )}
          >
            {t.label}
          </button>
        ))}
        {draft.tab !== 'point' && timelines.length > 0 && (
          <select
            value={draft.timelineId}
            onChange={(e) => update({ timelineId: e.target.value })}
            className="ml-2 h-7 rounded border border-border bg-background px-1 text-fs-sm"
            data-testid="backups-restore-timeline"
          >
            <option value="">newest timeline</option>
            {timelines.map((t) => (
              <option key={t.timelineId} value={t.timelineId}>
                {t.timelineId.slice(0, 8)} · {t.reason}
              </option>
            ))}
          </select>
        )}
      </div>

      {draft.tab === 'point' && (
        <div className="mt-1">
          <div className="flex flex-wrap gap-1">
            {offered.map((id) => (
              <button
                key={id}
                type="button"
                onClick={() => update({ pointId: id })}
                aria-pressed={draft.pointId === id}
                title={restorable(id) ? undefined : 'not restorable by a quick verify'}
                data-testid={`backups-restore-point-${id}`}
                className={cn(
                  'rounded border px-1.5 py-0.5 font-mono text-fs-xs',
                  draft.pointId === id ? 'border-ring bg-accent' : 'border-border',
                  !restorable(id) && 'text-red-600',
                )}
              >
                #{id}
              </button>
            ))}
          </div>
          {!showUnrestorable && ids.length > offered.length && (
            <button
              type="button"
              onClick={() => setShowUnrestorable(true)}
              className="mt-1 text-fs-xs text-muted-foreground underline"
            >
              show the {ids.length - offered.length} point(s) that cannot be restored, and why
            </button>
          )}
          {showUnrestorable && (
            <p className="mt-1 text-fs-xs text-muted-foreground">
              Red points do not resolve or verify; a restore of one is refused.
            </p>
          )}
        </div>
      )}

      {draft.tab === 'time' && (
        <div className="mt-1 flex flex-wrap items-center gap-2">
          <Input
            type="datetime-local"
            step={1}
            value={draft.timeLocal}
            onChange={(e) => {
              update({ timeLocal: e.target.value });
              setResolved(null);
            }}
            className="h-7 w-56 text-fs-sm"
            data-testid="backups-restore-time"
          />
          <Button
            variant="outline"
            disabled={!localInputToUtc(draft.timeLocal) || resolveTime.isPending}
            onClick={() =>
              resolveTime.mutate(
                { store, timeUtc: localInputToUtc(draft.timeLocal), timelineId: draft.timelineId },
                { onSuccess: setResolved },
              )
            }
            className="h-7 px-2 text-fs-sm"
          >
            Resolve
          </Button>
          {resolved && (
            <span className="text-fs-sm" data-testid="backups-restore-resolved">
              → LSN {Number(resolved.lsn).toLocaleString()}, sampled {new Date(resolved.sampleUtc).toLocaleString()}
            </span>
          )}
          {resolveTime.isError && (
            <span className="text-fs-sm text-destructive">{(resolveTime.error as Error).message}</span>
          )}
          <p className="w-full text-fs-xs text-muted-foreground">
            Your local time, sent as UTC. Commits up to one sample interval before it may be excluded, never one after
            (06 §3).
          </p>
        </div>
      )}

      {draft.tab === 'lsn' && (
        <div className="mt-1 flex flex-wrap items-center gap-2">
          <Input
            value={draft.lsn}
            onChange={(e) => update({ lsn: e.target.value })}
            placeholder="LSN"
            className="h-7 w-40 font-mono text-fs-sm"
            data-testid="backups-restore-lsn"
          />
          <span className="text-fs-sm text-muted-foreground">commits around</span>
          <Input
            type="datetime-local"
            step={1}
            value={draft.timeLocal}
            onChange={(e) => update({ timeLocal: e.target.value })}
            className="h-7 w-56 text-fs-sm"
          />
          <Button
            variant="outline"
            disabled={!localInputToUtc(draft.timeLocal) || readLog.isPending}
            onClick={() => {
              const at = Date.parse(localInputToUtc(draft.timeLocal));
              readLog.mutate(
                {
                  store,
                  timelineId: draft.timelineId,
                  fromUtc: new Date(at - 5 * 60_000).toISOString(),
                  toUtc: new Date(at + 5 * 60_000).toISOString(),
                },
                { onSuccess: setLog },
              );
            }}
            className="h-7 px-2 text-fs-sm"
          >
            List commits (±5 min)
          </Button>
          {readLog.isError && <span className="text-fs-sm text-destructive">{(readLog.error as Error).message}</span>}
          {log && (
            <ul className="max-h-40 w-full overflow-auto font-mono text-fs-xs" data-testid="backups-restore-log">
              {(log.frames ?? []).length === 0 && <li className="text-muted-foreground">No frame in that range.</li>}
              {(log.frames ?? []).map((f) => (
                <li key={String(f.lastLsn)}>
                  <button type="button" onClick={() => update({ lsn: String(f.lastLsn) })} className="hover:underline">
                    LSN {Number(f.firstLsn).toLocaleString()}..{Number(f.lastLsn).toLocaleString()} · {f.kind} ·{' '}
                    {Number(f.records)} record(s)
                    {f.approxUtc ? ` · ≈ ${new Date(f.approxUtc).toLocaleTimeString()}` : ''}
                  </button>
                </li>
              ))}
              {log.truncated && <li className="text-muted-foreground">… more; narrow the range.</li>}
            </ul>
          )}
        </div>
      )}

      {/* 2 · Where */}
      {canEnter('target', draft, plan) && (
        <div className="mt-2 flex flex-wrap items-center gap-2">
          <span className="text-fs-sm text-muted-foreground">Into</span>
          <Input
            value={draft.target}
            onChange={(e) => update({ target: e.target.value })}
            placeholder="a directory, or {name}.typhon carrying the database's own name"
            spellCheck={false}
            className="h-7 min-w-[18rem] flex-1 font-mono text-fs-sm"
            data-testid="backups-restore-target"
          />
          <Button variant="outline" onClick={() => setBrowseOpen(true)} className="h-7 gap-1 px-2 text-fs-sm">
            <FolderOpen className="h-3.5 w-3.5" />
            Browse
          </Button>
          <label
            className="flex items-center gap-1 text-fs-sm"
            title="An existing database there is moved aside to {name}.typhon.pre-restore-{utc}, never deleted."
          >
            <input
              type="checkbox"
              checked={draft.replace}
              onChange={(e) => update({ replace: e.target.checked })}
              data-testid="backups-restore-replace"
            />
            Replace an existing database (moved aside)
          </label>
          <details className="text-fs-sm">
            <summary className="cursor-pointer text-muted-foreground">Advanced</summary>
            <label
              className="flex items-center gap-1"
              title="Forensics only: write pages that fail their check and list them. The open will typically refuse such a database."
            >
              <input
                type="checkbox"
                checked={draft.allowDamaged}
                onChange={(e) => update({ allowDamaged: e.target.checked })}
              />
              Allow damaged pages (forensics)
            </label>
            <label
              className="flex items-center gap-1"
              title="Restore an image of another on-disk format revision, for another build's environment."
            >
              <input
                type="checkbox"
                checked={draft.allowFormatMismatch}
                onChange={(e) => update({ allowFormatMismatch: e.target.checked })}
              />
              Allow another format revision
            </label>
          </details>
        </div>
      )}

      {/* 3 · Preview */}
      {canEnter('preview', draft, plan) && (
        <div className="mt-2 flex items-center gap-2">
          <Button
            variant="outline"
            disabled={planRestore.isPending}
            onClick={preview}
            className="h-7 px-2 text-fs-sm"
            data-testid="backups-restore-preview"
          >
            Preview
          </Button>
          {planRestore.isError && (
            <span className="text-fs-sm text-destructive">{(planRestore.error as Error).message}</span>
          )}
        </div>
      )}

      {plan && (
        <div className="mt-2 text-fs-sm" data-testid="backups-restore-plan">
          {plan.canRestore ? (
            <>
              <div>
                Into <span className="font-mono">{plan.resolvedTarget}</span> · reads #{plan.chain.join(' → #')} (
                {formatBytes(plan.bytesToRead)})
                {plan.archivedSegments > 0 &&
                  ` + ${plan.archivedSegments} archived segment(s) (${formatBytes(plan.archivedBytes)})`}
              </div>
              <div>
                Restores through LSN <b>{plan.pointLsn.toLocaleString()}</b>
                {plan.targetLsn > 0 &&
                  plan.targetLsn !== plan.pointLsn &&
                  ` (the last transaction ending at or below ${plan.targetLsn.toLocaleString()})`}
                {plan.sampleUtc && `, sampled ${new Date(plan.sampleUtc).toLocaleString()}`}
              </div>
              {plan.displacedBundle && (
                <div className="text-amber-600">
                  The database there moves aside to <span className="font-mono">{plan.displacedBundle}</span>.
                </div>
              )}
              <div className="mt-0.5" data-testid="backups-restore-lost">
                {lostAfterText(plan)}
              </div>
              {canRun(plan) && (
                <Button
                  disabled={busy || restore.isPending}
                  onClick={run}
                  className="mt-1 h-7 px-3 text-fs-sm"
                  data-testid="backups-restore-run"
                >
                  Restore
                </Button>
              )}
              {restore.isError && <div className="text-destructive">{(restore.error as Error).message}</div>}
            </>
          ) : (
            <div className="text-destructive" data-testid="backups-restore-refused">
              Refused{plan.refusal ? ` (${plan.refusal})` : plan.unreachable ? ` (${plan.unreachable})` : ''}:{' '}
              {plan.refusalMessage}
              {plan.heldBySessionId && (
                <Button
                  variant="outline"
                  onClick={() => closeHolder(plan.heldBySessionId as string)}
                  className="ml-2 h-6 px-2 text-fs-sm"
                >
                  Close that session
                </Button>
              )}
              {plan.nearestRestorable.length > 0 && (
                <div className="mt-1 text-foreground">
                  Restorable instead:
                  {plan.nearestRestorable.map((lsn) => (
                    <Button
                      key={lsn}
                      variant="outline"
                      onClick={() => update({ tab: 'lsn', lsn: String(lsn) })}
                      className="ml-1 h-6 px-1.5 font-mono text-fs-xs"
                    >
                      LSN {lsn.toLocaleString()}
                    </Button>
                  ))}
                </div>
              )}
            </div>
          )}
        </div>
      )}

      <Dialog open={browseOpen} onOpenChange={setBrowseOpen}>
        <DialogContent className="flex h-[70vh] max-w-3xl flex-col">
          <DialogHeader>
            <DialogTitle>Restore into…</DialogTitle>
          </DialogHeader>
          <div className="min-h-0 flex-1">
            <FileBrowser
              extensionFilter={['.typhon']}
              recentKind="db"
              onPathChange={setBrowsePath}
              onActivate={(p) => {
                update({ target: p });
                setBrowseOpen(false);
              }}
            />
          </div>
          <div className="flex shrink-0 justify-end">
            <Button
              disabled={!browsePath}
              onClick={() => {
                update({ target: browsePath });
                setBrowseOpen(false);
              }}
              className="h-7 px-3 text-fs-sm"
            >
              Restore into this folder
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
