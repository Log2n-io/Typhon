import { useMutation, useQueryClient } from '@tanstack/react-query';
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
import { useBackupsStore, type VerifyLevel } from '@/stores/useBackupsStore';
import { backupKeys } from './useBackupQueries';

/**
 * The Backups view's operator acts (#1417 §4.2) — every one a mutation, never a query: a job starts when its button is pressed, and a plan is computed
 * when the operator asks for one and is then kept in the store until an input changes (Integrity's argument: a plan refetched behind the operator is a
 * plan they did not review). A start the store refuses as busy attaches to the job already running instead of failing.
 */

/** The running job a 409 `busy` names, if that is what the error is. */
export function busyJobId(error: unknown): string | null {
  if (error instanceof FetchError && error.status === 409 && error.problem.title === 'busy') {
    const id = error.problem.runningJobId;
    return typeof id === 'string' ? id : null;
  }
  return null;
}

function useJobStart<TVars>(start: (vars: TVars) => Promise<{ data: BackupJobDto }>) {
  const setActiveJob = useBackupsStore((s) => s.setActiveJob);
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (vars: TVars) => {
      try {
        return (await start(vars)).data.jobId;
      } catch (e) {
        const running = busyJobId(e);
        if (running) {
          return running;
        }
        throw e;
      }
    },
    onSuccess: (jobId) => {
      setActiveJob(jobId);
      void queryClient.invalidateQueries({ queryKey: backupKeys.all });
    },
  });
}

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
  const compact = useJobStart((v: { store: string; point: BackupPointRefDto }) => postApiBackupsJobsCompact({ store: v.store, point: v.point }));
  const applyRetention = useJobStart((v: { store: string; fingerprint: string }) =>
    postApiBackupsJobsRetention({ store: v.store, approvedFingerprint: v.fingerprint }),
  );
  const restore = useJobStart((v: BackupRestoreRequestDto) => postApiBackupsJobsRestore(v));

  const cancel = useMutation({ mutationFn: async (jobId: string) => deleteApiBackupsJobsJobId(jobId) });

  const planRestore = useMutation({
    mutationFn: async (v: BackupRestoreRequestDto) => normalizeRestorePlan((await postApiBackupsRestorePlan(v)).data),
    onSuccess: (plan) => setRestorePlan(plan),
  });

  const planRetention = useMutation({
    mutationFn: async (v: { store: string; policy: BackupRetentionPolicyDto }) =>
      normalizeRetentionPlan((await postApiBackupsRetentionPlan({ store: v.store, policy: v.policy })).data),
    onSuccess: (plan) => setRetentionPlan(plan),
  });

  const resolveTime = useMutation({
    mutationFn: async (v: { store: string; timeUtc: string; timelineId: string }) =>
      (await postApiBackupsArchiveResolveTime({ store: v.store, timeUtc: v.timeUtc, timelineId: v.timelineId || null })).data,
  });

  const readLog = useMutation({
    mutationFn: async (v: { store: string; timelineId: string; fromUtc: string; toUtc: string }) =>
      (await postApiBackupsArchiveLog({ store: v.store, timelineId: v.timelineId || null, fromUtc: v.fromUtc, toUtc: v.toUtc, max: 200 })).data,
  });

  return { create, verify, drill, compact, applyRetention, restore, cancel, planRestore, planRetention, resolveTime, readLog };
}
