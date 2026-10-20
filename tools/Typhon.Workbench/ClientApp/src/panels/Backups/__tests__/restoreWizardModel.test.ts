import { describe, expect, it } from 'vitest';
import type { RestoreDraft, RetentionDraft } from '@/stores/useBackupsStore';
import type { RestorePlan } from '../backupsModel';
import { canEnter, canRun, pointRefOf, restoreRequestOf, retentionPolicyOf } from '../restoreWizardModel';

const draft = (patch: Partial<RestoreDraft> = {}): RestoreDraft => ({
  tab: 'point',
  pointId: null,
  timeLocal: '',
  lsn: '',
  timelineId: '',
  target: '',
  replace: false,
  allowDamaged: false,
  allowFormatMismatch: false,
  ...patch,
});

const plan = (patch: Partial<RestorePlan> = {}) => ({ canRestore: true, fingerprint: 'f', ...patch }) as RestorePlan;

describe('restore wizard gating', () => {
  it('names a point, an instant (sent as UTC) or an LSN — or says what is missing', () => {
    expect(pointRefOf(draft())).toEqual({ error: 'Pick a restorable point.' });
    expect(pointRefOf(draft({ pointId: 4 }))).toEqual({ ref: { kind: 'id', id: 4, lsn: null, timeUtc: null, timelineId: null } });

    const time = pointRefOf(draft({ tab: 'time', timeLocal: '2026-10-21T14:03' }));
    expect('ref' in time && time.ref.timeUtc).toBe(new Date('2026-10-21T14:03').toISOString());
    expect('ref' in time && time.ref.timeUtc?.endsWith('Z')).toBe(true);

    expect(pointRefOf(draft({ tab: 'lsn', lsn: '-3' }))).toEqual({ error: 'Enter a positive LSN.' });
    expect(pointRefOf(draft({ tab: 'lsn', lsn: '1200', timelineId: 'tl' }))).toEqual({
      ref: { kind: 'lsn', id: null, lsn: 1200, timeUtc: null, timelineId: 'tl' },
    });
  });

  it('reaches the target with a point, the preview with a target, and never runs without a plan that refuses nothing', () => {
    expect(canEnter('target', draft(), null)).toBe(false);
    expect(canEnter('target', draft({ pointId: 1 }), null)).toBe(true);
    expect(canEnter('preview', draft({ pointId: 1 }), null)).toBe(false);
    expect(canEnter('preview', draft({ pointId: 1, target: 'C:/r' }), null)).toBe(true);

    expect(canEnter('run', draft({ pointId: 1, target: 'C:/r' }), null)).toBe(false);
    expect(canRun(plan({ canRestore: false }))).toBe(false);
    expect(canRun(plan({ fingerprint: null }))).toBe(false);
    expect(canRun(plan())).toBe(true);
  });

  it('a run request carries the reviewed fingerprint; a preview carries none', () => {
    const d = draft({ pointId: 2, target: ' C:/r ', replace: true });
    expect(restoreRequestOf('S', 'B', d, null)?.planFingerprint).toBeNull();
    expect(restoreRequestOf('S', 'B', d, 'f9')).toMatchObject({ store: 'S', bundle: 'B', target: 'C:/r', replace: true, planFingerprint: 'f9' });
    expect(restoreRequestOf('S', 'B', draft({ pointId: 2 }), null)).toBeNull();
  });
});

describe('retention policy', () => {
  const full: RetentionDraft = { maxAgeDays: '30', maxCount: '0', maxTotalGiB: '0', minKeep: '2', compactThreshold: '0', keepLastDrilled: true };

  it('defaults no field: an empty one is refused', () => {
    expect(retentionPolicyOf({ ...full, minKeep: '' })).toEqual({ error: 'Min keep is required (0 = no limit).' });
    expect(retentionPolicyOf({ ...full, maxCount: 'many' })).toEqual({ error: 'Max count must be a number ≥ 0.' });
  });

  it('converts days and GiB to the engine’s seconds and bytes', () => {
    expect(retentionPolicyOf({ ...full, maxTotalGiB: '1.5' })).toEqual({
      policy: { maxAgeSeconds: 30 * 86_400, maxCount: 0, maxTotalBytes: 1.5 * 1024 ** 3, minKeep: 2, compactThreshold: 0, keepLastDrilled: true },
    });
  });
});
