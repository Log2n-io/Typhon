import { useMutation } from '@tanstack/react-query';
import { FetchError } from '@/api/client';
import {
  deleteApiBackupsJobsJobId,
  postApiBackupsArchiveLog,
  postApiBackupsArchiveResolveTime,
  postApiBackupsJobsCompact,
  postApiBackupsJobsCreate,
  postApiBackupsJobsDrill,
  postApiBackupsJobsRestore,
  postApiBackupsJobsRetention,
  postApiBackupsJobsVerify,
  postApiBackupsRestorePlan,
  postApiBackupsRetentionPlan,
} from '@/api/generated/backups/backups';
import type {
  BackupJobDto,
  BackupPointRefDto,
  BackupRestoreRequestDto,
  BackupRetentionPolicyDto,
} from '@/api/generated/model';
import { normalizeRestorePlan, normalizeRetentionPlan } from '@/panels/Backups/backupsModel';
import { restoreRequestOf, retentionPolicyOf } from '@/panels/Backups/restoreWizardModel';
import { useBackupsStore, type VerifyLevel } from '@/stores/useBackupsStore';
import { useInvalidateAfterJob } from './useBackupQueries';

/**
 * The Backups view's operator acts (#1417 §4.2) — every one a mutation, never a query: a job starts when its button is pressed, and a plan is computed
 * when the operator asks for one and is then kept in the store until an input changes (Integrity's argument: a plan refetched behind the operator is a
 * plan they did not review). A plan answer is kept only while the inputs it was asked for are still the ones on screen.
 *
 * A start the server refuses as busy is a refusal, never a start (the wizard does not close as if its restore ran): it fails with the server's
 * sentence, and the card attaches to the job in the way only when that job is this store's — a drill busy on another store is not this store's job.
 */

/** The job a 409 `busy` names, and whether it runs on the store the start was for. */
export function busyOf(error: unknown): { jobId: string; sameStore: boolean } | null {
  if (error instanceof FetchError && error.status === 409 && error.problem.title === 'busy') {
    const id = error.problem.runningJobId;
    return typeof id === 'string' ? { jobId: id, sameStore: error.problem.sameStore === true } : null;
  }
  return null;
}

function useJobStart<TVars>(start: (vars: TVars) => Promise<{ data: BackupJobDto }>) {
  const setActiveJob = useBackupsStore((s) => s.setActiveJob);
  const invalidateAfter = useInvalidateAfterJob();
  return useMutation({
    mutationFn: async (vars: TVars) => (await start(vars)).data.jobId,
    onSuccess: (jobId) => {
      setActiveJob(jobId);
      void invalidateAfter(null);
    },
    onError: (e) => {
      const busy = busyOf(e);
      if (busy?.sameStore) {
        setActiveJob(busy.jobId);
      }
    },
  });
}

/** Whether an answer to `asked` is still about what the operator has on screen. */
const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);

export function useBackupActions() {
  const setRestorePlan = useBackupsStore((s) => s.setRestorePlan);
  const setRetentionPlan = useBackupsStore((s) => s.setRetentionPlan);

  const create = useJobStart((v: { store: string; bundle: string; full: boolean }) =>
    postApiBackupsJobsCreate({ store: v.store, bundle: v.bundle || null, full: v.full }),
  );
  const verify = useJobStart((v: { store: string; point: BackupPointRefDto | null; level: VerifyLevel }) =>
    postApiBackupsJobsVerify({ store: v.store, point: v.point as BackupPointRefDto, level: v.level }),
  );
  const drill = useJobStart((v: { store: string; point: BackupPointRefDto; scratch: string; bundle: string }) =>
    postApiBackupsJobsDrill({ store: v.store, point: v.point, scratch: v.scratch || null, bundle: v.bundle || null }),
  );
  const compact = useJobStart((v: { store: string; point: BackupPointRefDto }) =>
    postApiBackupsJobsCompact({ store: v.store, point: v.point }),
  );
  const applyRetention = useJobStart((v: { store: string; fingerprint: string }) =>
    postApiBackupsJobsRetention({ store: v.store, approvedFingerprint: v.fingerprint }),
  );
  const restore = useJobStart((v: BackupRestoreRequestDto) => postApiBackupsJobsRestore(v));

  const cancel = useMutation({ mutationFn: async (jobId: string) => deleteApiBackupsJobsJobId(jobId) });

  // A preview answered after its inputs changed is dropped: the store already dropped that plan, and showing it would put an old plan against new fields.
  const planRestore = useMutation({
    mutationFn: async (v: BackupRestoreRequestDto) => normalizeRestorePlan((await postApiBackupsRestorePlan(v)).data),
    onSuccess: (plan, asked) => {
      const s = useBackupsStore.getState();
      if (s.wizardOpen && same(restoreRequestOf(s.storePath, s.bundlePath, s.restore, null), asked)) {
        setRestorePlan(plan);
      }
    },
  });

  const planRetention = useMutation({
    mutationFn: async (v: { store: string; policy: BackupRetentionPolicyDto }) =>
      normalizeRetentionPlan((await postApiBackupsRetentionPlan({ store: v.store, policy: v.policy })).data),
    onSuccess: (plan, asked) => {
      const s = useBackupsStore.getState();
      const policy = retentionPolicyOf(s.retention);
      if (s.retentionOpen && s.storePath === asked.store && 'policy' in policy && same(policy.policy, asked.policy)) {
        setRetentionPlan(plan);
      }
    },
  });

  const resolveTime = useMutation({
    mutationFn: async (v: { store: string; timeUtc: string; timelineId: string }) =>
      (await postApiBackupsArchiveResolveTime({ store: v.store, timeUtc: v.timeUtc, timelineId: v.timelineId || null }))
        .data,
  });

  const readLog = useMutation({
    mutationFn: async (v: { store: string; timelineId: string; fromUtc: string; toUtc: string }) =>
      (
        await postApiBackupsArchiveLog({
          store: v.store,
          timelineId: v.timelineId || null,
          fromUtc: v.fromUtc,
          toUtc: v.toUtc,
          max: 200,
        })
      ).data,
  });

  return {
    create,
    verify,
    drill,
    compact,
    applyRetention,
    restore,
    cancel,
    planRestore,
    planRetention,
    resolveTime,
    readLog,
  };
}
