import { useEffect, useRef, useState } from 'react';
import { FolderOpen, RefreshCw } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import ViewState from '@/components/ViewState';
import FileBrowser from '@/shell/components/FileBrowser';
import { useOpenDatabaseFile } from '@/hooks/useOpenDatabaseFile';
import { useBackupActions } from '@/hooks/backups/useBackupActions';
import { useBackupJob } from '@/hooks/backups/useBackupJob';
import {
  useBackupArchive,
  useBackupJobs,
  useBackupListing,
  useBackupStatus,
  useInvalidateBackups,
} from '@/hooks/backups/useBackupQueries';
import { scratchFor, useBackupsStore } from '@/stores/useBackupsStore';
import { useSessionStore } from '@/stores/useSessionStore';
import type { BackupPointRefDto } from '@/api/generated/model';
import { isTerminal } from './backupsModel';
import HealthCard from './HealthCard';
import ActionsBar from './ActionsBar';
import ChainTimeline from './ChainTimeline';
import ArchiveCoverageBar from './ArchiveCoverageBar';
import PointDetail from './PointDetail';
import JobCard from './JobCard';
import RetentionFlow from './RetentionFlow';
import RestoreWizard from './RestoreWizard';

/** Looser than `IDockviewPanelProps`: the view has two homes (dock panel, and full-bleed with no session), as the Integrity view does. */
interface BackupsPanelProps {
  params?: { path?: string };
}

const unquote = (p: string) => p.trim().replace(/^"(.*)"$/, '$1');

const byId = (id: number): BackupPointRefDto => ({ kind: 'id', id, lsn: null, timeUtc: null, timelineId: null });

/**
 * The Backups view (#1417, 07 §5) — a store's health, its points and archive, and what can be done with them: back up now, verify, drill, compact,
 * retention, restore.
 *
 * **Path-scoped, not session-scoped**, like the Integrity view: a store is useful with no session at all — restoring a database that will not open is
 * the case that most justifies the view. The bundle is seeded from an Open session (an Attach session names an endpoint, not a file: store-only use);
 * the store is remembered per bundle. Everything long is a server-side job that survives this tab: reopening the view re-attaches to a running job and
 * shows a finished one's result.
 */
export default function BackupsPanel(props: BackupsPanelProps) {
  const kind = useSessionStore((s) => s.kind);
  const sessionPath = useSessionStore((s) => s.filePath);
  const bundle = useBackupsStore((s) => s.bundlePath);
  const store = useBackupsStore((s) => s.storePath);
  const setBundle = useBackupsStore((s) => s.setBundle);
  const setStore = useBackupsStore((s) => s.setStore);
  const selectedPointId = useBackupsStore((s) => s.selectedPointId);
  const selectPoint = useBackupsStore((s) => s.selectPoint);
  const verifyLevel = useBackupsStore((s) => s.verifyLevel);
  const setVerifyLevel = useBackupsStore((s) => s.setVerifyLevel);
  const activeJobId = useBackupsStore((s) => s.activeJobId);
  const setActiveJob = useBackupsStore((s) => s.setActiveJob);
  const scratchByStore = useBackupsStore((s) => s.scratchByStore);
  const setScratch = useBackupsStore((s) => s.setScratch);
  const wizardOpen = useBackupsStore((s) => s.wizardOpen);
  const openWizard = useBackupsStore((s) => s.openWizard);
  const retentionOpen = useBackupsStore((s) => s.retentionOpen);
  const openRetention = useBackupsStore((s) => s.openRetention);

  const [bundleDraft, setBundleDraft] = useState(bundle);
  const [storeDraft, setStoreDraft] = useState(store);
  const [browse, setBrowse] = useState<'bundle' | 'store' | null>(null);
  const [browsePath, setBrowsePath] = useState('');
  const dismissed = useRef(new Set<string>());

  // Seed the bundle from a deep link or the Open session; an Attach session's "file path" is an endpoint and seeds nothing.
  const paramPath = props.params?.path;
  useEffect(() => {
    const seed = paramPath ?? (bundle || (kind === 'open' ? sessionPath : '') || '');
    if (seed && seed !== bundle) {
      setBundle(seed);
      setBundleDraft(seed);
    }
    // Seeding is a mount/param concern: re-running it on every keystroke would fight the input.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [paramPath, sessionPath, kind]);

  // The fields follow the store's paths, whoever set them: a bundle change can bring back its remembered store, and openBackups(path) (the Integrity
  // handoff) sets the bundle of a panel already docked. A field left showing the old path would commit it back on its next blur.
  useEffect(() => setBundleDraft(bundle), [bundle]);
  useEffect(() => setStoreDraft(store), [store]);

  const { job, lost } = useBackupJob(activeJobId);
  const jobRunning = !!job && !isTerminal(job.state);
  const status = useBackupStatus(store, bundle, jobRunning);
  const isStore = !!status.data?.storeStatus;
  const listing = useBackupListing(store, isStore);
  const archive = useBackupArchive(store, isStore);
  const jobs = useBackupJobs(store);
  const invalidate = useInvalidateBackups();
  const actions = useBackupActions();
  const { openDatabaseFile } = useOpenDatabaseFile();

  // Re-attach (§3.5): a running job of this store is followed; else its newest finished one is shown until dismissed.
  useEffect(() => {
    if (activeJobId || !jobs.data) {
      return;
    }
    const running = jobs.data.find((j) => !isTerminal(j.state));
    const newest = jobs.data[0];
    const pick = running ?? (newest && !dismissed.current.has(newest.jobId) ? newest : undefined);
    if (pick) {
      setActiveJob(pick.jobId);
    }
  }, [jobs.data, activeJobId, setActiveJob]);

  const busy = jobRunning || !!status.data?.runningJobId;
  // A changed path changes the queries' keys, which read it; an unchanged one re-reads nothing (the Refresh button does).
  const commitPaths = () => {
    const b = unquote(bundleDraft);
    const s = unquote(storeDraft);
    // The store field counts only if the operator edited it. A new bundle brings back its remembered store (PC-1): the field's text, still the old
    // store, must not revert it — nor overwrite the new bundle's memory with it.
    const storeEdited = s !== store;
    if (b !== bundle) {
      setBundle(b);
    }
    if (s && storeEdited && s !== useBackupsStore.getState().storePath) {
      setStore(s);
    }
  };

  const selected =
    listing.data?.points.find((p) => p.backupId === selectedPointId && p.kind !== 'Incremental') ??
    listing.data?.points.find((p) => p.backupId === selectedPointId);
  const scratch = scratchFor(store, scratchByStore, status.data?.defaultScratch ?? null);

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background" data-testid="backups">
      {/* Target */}
      <div className="wb-pane-header flex shrink-0 flex-col gap-1.5 border-b border-border px-3 py-2">
        <div className="flex items-center gap-2">
          <span className="w-14 shrink-0 text-fs-sm text-muted-foreground">Database</span>
          <Input
            value={bundleDraft}
            onChange={(e) => setBundleDraft(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && commitPaths()}
            onBlur={commitPaths}
            placeholder="C:\path\to\database.typhon (optional: a store alone can be verified and restored)"
            spellCheck={false}
            autoComplete="off"
            data-testid="backups-bundle"
            className="h-7 flex-1 font-mono text-fs-base"
          />
          <Button
            variant="outline"
            onClick={() => setBrowse('bundle')}
            title="Browse for a .typhon bundle"
            className="h-7 gap-1 px-2 text-fs-base"
          >
            <FolderOpen className="h-3.5 w-3.5" />
          </Button>
        </div>
        <div className="flex items-center gap-2">
          <span className="w-14 shrink-0 text-fs-sm text-muted-foreground">Store</span>
          <Input
            value={storeDraft}
            onChange={(e) => setStoreDraft(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && commitPaths()}
            onBlur={commitPaths}
            placeholder="D:\backups\database (the backup store directory)"
            spellCheck={false}
            autoComplete="off"
            data-testid="backups-store"
            className="h-7 flex-1 font-mono text-fs-base"
          />
          <Button
            variant="outline"
            onClick={() => setBrowse('store')}
            title="Browse for the store directory"
            className="h-7 gap-1 px-2 text-fs-base"
          >
            <FolderOpen className="h-3.5 w-3.5" />
          </Button>
          <Button
            variant="outline"
            onClick={() => void invalidate()}
            title="Refresh"
            disabled={!store}
            className="h-7 px-2"
            data-testid="backups-refresh"
          >
            <RefreshCw className="h-3.5 w-3.5" />
          </Button>
        </div>
      </div>

      <div className="min-h-0 flex-1 overflow-auto">
        <ViewState
          phase={!store ? 'no-selection' : status.isLoading ? 'loading' : status.isError ? 'error' : 'ready'}
          error={status.error}
          onRetry={() => void status.refetch()}
          noSelection={
            <div className="flex h-full items-center justify-center p-4">
              <p className="max-w-md text-center text-fs-base text-muted-foreground" data-testid="backups-no-store">
                Name the backup store — the directory a database’s points are kept in. With the database’s bundle too,
                this view backs it up, by the route that reaches it: through this Workbench’s session, by asking the
                application holding it, or from the closed file.
              </p>
            </div>
          }
        >
          {status.data && (
            <>
              <HealthCard status={status.data} />
              <ActionsBar
                status={status.data}
                busy={busy}
                verifyLevel={verifyLevel}
                onVerifyLevel={setVerifyLevel}
                onBackUp={(full) => actions.create.mutate({ store, bundle, full })}
                onVerify={() => actions.verify.mutate({ store, point: null, level: verifyLevel })}
                onRetention={openRetention}
                onRestore={() => openWizard(selectedPointId)}
              />
              {[actions.create, actions.verify, actions.drill, actions.compact].map((m, i) =>
                m.isError ? (
                  <div
                    key={i}
                    className="shrink-0 border-b border-border bg-destructive/10 px-3 py-1.5 text-fs-sm text-destructive"
                    data-testid="backups-action-error"
                  >
                    {(m.error as Error).message}
                  </div>
                ) : null,
              )}
              <JobCard
                job={job}
                lost={lost}
                onCancel={(id) => actions.cancel.mutate(id)}
                onDismiss={() => {
                  if (activeJobId) {
                    dismissed.current.add(activeJobId);
                  }
                  setActiveJob(null);
                }}
                onOpenRestored={(target) => void openDatabaseFile(target, [])}
              />
              {retentionOpen && <RetentionFlow store={store} busy={busy} />}
              {wizardOpen && (
                <RestoreWizard
                  store={store}
                  bundle={bundle}
                  listing={listing.data}
                  timelines={archive.data ?? []}
                  busy={busy}
                />
              )}
              {listing.isError && (
                <div className="shrink-0 border-b border-border px-3 py-1.5 text-fs-sm text-destructive">
                  {(listing.error as Error).message}
                </div>
              )}
              {listing.data && (
                <ChainTimeline
                  listing={listing.data}
                  selectedId={selectedPointId}
                  onSelect={(id) => selectPoint(id === selectedPointId ? null : id)}
                />
              )}
              {archive.data && listing.data && (
                <ArchiveCoverageBar timelines={archive.data} points={listing.data.points} />
              )}
              {selected && (
                <PointDetail
                  point={selected}
                  restorable={
                    listing.data?.restorable == null ? null : listing.data.restorable.includes(selected.backupId)
                  }
                  drill={listing.data?.drills.get(selected.backupId)}
                  scratch={scratch}
                  busy={busy}
                  onScratchChange={(s) => setScratch(store, s)}
                  onVerify={() => actions.verify.mutate({ store, point: byId(selected.backupId), level: verifyLevel })}
                  onDrill={() => actions.drill.mutate({ store, point: byId(selected.backupId), scratch, bundle })}
                  onCompact={() => actions.compact.mutate({ store, point: byId(selected.backupId) })}
                  onRestore={() => openWizard(selected.backupId)}
                />
              )}
            </>
          )}
        </ViewState>
      </div>

      <Dialog open={browse !== null} onOpenChange={(open) => !open && setBrowse(null)}>
        <DialogContent className="flex h-[70vh] max-w-3xl flex-col">
          <DialogHeader>
            <DialogTitle>
              {browse === 'bundle' ? 'Select a database bundle' : 'Select the backup store directory'}
            </DialogTitle>
          </DialogHeader>
          <div className="min-h-0 flex-1">
            <FileBrowser
              extensionFilter={browse === 'bundle' ? ['.typhon'] : ['store.json']}
              recentKind={browse === 'bundle' ? 'db' : undefined}
              onPathChange={setBrowsePath}
              onActivate={(p) => {
                if (browse === 'bundle') {
                  setBundleDraft(p);
                  setBundle(p);
                } else {
                  const dir = p.replace(/[\\/]store\.json$/i, '');
                  setStoreDraft(dir);
                  setStore(dir);
                }
                setBrowse(null);
              }}
            />
          </div>
          {browse === 'store' && (
            <div className="flex shrink-0 justify-end">
              <Button
                disabled={!browsePath}
                onClick={() => {
                  setStoreDraft(browsePath);
                  setStore(browsePath);
                  setBrowse(null);
                }}
                className="h-7 px-3 text-fs-sm"
              >
                Use this folder
              </Button>
            </div>
          )}
        </DialogContent>
      </Dialog>
    </div>
  );
}
