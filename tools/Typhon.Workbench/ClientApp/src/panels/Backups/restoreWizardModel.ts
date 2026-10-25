import type { BackupPointRefDto, BackupRestoreRequestDto, BackupRetentionPolicyDto } from '@/api/generated/model';
import type { RestoreDraft, RetentionDraft, WizardStep } from '@/stores/useBackupsStore';
import { localInputToUtc, type RestorePlan } from './backupsModel';

/**
 * The restore wizard's and the retention flow's gating (#1417 §4.4, WB-7, WB-8), as pure functions: what a draft asks for, whether it is complete, and
 * which steps are reachable. The one rule both enforce: nothing runs without a plan of exactly these inputs — the store drops the plan on any change,
 * and these refuse to run without one.
 */

/** The point a draft names, or why it names none yet. */
export function pointRefOf(draft: RestoreDraft): { ref: BackupPointRefDto } | { error: string } {
  const timelineId = draft.timelineId.trim() || null;
  switch (draft.tab) {
    case 'point':
      return draft.pointId == null
        ? { error: 'Pick a restorable point.' }
        : { ref: { kind: 'id', id: draft.pointId, lsn: null, timeUtc: null, timelineId: null } };
    case 'time': {
      const timeUtc = localInputToUtc(draft.timeLocal);
      return timeUtc
        ? { ref: { kind: 'time', id: null, lsn: null, timeUtc, timelineId } }
        : { error: 'Enter the instant to restore to.' };
    }
    case 'lsn': {
      const lsn = Number(draft.lsn.trim());
      return Number.isSafeInteger(lsn) && lsn > 0
        ? { ref: { kind: 'lsn', id: null, lsn, timeUtc: null, timelineId } }
        : { error: 'Enter a positive LSN.' };
    }
  }
}

/** The request a draft makes; <paramref name="fingerprint"/> null for a preview. */
export function restoreRequestOf(
  store: string,
  bundle: string,
  draft: RestoreDraft,
  fingerprint: string | null,
): BackupRestoreRequestDto | null {
  const point = pointRefOf(draft);
  if (!('ref' in point) || !draft.target.trim()) {
    return null;
  }
  return {
    store,
    point: point.ref,
    target: draft.target.trim(),
    replace: draft.replace,
    allowDamaged: draft.allowDamaged,
    allowFormatMismatch: draft.allowFormatMismatch,
    bundle: bundle || null,
    planFingerprint: fingerprint,
  };
}

/** Whether the wizard may move to <paramref name="step"/>: each step needs what the ones before it produce. */
export function canEnter(step: WizardStep, draft: RestoreDraft, plan: RestorePlan | null): boolean {
  switch (step) {
    case 'point':
      return true;
    case 'target':
      return 'ref' in pointRefOf(draft);
    case 'preview':
      return 'ref' in pointRefOf(draft) && draft.target.trim().length > 0;
    case 'run':
      return canRun(plan);
    case 'result':
      return false;
  }
}

/** A restore runs only from a reviewed plan that refuses nothing and carries its fingerprint. */
export function canRun(plan: RestorePlan | null): boolean {
  return !!plan && plan.canRestore && !!plan.fingerprint;
}

/** A retention draft's policy, or why it is not one yet. Every field must be filled: the Workbench defaults no limit (S10). */
export function retentionPolicyOf(draft: RetentionDraft): { policy: BackupRetentionPolicyDto } | { error: string } {
  const field = (text: string, name: string, scale: number): number | string => {
    const t = text.trim();
    if (t === '') {
      return `${name} is required (0 = no limit).`;
    }
    const v = Number(t);
    return Number.isFinite(v) && v >= 0 ? Math.round(v * scale) : `${name} must be a number ≥ 0.`;
  };
  const maxAge = field(draft.maxAgeDays, 'Max age (days)', 86_400);
  const maxCount = field(draft.maxCount, 'Max count', 1);
  const maxBytes = field(draft.maxTotalGiB, 'Max size (GiB)', 1024 ** 3);
  const minKeep = field(draft.minKeep, 'Min keep', 1);
  const compact = field(draft.compactThreshold, 'Compact at depth', 1);
  for (const v of [maxAge, maxCount, maxBytes, minKeep, compact]) {
    if (typeof v === 'string') {
      return { error: v };
    }
  }
  return {
    policy: {
      maxAgeSeconds: maxAge as number,
      maxCount: maxCount as number,
      maxTotalBytes: maxBytes as number,
      minKeep: minKeep as number,
      compactThreshold: compact as number,
      keepLastDrilled: draft.keepLastDrilled,
    },
  };
}
