import { useState } from 'react';
import { AlertTriangle, ArrowLeft, FlaskConical, Lock, Wrench } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { StatusBadge } from '@/components/ui/status-badge';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { deleteApiSessionsId } from '@/api/generated/sessions/sessions';
import { useSessionStore } from '@/stores/useSessionStore';
import { useIntegrityStore } from '@/stores/useIntegrityStore';
import { useRepairApply } from '@/hooks/integrity/useIntegrityActions';
import type { Limits, RepairOutcome, RepairPlan } from './integrityModel';
import LimitsBlock from './LimitsBlock';
import LossManifest from './LossManifest';
import RepairReceipt from './RepairReceipt';

/**
 * Plan review → consent → apply, in the panel rather than a modal.
 *
 * A modal was the obvious shape and is the wrong one. The loss manifest can run to thousands of rows, and a
 * modal that scrolls internally trains the eye to reach past the content for the button underneath it. In
 * the panel the manifest occupies the view, and the action bar sits below the thing it is consenting to.
 */
export default function RepairFlow({ limits }: { limits: Limits }) {
  const path = useIntegrityStore((s) => s.path);
  const plan = useIntegrityStore((s) => s.plan);
  const outcome = useIntegrityStore((s) => s.outcome);
  const allowLoss = useIntegrityStore((s) => s.allowLoss);
  const backupFirst = useIntegrityStore((s) => s.backupFirst);
  const setAllowLoss = useIntegrityStore((s) => s.setAllowLoss);
  const setBackupFirst = useIntegrityStore((s) => s.setBackupFirst);
  const setPlan = useIntegrityStore((s) => s.setPlan);
  const setOutcome = useIntegrityStore((s) => s.setOutcome);

  const sessionId = useSessionStore((s) => s.sessionId);
  const sessionPath = useSessionStore((s) => s.filePath);
  const clearSession = useSessionStore((s) => s.clearSession);

  const apply = useRepairApply();
  // A rehearsal is kept out of the store's outcome slot on purpose: `outcome` means "this database was
  // repaired", and a dry run must never be able to render as that receipt.
  const [rehearsal, setRehearsal] = useState<RepairOutcome | null>(null);
  const [closing, setClosing] = useState(false);

  if (outcome) {
    return (
      <>
        <RepairReceipt outcome={outcome} />
        <LimitsBlock limits={limits} />
      </>
    );
  }
  if (!plan) {
    return null;
  }

  // Refusal replaces the flow rather than disabling its button. A greyed-out Apply beneath a full plan reads
  // as "consent harder" and invites hunting for the override; there is none, and the screen should not imply
  // one exists. The report itself stays reachable — diagnosis is exactly what still works here.
  if (plan.blockedReason) {
    return (
      <>
        <div className="flex shrink-0 items-center gap-2 border-b border-border px-3 py-1.5">
          <Button
            variant="ghost"
            onClick={() => setPlan(null)}
            data-testid="integrity-repair-back"
            className="h-6 gap-1 px-1.5 text-fs-sm"
          >
            <ArrowLeft className="h-3 w-3" />
            Back to report
          </Button>
          <span className="text-fs-base font-medium text-foreground">Repair refused</span>
        </div>
        <div className="min-h-0 flex-1 overflow-auto px-3 py-3" data-testid="integrity-repair-blocked">
          <div className="flex items-start gap-2">
            <Lock className="mt-0.5 h-4 w-4 shrink-0 text-rose-500" />
            <p className="select-text text-fs-base text-foreground">{plan.blockedReason}</p>
          </div>
        </div>
        <LimitsBlock limits={limits} />
      </>
    );
  }

  // The Workbench holds databases open; repair needs exclusive access. The server independently refuses
  // (409 on a held lock) — this is the same rule stated early enough that the operator isn't surprised
  // by it after reading a manifest.
  const sessionHoldsTarget =
    !!sessionId && !!sessionPath && sessionPath.toLowerCase() === path.toLowerCase();

  const blocked = sessionHoldsTarget || (plan.requiresLossyConsent && !allowLoss);

  const run = (dryRun: boolean) => {
    apply.mutate(
      { path, fingerprint: plan.fingerprint, allowLoss, backupFirst, dryRun },
      {
        onSuccess: (result) => (dryRun ? setRehearsal(result) : setOutcome(result)),
      },
    );
  };

  const closeSessionThenStay = async () => {
    if (!sessionId) {
      return;
    }
    setClosing(true);
    try {
      await deleteApiSessionsId(sessionId);
    } catch {
      /* the local clear must happen regardless — the session is gone from this client's perspective */
    } finally {
      clearSession();
      setClosing(false);
    }
  };

  return (
    <div className="flex min-h-0 flex-1 flex-col" data-testid="integrity-repair">
      <div className="flex shrink-0 items-center gap-2 border-b border-border px-3 py-1.5">
        <Button
          variant="ghost"
          onClick={() => setPlan(null)}
          data-testid="integrity-repair-back"
          className="h-6 gap-1 px-1.5 text-fs-sm"
        >
          <ArrowLeft className="h-3 w-3" />
          Back to report
        </Button>
        <span className="text-fs-base font-medium text-foreground">Repair plan</span>
        <span
          className="select-text font-mono text-fs-xs text-muted-foreground"
          title="Binds this plan to the exact database state it was built for. Applying refuses if the database moved."
        >
          {plan.fingerprint.slice(0, 16)}…
        </span>
      </div>

      <div className="min-h-0 flex-1 overflow-auto">
        <StepsTable plan={plan} />

        {plan.unaddressed.length > 0 && (
          <div className="border-t border-border px-3 py-2" data-testid="integrity-unaddressed">
            {/* Part of the diagnosis, not a footnote: what the tool cannot fix is what determines whether
                the operator should be reaching for a backup instead. */}
            <p className="text-fs-base font-medium text-foreground">This repair will not address</p>
            <ul className="mt-1 list-inside list-disc text-fs-sm text-muted-foreground">
              {plan.unaddressed.map((u) => (
                <li key={u}>{u}</li>
              ))}
            </ul>
          </div>
        )}

        <div className="flex min-h-0 flex-col border-t border-border">
          <LossManifest entries={plan.loss} />
        </div>

        {rehearsal && (
          <div className="border-t border-border" data-testid="integrity-rehearsal">
            <div className="flex items-center gap-2 bg-sky-500/10 px-3 py-1.5">
              <FlaskConical className="h-3.5 w-3.5 text-sky-500" />
              <span className="text-fs-base text-foreground">Dry run — nothing was written</span>
            </div>
            <RepairReceipt outcome={rehearsal} rehearsal />
          </div>
        )}
      </div>

      {/* What the scan could not see, immediately above the decision it bears on. */}
      <LimitsBlock limits={limits} />

      {/* Action bar — below the manifest, never floating above it */}
      <div className="shrink-0 border-t border-border px-3 py-2">
        {sessionHoldsTarget && (
          <div className="mb-2 flex items-center gap-2 rounded border border-amber-500/40 bg-amber-500/10 px-2 py-1.5" data-testid="integrity-lock-block">
            <Lock className="h-3.5 w-3.5 shrink-0 text-amber-500" />
            <span className="text-fs-base text-foreground">
              This database is open in the Workbench. Repair needs exclusive access.
            </span>
            <Button
              variant="outline"
              onClick={() => void closeSessionThenStay()}
              disabled={closing}
              data-testid="integrity-close-session"
              className="ml-auto h-6 px-2 text-fs-sm"
            >
              {closing ? 'Closing…' : 'Close the session'}
            </Button>
          </div>
        )}

        {plan.requiresLossyConsent && (
          <label
            className="mb-2 flex cursor-pointer items-start gap-2 rounded border border-destructive/40 bg-destructive/10 px-2 py-1.5"
            data-testid="integrity-consent"
          >
            <input
              type="checkbox"
              checked={allowLoss}
              onChange={(e) => setAllowLoss(e.target.checked)}
              data-testid="integrity-consent-checkbox"
              className="mt-0.5"
            />
            <span className="text-fs-base text-foreground">
              <AlertTriangle className="mr-1 inline h-3.5 w-3.5 text-destructive" />
              I have read the {plan.loss.length.toLocaleString()}-entry list above and accept losing what it names.
            </span>
          </label>
        )}

        <div className="flex flex-wrap items-center gap-2">
          <label className="flex cursor-pointer items-center gap-1.5 text-fs-base text-muted-foreground">
            <input
              type="checkbox"
              checked={backupFirst}
              onChange={(e) => setBackupFirst(e.target.checked)}
              data-testid="integrity-backup-first"
            />
            Copy the bundle before the first write
          </label>

          <div className="ml-auto flex gap-2">
            <Button
              variant="outline"
              onClick={() => run(true)}
              disabled={apply.isPending || sessionHoldsTarget}
              data-testid="integrity-dry-run"
              className="h-7 gap-1 px-2 text-fs-base"
              title="Execute every step in rehearsal and report what would happen. Writes nothing."
            >
              <FlaskConical className="h-3.5 w-3.5" />
              Dry run
            </Button>
            <Button
              onClick={() => run(false)}
              disabled={blocked || apply.isPending}
              data-testid="integrity-apply"
              className="h-7 gap-1 px-3 text-fs-base"
            >
              <Wrench className="h-3.5 w-3.5" />
              {apply.isPending ? 'Repairing…' : 'Repair'}
            </Button>
          </div>
        </div>

        {apply.isError && (
          <p className="mt-2 text-fs-base text-destructive" data-testid="integrity-apply-error">
            {(apply.error as Error)?.message ?? 'Repair failed.'}
          </p>
        )}
      </div>
    </div>
  );
}

function StepsTable({ plan }: { plan: RepairPlan }) {
  if (plan.steps.length === 0) {
    return <p className="px-3 py-2 text-fs-base text-muted-foreground">Nothing to do — this database needs no repair.</p>;
  }

  return (
    <Table className="text-fs-base">
      <TableHeader>
        <TableRow>
          <TableHead className="w-8 text-right text-fs-sm">#</TableHead>
          <TableHead className="text-fs-sm">Action</TableHead>
          <TableHead className="text-fs-sm">Class</TableHead>
          <TableHead className="text-fs-sm">What it does</TableHead>
          <TableHead className="text-fs-sm">Why</TableHead>
          <TableHead className="text-fs-sm">Addresses</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {plan.steps.map((s) => (
          <TableRow key={s.order} data-testid="integrity-step-row">
            {/* Order is a correctness constraint (RB-02), not a display preference — indexes rebuilt over
                un-scrubbed chains are confidently wrong rather than merely stale. Shown, never sortable. */}
            <TableCell className="text-right tabular-nums text-muted-foreground">{s.order}</TableCell>
            <TableCell className="font-mono text-fs-sm">{s.action}</TableCell>
            <TableCell>
              <StatusBadge tone={s.class === 'Excise' ? 'error' : s.class === 'Regenerate' ? 'success' : 'neutral'}>
                {s.class}
              </StatusBadge>
            </TableCell>
            <TableCell>{s.description}</TableCell>
            <TableCell className="text-fs-sm text-muted-foreground">{s.rationale}</TableCell>
            <TableCell className="font-mono text-fs-sm text-muted-foreground">{s.addresses.join(' ')}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
