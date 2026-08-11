import { useEffect, useState } from 'react';
import { FolderOpen, ShieldCheck, X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import FileBrowser from '@/shell/components/FileBrowser';
import { useSessionStore } from '@/stores/useSessionStore';
import { useIntegrityStore, integrityStage } from '@/stores/useIntegrityStore';
import { useIntegrityScan } from '@/hooks/integrity/useIntegrityActions';
import { revealPageInDbMap } from '@/shell/commands/openDbMap';
import { DEPTH_BLURB, SCAN_DEPTHS, type Finding, type ScanDepth } from './integrityModel';
import VerdictBanner from './VerdictBanner';
import ScanTotals from './ScanTotals';
import FindingsTable from './FindingsTable';
import LimitsBlock from './LimitsBlock';
import RepairFlow from './RepairFlow';
import RepairFooter from './RepairFooter';

/**
 * Props are deliberately looser than `IDockviewPanelProps`.
 *
 * The view has two homes — a dock panel when a session exists, and the main area when one does not (see
 * {@link useIntegrityStore.standaloneOpen}) — so it cannot require the dockview panel context. dockview's
 * props structurally satisfy this, so the same component serves both without a wrapper or a branch.
 */
interface IntegrityPanelProps {
  params?: { path?: string };
}

/**
 * The Integrity view — scan a database, read the verdict, plan and apply a repair.
 *
 * **Path-scoped, not session-scoped**, and that is the whole reason it is a view of its own rather than a
 * tab inside Storage Health. Every other storage surface introspects the live engine of an open session.
 * This one must work on a database that will not open — the case that most justifies the feature — and on
 * one that is locked by another process, and it must survive the session being closed so a repair can take
 * the exclusive access it requires. A session-keyed host could do none of those.
 */
export default function IntegrityPanel(props: IntegrityPanelProps) {
  const sessionPath = useSessionStore((s) => s.filePath);
  const sessionId = useSessionStore((s) => s.sessionId);

  const path = useIntegrityStore((s) => s.path);
  const depth = useIntegrityStore((s) => s.depth);
  const report = useIntegrityStore((s) => s.report);
  const plan = useIntegrityStore((s) => s.plan);
  const outcome = useIntegrityStore((s) => s.outcome);
  const setPath = useIntegrityStore((s) => s.setPath);
  const setDepth = useIntegrityStore((s) => s.setDepth);

  const scan = useIntegrityScan();
  const [browseOpen, setBrowseOpen] = useState(false);
  const [draft, setDraft] = useState(path);

  // Panel params carry a path when opened from a deep link or a "check this database" handoff. The open
  // session is the fallback seed — convenient, and it makes the common case one click.
  const paramPath = props.params?.path;
  useEffect(() => {
    const seed = paramPath ?? (path || sessionPath || '');
    if (seed && seed !== path) {
      setPath(seed);
      setDraft(seed);
    } else if (!draft && seed) {
      setDraft(seed);
    }
    // Seeding is a mount/param concern; re-running it on every keystroke would fight the input.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [paramPath, sessionPath]);

  const effectivePath = draft.trim().replace(/^"(.*)"$/, '$1');
  const canScan = effectivePath.length > 0 && !scan.isPending;
  const stage = integrityStage({ report, plan, outcome });

  const runScan = () => {
    if (!canScan) {
      return;
    }
    if (effectivePath !== path) {
      setPath(effectivePath);
    }
    scan.mutate({ path: effectivePath, depth });
  };

  // The File Map needs a live engine, so revealing is only offered when the open session is looking at the
  // same bundle this report describes. Comparison is case-insensitive because Windows paths reach us with
  // inconsistent casing depending on whether they came from a picker, a deep link, or the session.
  const sameAsSession =
    !!sessionId && !!sessionPath && sessionPath.toLowerCase() === (report?.source ?? path).toLowerCase();
  const onReveal = sameAsSession ? (f: Finding) => revealPageInDbMap(f.locus.filePageIndex) : undefined;

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background" data-testid="integrity">
      {/* Target + depth + run */}
      <div className="wb-pane-header flex shrink-0 flex-col gap-1.5 border-b border-border px-3 py-2">
        <div className="flex items-center gap-2">
          <Input
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && runScan()}
            placeholder="C:\path\to\database.typhon"
            spellCheck={false}
            autoComplete="off"
            data-testid="integrity-path"
            className="h-7 flex-1 font-mono text-fs-base"
          />
          <Button
            variant="outline"
            onClick={() => setBrowseOpen(true)}
            title="Browse for a .typhon bundle"
            data-testid="integrity-browse"
            className="h-7 gap-1 px-2 text-fs-base"
          >
            <FolderOpen className="h-3.5 w-3.5" />
            Browse
          </Button>
          {scan.isPending ? (
            <Button
              variant="outline"
              onClick={() => scan.cancel()}
              data-testid="integrity-cancel"
              className="h-7 gap-1 px-2 text-fs-base"
            >
              <X className="h-3.5 w-3.5" />
              Cancel
            </Button>
          ) : (
            <Button onClick={runScan} disabled={!canScan} data-testid="integrity-scan" className="h-7 gap-1 px-3 text-fs-base">
              <ShieldCheck className="h-3.5 w-3.5" />
              Scan
            </Button>
          )}
        </div>

        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <span className="text-fs-sm text-muted-foreground">Depth</span>
          <div className="flex overflow-hidden rounded border border-border" role="radiogroup" aria-label="Scan depth">
            {SCAN_DEPTHS.map((d) => (
              <button
                key={d}
                type="button"
                role="radio"
                aria-checked={depth === d}
                onClick={() => setDepth(d)}
                data-testid={`integrity-depth-${d}`}
                className={`px-2 py-0.5 text-fs-sm capitalize ${
                  depth === d ? 'bg-accent text-foreground' : 'text-muted-foreground hover:bg-muted/60'
                }`}
              >
                {d}
              </button>
            ))}
          </div>
          <span className="text-fs-sm text-muted-foreground">{DEPTH_BLURB[depth as ScanDepth]}</span>
        </div>
      </div>

      {/* Report */}
      {scan.isError && (
        <div className="shrink-0 border-b border-border bg-destructive/10 px-3 py-2 text-fs-base text-destructive" data-testid="integrity-error">
          {(scan.error as Error)?.message ?? 'Scan failed.'}
        </div>
      )}

      {scan.isPending && (
        <div className="flex flex-1 items-center justify-center" data-testid="integrity-scanning">
          <p className="text-fs-base text-muted-foreground">Scanning…</p>
        </div>
      )}

      {!scan.isPending && !report && (
        <div className="flex flex-1 items-center justify-center p-4">
          <p className="max-w-md text-center text-fs-base text-muted-foreground">
            Point this at a <span className="font-mono">.typhon</span> bundle and scan it. Reading is always safe — it
            opens the file directly, without starting the engine, so it works on a database that will not open and on
            one another process is using.
          </p>
        </div>
      )}

      {!scan.isPending && report && (
        <>
          <VerdictBanner report={report} />
          <ScanTotals totals={report.totals} />
          {stage === 'scanned' ? (
            <>
              <FindingsTable findings={report.findings} onReveal={onReveal} />
              {/* Immediately above the action bar: the last thing read before deciding to act, never
                  something scrolled past on the way to a button. The repair stages render their own copy
                  in the same position — the block is passed down rather than stacked after the flow, which
                  would leave it *below* the Repair button and defeat the point. */}
              <LimitsBlock limits={report.limits} />
              <RepairFooter report={report} />
            </>
          ) : (
            <RepairFlow limits={report.limits} />
          )}
        </>
      )}

      <Dialog open={browseOpen} onOpenChange={setBrowseOpen}>
        <DialogContent className="flex h-[70vh] max-w-3xl flex-col">
          <DialogHeader>
            <DialogTitle>Select a database bundle</DialogTitle>
          </DialogHeader>
          <div className="min-h-0 flex-1">
            <FileBrowser
              extensionFilter={['.typhon']}
              recentKind="db"
              onSelectionChange={(paths) => paths[0] && setDraft(paths[0])}
              onActivate={(p) => {
                setDraft(p);
                setBrowseOpen(false);
              }}
            />
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
