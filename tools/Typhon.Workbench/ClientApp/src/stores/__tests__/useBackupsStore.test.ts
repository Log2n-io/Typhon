import { beforeEach, describe, expect, it } from 'vitest';
import { scratchFor, useBackupsStore } from '../useBackupsStore';
import type { RestorePlan, RetentionPlan } from '@/panels/Backups/backupsModel';

const restorePlan = { canRestore: true, fingerprint: 'f1' } as RestorePlan;
const retentionPlan = { fingerprint: 'r1', packDeletions: ['00000001-F.tpack'] } as RetentionPlan;

describe('useBackupsStore', () => {
  beforeEach(() => {
    useBackupsStore.setState({
      bundlePath: '',
      storePath: '',
      storeByBundle: {},
      scratchByStore: {},
      selectedPointId: null,
      activeJobId: null,
      wizardOpen: false,
      wizardStep: 'point',
      restorePlan: null,
      retentionOpen: false,
      retentionPlan: null,
      retentionConsent: false,
      standaloneOpen: false,
    });
  });

  it('changing the store drops every plan, the selection and the job shown — they were about the other store', () => {
    const s = useBackupsStore.getState();
    s.setStore('C:/stores/a');
    s.selectPoint(3);
    s.setActiveJob('job-1');
    s.openWizard(3);
    s.setRestorePlan(restorePlan);
    s.openRetention();
    s.setRetentionPlan(retentionPlan);

    useBackupsStore.getState().setStore('C:/stores/b');

    const after = useBackupsStore.getState();
    expect(after.storePath).toBe('C:/stores/b');
    expect([after.selectedPointId, after.activeJobId, after.restorePlan, after.retentionPlan]).toEqual([
      null,
      null,
      null,
      null,
    ]);
    expect([after.wizardOpen, after.retentionOpen]).toEqual([false, false]);
  });

  it('setting the same store again keeps everything', () => {
    useBackupsStore.getState().setStore('S');
    useBackupsStore.getState().setActiveJob('job-1');
    useBackupsStore.getState().setStore('S');
    expect(useBackupsStore.getState().activeJobId).toBe('job-1');
  });

  it('remembers the store per bundle and brings it back with the bundle (PC-1)', () => {
    const s = useBackupsStore.getState();
    s.setBundle('C:/db/World.typhon');
    s.setStore('D:/backups/world');
    s.setBundle('C:/db/Other.typhon');
    s.setStore('D:/backups/other');

    useBackupsStore.getState().setBundle('c:/DB/world.typhon');
    expect(useBackupsStore.getState().storePath).toBe('D:/backups/world');

    useBackupsStore.getState().setBundle('C:/db/never-seen.typhon');
    expect(useBackupsStore.getState().storePath, 'no memory: the store field keeps what it had').toBe(
      'D:/backups/world',
    );
  });

  it('any restore input change drops the plan and steps back from the preview', () => {
    const s = useBackupsStore.getState();
    s.openWizard(2);
    s.updateRestore({ target: 'C:/restored' });
    s.setRestorePlan(restorePlan);
    s.setWizardStep('preview');

    useBackupsStore.getState().updateRestore({ replace: true });

    const after = useBackupsStore.getState();
    expect(after.restorePlan).toBeNull();
    expect(after.wizardStep).toBe('target');
    expect(after.restore.replace).toBe(true);
  });

  it('consent never outlives its plan: a new plan, or a changed policy, resets it', () => {
    const s = useBackupsStore.getState();
    s.openRetention();
    s.setRetentionPlan(retentionPlan);
    s.setRetentionConsent(true);
    useBackupsStore.getState().setRetentionPlan({ ...retentionPlan, fingerprint: 'r2' });
    expect(useBackupsStore.getState().retentionConsent).toBe(false);

    useBackupsStore.getState().setRetentionConsent(true);
    useBackupsStore.getState().updateRetention({ maxCount: '5' });
    expect([useBackupsStore.getState().retentionConsent, useBackupsStore.getState().retentionPlan]).toEqual([
      false,
      null,
    ]);
  });

  it('remembers a drill scratch per store, else the server’s default', () => {
    useBackupsStore.getState().setScratch('D:/backups/World', 'E:/scratch');
    const remembered = useBackupsStore.getState().scratchByStore;
    expect(scratchFor('d:/backups/world', remembered, 'C:/default')).toBe('E:/scratch');
    expect(scratchFor('D:/other', remembered, 'C:/default')).toBe('C:/default');
    expect(scratchFor('D:/other', remembered, null)).toBe('');
  });
});
