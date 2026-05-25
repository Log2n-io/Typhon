import { afterEach, describe, expect, it } from 'vitest';
import { queryRefEqual, selectValidQuery, useQueryAnalyzerStore } from '../useQueryAnalyzerStore';

afterEach(() => useQueryAnalyzerStore.getState().reset());

describe('useQueryAnalyzerStore', () => {
  it('queryRefEqual compares by value, not reference', () => {
    expect(queryRefEqual({ kind: 0, localId: 1 }, { kind: 0, localId: 1 })).toBe(true);
    expect(queryRefEqual({ kind: 0, localId: 1 }, { kind: 0, localId: 2 })).toBe(false);
    expect(queryRefEqual(null, null)).toBe(true);
    expect(queryRefEqual({ kind: 0, localId: 1 }, null)).toBe(false);
  });

  it('setSelectedQuery stamps the session and resets the execution + plan mode', () => {
    const s = useQueryAnalyzerStore.getState();
    s.setSelectedExecution({ tickIndex: 5, systemId: 0 });
    s.setPlanMode('execution');
    s.setSelectedQuery('sess-A', { kind: 0, localId: 1 });
    const st = useQueryAnalyzerStore.getState();
    expect(st.ownerSessionId).toBe('sess-A');
    expect(st.selectedQuery).toEqual({ kind: 0, localId: 1 });
    expect(st.selectedExecution).toBeNull();
    expect(st.planMode).toBe('structural');
  });

  it('re-selecting the same query in the same session is idempotent (keeps the execution)', () => {
    const s = useQueryAnalyzerStore.getState();
    s.setSelectedQuery('A', { kind: 0, localId: 1 });
    s.setSelectedExecution({ tickIndex: 2, systemId: 0 });
    s.setSelectedQuery('A', { kind: 0, localId: 1 }); // same identity → no reset
    expect(useQueryAnalyzerStore.getState().selectedExecution).toEqual({ tickIndex: 2, systemId: 0 });
  });

  it('selectValidQuery returns null when the session differs (stale-session guard)', () => {
    useQueryAnalyzerStore.getState().setSelectedQuery('sess-A', { kind: 0, localId: 1 });
    expect(selectValidQuery(useQueryAnalyzerStore.getState(), 'sess-A')).toEqual({ kind: 0, localId: 1 });
    expect(selectValidQuery(useQueryAnalyzerStore.getState(), 'sess-B')).toBeNull();
    expect(selectValidQuery(useQueryAnalyzerStore.getState(), null)).toBeNull();
  });

  it('setSelectedExecution does NOT flip plan mode (unified-view fix vs the old store quirk)', () => {
    useQueryAnalyzerStore.getState().setPlanMode('structural');
    useQueryAnalyzerStore.getState().setSelectedExecution({ tickIndex: 3, systemId: 1 });
    expect(useQueryAnalyzerStore.getState().planMode).toBe('structural');
  });

  it('showExecutionInPlan selects the execution, flips to execution mode, and reveals the Plan tab', () => {
    useQueryAnalyzerStore.getState().setActiveTab('executions');
    useQueryAnalyzerStore.getState().showExecutionInPlan({ tickIndex: 7, systemId: 2 });
    const st = useQueryAnalyzerStore.getState();
    expect(st.selectedExecution).toEqual({ tickIndex: 7, systemId: 2 });
    expect(st.planMode).toBe('execution');
    expect(st.activeTab).toBe('plan');
  });

  it('execScopeLinked defaults to true and toggles', () => {
    expect(useQueryAnalyzerStore.getState().execScopeLinked).toBe(true);
    useQueryAnalyzerStore.getState().setExecScopeLinked(false);
    expect(useQueryAnalyzerStore.getState().execScopeLinked).toBe(false);
  });
});
