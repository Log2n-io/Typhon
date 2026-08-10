import { beforeEach, describe, expect, it } from 'vitest';
import { integrityStage, useIntegrityStore } from '../useIntegrityStore';
import type { Report, RepairOutcome, RepairPlan } from '@/panels/Integrity/integrityModel';

const report = { verdict: 'DataLoss', findings: [] } as unknown as Report;
const plan = { fingerprint: 'abc123', loss: [], steps: [] } as unknown as RepairPlan;
const outcome = { succeeded: true, results: [] } as unknown as RepairOutcome;

describe('useIntegrityStore', () => {
  beforeEach(() => {
    useIntegrityStore.getState().reset();
  });

  it('starts with backups on and consent off', () => {
    const s = useIntegrityStore.getState();
    expect(s.backupFirst).toBe(true);
    expect(s.allowLoss).toBe(false);
    expect(s.depth).toBe('standard');
  });

  // ── The invalidation rules. These are the correctness properties of this store, not conveniences. ──

  it('a new scan discards the plan built from the previous one', () => {
    // A plan is bound to a database fingerprint. Leaving it on screen after a re-scan would invite applying
    // it against a diagnosis that has since changed — the failure the server's fingerprint check exists to
    // catch. Catch it here too, before the operator has invested attention in reading it.
    const s = useIntegrityStore.getState();
    s.setReport(report);
    s.setPlan(plan);
    expect(useIntegrityStore.getState().plan).not.toBeNull();

    s.setReport(report);
    expect(useIntegrityStore.getState().plan).toBeNull();
    expect(useIntegrityStore.getState().outcome).toBeNull();
  });

  it('a new plan revokes consent given for the previous one', () => {
    // Otherwise an operator could tick the box against one loss manifest and apply a different one.
    const s = useIntegrityStore.getState();
    s.setPlan(plan);
    s.setAllowLoss(true);
    expect(useIntegrityStore.getState().allowLoss).toBe(true);

    s.setPlan(plan);
    expect(useIntegrityStore.getState().allowLoss).toBe(false);
  });

  it('a new scan revokes consent too', () => {
    const s = useIntegrityStore.getState();
    s.setPlan(plan);
    s.setAllowLoss(true);
    s.setReport(report);
    expect(useIntegrityStore.getState().allowLoss).toBe(false);
  });

  it('retargeting the path clears every downstream artefact', () => {
    // A report about one database must never sit next to another database's path.
    const s = useIntegrityStore.getState();
    s.setReport(report);
    s.setPlan(plan);
    s.setAllowLoss(true);

    s.setPath('C:\\other.typhon');
    const next = useIntegrityStore.getState();
    expect(next.report).toBeNull();
    expect(next.plan).toBeNull();
    expect(next.outcome).toBeNull();
    expect(next.allowLoss).toBe(false);
  });

  it('openStandalone without a path keeps the loaded report, so reopening returns to it', () => {
    const s = useIntegrityStore.getState();
    s.setPath('C:\\db.typhon');
    s.setReport(report);
    s.closeStandalone();

    s.openStandalone();
    const next = useIntegrityStore.getState();
    expect(next.standaloneOpen).toBe(true);
    expect(next.report).not.toBeNull();
  });

  it('openStandalone with a different path invalidates like any other retarget', () => {
    const s = useIntegrityStore.getState();
    s.setReport(report);
    s.openStandalone('C:\\other.typhon');
    const next = useIntegrityStore.getState();
    expect(next.path).toBe('C:\\other.typhon');
    expect(next.report).toBeNull();
  });
});

describe('integrityStage', () => {
  it('derives the flow position from what is loaded', () => {
    expect(integrityStage({ report: null, plan: null, outcome: null })).toBe('idle');
    expect(integrityStage({ report, plan: null, outcome: null })).toBe('scanned');
    expect(integrityStage({ report, plan, outcome: null })).toBe('planned');
    expect(integrityStage({ report, plan, outcome })).toBe('applied');
  });
});
