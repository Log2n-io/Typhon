import { ClipboardList } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useIntegrityStore } from '@/stores/useIntegrityStore';
import { useRepairPlan } from '@/hooks/integrity/useIntegrityActions';
import { verdictIsDamage, type Report } from './integrityModel';

/**
 * The entry to the repair flow, shown under a finished report.
 *
 * Planning is offered as a separate, explicitly read-only step rather than a "Repair" button that does
 * everything. The friction is the feature: the cost of one extra click is seconds, and the cost of a wrong
 * automatic repair is unbounded and unrecoverable. The button says *Plan* because that is all it does — it
 * re-scans at Deep depth and describes what it would do, writing nothing.
 */
export default function RepairFooter({ report }: { report: Report }) {
  const path = useIntegrityStore((s) => s.path);
  const planMutation = useRepairPlan();

  // A leaks-only database is not damaged. Offering repair here would push someone toward a mutation they
  // don't need — reclaiming space is a maintenance task, not a correctness one.
  const needsRepair = verdictIsDamage(report.verdict);

  return (
    <div className="flex shrink-0 items-center gap-2 border-t border-border px-3 py-2" data-testid="integrity-repair-footer">
      <span className="text-fs-base text-muted-foreground">
        {needsRepair
          ? 'Planning is read-only — it describes what a repair would do and what it would cost.'
          : 'Nothing here needs repairing.'}
      </span>
      <Button
        onClick={() => planMutation.mutate({ path })}
        disabled={planMutation.isPending || !path}
        variant={needsRepair ? 'default' : 'outline'}
        data-testid="integrity-plan"
        className="ml-auto h-7 gap-1 px-3 text-fs-base"
      >
        <ClipboardList className="h-3.5 w-3.5" />
        {planMutation.isPending ? 'Planning…' : 'Plan a repair'}
      </Button>
      {planMutation.isError && (
        <span className="text-fs-sm text-destructive" data-testid="integrity-plan-error">
          {(planMutation.error as Error)?.message ?? 'Planning failed.'}
        </span>
      )}
    </div>
  );
}
