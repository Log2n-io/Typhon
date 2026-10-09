import { beforeEach, describe, expect, it } from 'vitest';
import { activeRealmScope, useRealmScopeStore } from '../useRealmScopeStore';
import { useSelectionStore } from '../useSelectionStore';
import { resetSessionScopedState } from '../resetSessionScopedState';

/**
 * #1083 rung 5 — the realm scope, the third global scope beside the time window and the revision.
 */
beforeEach(() => {
  useRealmScopeStore.getState().clear();
  useSelectionStore.getState().clear();
});

describe('the realm scope', () => {
  /**
   * **The assertion AC-5 exists for.** A scope that silently defaulted to realm 0 would reintroduce WB-06's bug one
   * layer up: the compiler stopped assuming realm 0, and the UI must not start. Unscoped has to mean "no realm named",
   * which is what lets every panel behave exactly as it did before this store existed.
   */
  it('is unset by default, and unset is not realm 0', () => {
    const state = useRealmScopeStore.getState();

    expect(state.realm).toBeNull();
    expect(activeRealmScope(state)).toBeNull();
    expect(activeRealmScope(state)).not.toBe(0);
  });

  it('realm 0 is a scope like any other, not the absence of one', () => {
    useRealmScopeStore.getState().setRealm(0);

    expect(activeRealmScope(useRealmScopeStore.getState())).toBe(0);
  });

  it('narrows to the realm it was given', () => {
    useRealmScopeStore.getState().setRealm(7);

    expect(activeRealmScope(useRealmScopeStore.getState())).toBe(7);
  });

  /** Unlinking keeps the chosen realm visible but stops it narrowing — the same affordance the time window has. */
  it('stops narrowing when unlinked, without forgetting the realm', () => {
    useRealmScopeStore.getState().setRealm(7);
    useRealmScopeStore.getState().setLinked(false);

    const state = useRealmScopeStore.getState();
    expect(state.realm).toBe(7);
    expect(activeRealmScope(state)).toBeNull();

    useRealmScopeStore.getState().setLinked(true);
    expect(activeRealmScope(useRealmScopeStore.getState())).toBe(7);
  });

  it('setting the same realm again is a no-op, so it does not re-notify', () => {
    useRealmScopeStore.getState().setRealm(7);
    const first = useRealmScopeStore.getState();

    useRealmScopeStore.getState().setRealm(7);

    expect(useRealmScopeStore.getState()).toBe(first);
  });
});

describe('the scope against the selection', () => {
  /**
   * Scope and selection are separate stores on purpose: selecting a realm means *looking at* it, scoping means
   * *working in* it. If clicking a realm in the navigator silently re-pointed the Query Console at it, the context bar
   * would stop describing the state the panels are actually in.
   */
  it('selecting a realm does not scope to it', () => {
    useSelectionStore.getState().select('realm', 7);

    expect(activeRealmScope(useRealmScopeStore.getState())).toBeNull();
  });

  it('scoping to a realm does not select it', () => {
    useRealmScopeStore.getState().setRealm(7);

    expect(useSelectionStore.getState().leaf).toBeNull();
  });
});

describe('the scope across a session switch', () => {
  /**
   * A realm id is a coordinate INTO a database, so it means nothing in the next one — and carrying realm 7 across a
   * switch would silently narrow the new session's panels to a realm that may not exist there.
   */
  it('is cleared with the rest of the session-scoped state', () => {
    useRealmScopeStore.getState().setRealm(7);
    useRealmScopeStore.getState().setLinked(false);

    resetSessionScopedState();

    const state = useRealmScopeStore.getState();
    expect(state.realm).toBeNull();
    expect(state.linked).toBe(true);
  });
});
