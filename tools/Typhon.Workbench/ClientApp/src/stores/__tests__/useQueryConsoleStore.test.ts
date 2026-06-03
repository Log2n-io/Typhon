import { describe, expect, it, beforeEach } from 'vitest';
import { useQueryConsoleStore, HISTORY_RING_SIZE, EMPTY_SPEC } from '../useQueryConsoleStore';

/**
 * Store unit tests for {@link useQueryConsoleStore} (#386 Phase 1 — AC-10 + AC-16). Covers mode toggle, spec
 * mutation, history ring eviction, and saved-query overwrite. The store is pure (no DOM / fetch / engine) so tests
 * run in plain Vitest with no jsdom.
 */
describe('useQueryConsoleStore', () => {
  beforeEach(() => {
    useQueryConsoleStore.getState().reset();
    // reset() doesn't touch persisted state — clear savedQueries explicitly per test.
    useQueryConsoleStore.setState({ savedQueries: [], history: [] });
  });

  it('starts in chips mode with the empty spec', () => {
    const s = useQueryConsoleStore.getState();
    expect(s.mode).toBe('chips');
    expect(s.spec.archetype).toBe(EMPTY_SPEC.archetype);
    expect(s.spec.polymorphic).toBe(true);
    expect(s.spec.take).toBe(1000);
  });

  it('toggles between chip and dsl modes', () => {
    useQueryConsoleStore.getState().setMode('dsl');
    expect(useQueryConsoleStore.getState().mode).toBe('dsl');
    useQueryConsoleStore.getState().setMode('chips');
    expect(useQueryConsoleStore.getState().mode).toBe('chips');
  });

  it('persists DSL draft + spec edits through setters', () => {
    useQueryConsoleStore.getState().setDslDraft('FROM Foo WHERE Foo.Bar >= 5');
    useQueryConsoleStore.getState().setSpec({ ...EMPTY_SPEC, archetype: 'Foo' });
    const s = useQueryConsoleStore.getState();
    expect(s.dslDraft).toBe('FROM Foo WHERE Foo.Bar >= 5');
    expect(s.spec.archetype).toBe('Foo');
  });

  it('bounded ring evicts oldest history entries past capacity', () => {
    const { appendHistory } = useQueryConsoleStore.getState();
    for (let i = 0; i < HISTORY_RING_SIZE + 10; i++) {
      appendHistory({ dsl: `q${i}`, ranAt: i, rowCount: i, elapsedNs: 0, errorCode: null });
    }
    const s = useQueryConsoleStore.getState();
    expect(s.history.length).toBe(HISTORY_RING_SIZE);
    // Newest first: the just-appended q59 sits at index 0; the original q0–q9 should have been evicted.
    expect(s.history[0].dsl).toBe(`q${HISTORY_RING_SIZE + 10 - 1}`);
    expect(s.history.find((h) => h.dsl === 'q0')).toBeUndefined();
  });

  it('saved-query save/delete are idempotent on (filePath, name)', () => {
    const { saveQuery, deleteSavedQuery } = useQueryConsoleStore.getState();
    saveQuery('/x.typhon', 'first', 'FROM A');
    saveQuery('/x.typhon', 'first', 'FROM B');   // same id; should overwrite
    expect(useQueryConsoleStore.getState().savedQueries.length).toBe(1);
    expect(useQueryConsoleStore.getState().savedQueries[0].dsl).toBe('FROM B');

    deleteSavedQuery('/x.typhon::first');
    expect(useQueryConsoleStore.getState().savedQueries.length).toBe(0);
  });

  it('loadSavedQuery seeds DSL + switches to DSL mode (no auto-run, anti-pattern guard)', () => {
    const { saveQuery, loadSavedQuery } = useQueryConsoleStore.getState();
    saveQuery('/x.typhon', 'kept', 'FROM Saved WHERE Saved.Field == 1');
    loadSavedQuery('/x.typhon::kept');
    const s = useQueryConsoleStore.getState();
    expect(s.dslDraft).toContain('FROM Saved');
    expect(s.mode).toBe('dsl');
    // Anti-pattern check: loadSavedQuery does NOT run the query; runState stays idle.
    expect(s.runState).toBe('idle');
  });

  it('setRunResult clears errors on ok and surfaces them on error', () => {
    const { setRunResult } = useQueryConsoleStore.getState();
    setRunResult(null, 'error', 'invalid_field', 'Field X is not indexed.');
    let s = useQueryConsoleStore.getState();
    expect(s.runErrorCode).toBe('invalid_field');
    expect(s.runErrorMessage).toBe('Field X is not indexed.');

    setRunResult(null, 'idle');
    s = useQueryConsoleStore.getState();
    expect(s.runErrorCode).toBeNull();
    expect(s.runErrorMessage).toBeNull();
  });
});
