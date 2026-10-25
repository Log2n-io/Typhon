import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { useBackupsStore, type RetentionDraft } from '@/stores/useBackupsStore';
import { useBackupActions } from '@/hooks/backups/useBackupActions';
import { formatBytes } from './backupsModel';
import { retentionPolicyOf } from './restoreWizardModel';

const FIELDS: readonly { key: keyof Omit<RetentionDraft, 'keepLastDrilled'>; label: string; hint: string }[] = [
  { key: 'maxAgeDays', label: 'Max age (days)', hint: '0 = no age limit' },
  { key: 'maxCount', label: 'Max count', hint: '0 = no count limit' },
  { key: 'maxTotalGiB', label: 'Max size (GiB)', hint: '0 = no size limit' },
  { key: 'minKeep', label: 'Min keep', hint: 'restorable points always kept, ≥ 1' },
  { key: 'compactThreshold', label: 'Compact at depth', hint: '0 = never, else ≥ 2' },
];

/**
 * Retention (#1417 WB-8, AC-W6): policy → preview → apply. The preview is mandatory and is the plan the server applies — the apply names it by its
 * fingerprint, and the engine refuses (nothing deleted) when the store would now delete a pack the preview did not list. Consent is to this plan only:
 * any policy change drops the plan and the consent with it. No limit is defaulted by the Workbench (S10).
 */
export default function RetentionFlow({ store, busy }: { store: string; busy: boolean }) {
  const draft = useBackupsStore((s) => s.retention);
  const plan = useBackupsStore((s) => s.retentionPlan);
  const consent = useBackupsStore((s) => s.retentionConsent);
  const update = useBackupsStore((s) => s.updateRetention);
  const setConsent = useBackupsStore((s) => s.setRetentionConsent);
  const close = useBackupsStore((s) => s.closeRetention);
  const { planRetention, applyRetention } = useBackupActions();

  const parsed = retentionPolicyOf(draft);
  const blocked = plan != null && plan.blocked !== 'None';

  return (
    <div className="shrink-0 border-b border-border px-3 py-2" data-testid="backups-retention-flow">
      <div className="mb-1 flex items-center gap-2">
        <span className="text-fs-base font-medium">Retention</span>
        <span className="text-fs-sm text-muted-foreground">
          Preview first; the apply runs exactly the plan you reviewed.
        </span>
        <Button variant="ghost" onClick={close} className="ml-auto h-6 px-1.5 text-fs-sm">
          Close
        </Button>
      </div>

      <div className="grid grid-cols-[repeat(auto-fill,minmax(11rem,1fr))] gap-2">
        {FIELDS.map((f) => (
          <label key={f.key} className="flex flex-col gap-0.5 text-fs-sm">
            <span className="text-muted-foreground">{f.label}</span>
            <Input
              value={draft[f.key]}
              onChange={(e) => update({ [f.key]: e.target.value })}
              placeholder={f.hint}
              className="h-7 font-mono text-fs-sm"
              data-testid={`backups-retention-${f.key}`}
            />
          </label>
        ))}
        <label className="flex items-center gap-1.5 self-end text-fs-sm">
          <input
            type="checkbox"
            checked={draft.keepLastDrilled}
            onChange={(e) => update({ keepLastDrilled: e.target.checked })}
          />
          Keep the newest drill-passed point
        </label>
      </div>

      <div className="mt-2 flex items-center gap-2">
        <Button
          variant="outline"
          disabled={!('policy' in parsed) || planRetention.isPending}
          onClick={() => 'policy' in parsed && planRetention.mutate({ store, policy: parsed.policy })}
          className="h-7 px-2 text-fs-sm"
          data-testid="backups-retention-preview"
        >
          Preview
        </Button>
        {'error' in parsed && <span className="text-fs-sm text-muted-foreground">{parsed.error}</span>}
        {planRetention.isError && (
          <span className="text-fs-sm text-destructive">{(planRetention.error as Error).message}</span>
        )}
      </div>

      {plan && (
        <div className="mt-2 text-fs-sm" data-testid="backups-retention-plan">
          <div>
            Keeps {plan.kept.length} point(s){plan.kept.length ? ` (#${plan.kept.join(', #')})` : ''} · compacts{' '}
            {plan.compactions.length ? `#${plan.compactions.join(', #')}` : 'nothing'} · removes{' '}
            {plan.removed.length ? `#${plan.removed.join(', #')}` : 'nothing'}
          </div>
          <div className="text-muted-foreground">
            {formatBytes(plan.bytesBefore)} now → at most {formatBytes(plan.bytesAfterAtMost)} after.
          </div>
          {blocked && (
            <div className="text-amber-600">
              Cannot meet the policy ({plan.blocked}): {plan.blockedReason}
            </div>
          )}
          {plan.packDeletions.length > 0 ? (
            <>
              <div className="mt-1">
                Deletes, in this order (newest first, so a crash part-way leaves every listed point restorable):
              </div>
              <ul className="font-mono text-fs-xs" data-testid="backups-retention-deletions">
                {plan.packDeletions.map((f) => (
                  <li key={f}>{f}</li>
                ))}
              </ul>
              <label className="mt-1 flex items-center gap-1.5">
                <input
                  type="checkbox"
                  checked={consent}
                  onChange={(e) => setConsent(e.target.checked)}
                  data-testid="backups-retention-consent"
                />
                Delete these {plan.packDeletions.length} pack(s)
              </label>
            </>
          ) : (
            <div className="mt-1 text-muted-foreground">Deletes nothing.</div>
          )}
          {(plan.packDeletions.length === 0 ? plan.compactions.length > 0 : consent) && (
            <Button
              disabled={busy || applyRetention.isPending}
              onClick={() => applyRetention.mutate({ store, fingerprint: plan.fingerprint })}
              className="mt-1 h-7 px-3 text-fs-sm"
              data-testid="backups-retention-apply"
            >
              Apply this plan
            </Button>
          )}
          {applyRetention.isError && <div className="text-destructive">{(applyRetention.error as Error).message}</div>}
        </div>
      )}
    </div>
  );
}
