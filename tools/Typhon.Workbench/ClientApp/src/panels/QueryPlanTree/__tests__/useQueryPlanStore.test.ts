import { beforeEach, describe, expect, it } from 'vitest';
import type { QueryExecutionDto } from '@/api/generated/model/queryExecutionDto';
import { useQueryPlanStore } from '../useQueryPlanStore';

beforeEach(() => {
  useQueryPlanStore.getState().reset();
});

function fakeExecution(): QueryExecutionDto {
  return {
    definitionId: { kind: 0, localId: 1 },
    spanId: 0,
    parentSpanId: 0,
    tickIndex: 0,
    systemId: -1,
    startTs: 0,
    endTs: 0,
    args: null,
    phases: [],
  };
}

describe('useQueryPlanStore', () => {
  it('initial state: no focus, structural mode, no execution', () => {
    const s = useQueryPlanStore.getState();
    expect(s.focus).toBeNull();
    expect(s.mode).toBe('structural');
    expect(s.selectedExecution).toBeNull();
  });

  it('setFocus stores the (kind, localId) pair', () => {
    useQueryPlanStore.getState().setFocus({ kind: 0, localId: 42 });
    expect(useQueryPlanStore.getState().focus).toEqual({ kind: 0, localId: 42 });
  });

  it('setFocus clears any pre-existing execution + resets mode to structural', () => {
    useQueryPlanStore.getState().setSelectedExecution(fakeExecution());
    expect(useQueryPlanStore.getState().mode).toBe('execution');
    useQueryPlanStore.getState().setFocus({ kind: 1, localId: 7 });
    expect(useQueryPlanStore.getState().mode).toBe('structural');
    expect(useQueryPlanStore.getState().selectedExecution).toBeNull();
  });

  it('setSelectedExecution(execution) flips mode to execution', () => {
    useQueryPlanStore.getState().setSelectedExecution(fakeExecution());
    expect(useQueryPlanStore.getState().mode).toBe('execution');
    expect(useQueryPlanStore.getState().selectedExecution).not.toBeNull();
  });

  it('setSelectedExecution(null) reverts mode to structural', () => {
    useQueryPlanStore.getState().setSelectedExecution(fakeExecution());
    useQueryPlanStore.getState().setSelectedExecution(null);
    expect(useQueryPlanStore.getState().mode).toBe('structural');
  });

  it('setMode toggles independently when an execution is loaded', () => {
    useQueryPlanStore.getState().setSelectedExecution(fakeExecution());
    useQueryPlanStore.getState().setMode('structural');
    expect(useQueryPlanStore.getState().mode).toBe('structural');
    useQueryPlanStore.getState().setMode('execution');
    expect(useQueryPlanStore.getState().mode).toBe('execution');
  });

  it('reset returns to initial state', () => {
    useQueryPlanStore.getState().setFocus({ kind: 0, localId: 1 });
    useQueryPlanStore.getState().setSelectedExecution(fakeExecution());
    useQueryPlanStore.getState().reset();
    const s = useQueryPlanStore.getState();
    expect(s.focus).toBeNull();
    expect(s.mode).toBe('structural');
    expect(s.selectedExecution).toBeNull();
  });
});
