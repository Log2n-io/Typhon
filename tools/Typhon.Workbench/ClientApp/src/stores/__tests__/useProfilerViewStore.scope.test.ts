import { beforeEach, describe, expect, it } from 'vitest';
import { selectEffectiveScope, useProfilerViewStore } from '@/stores/useProfilerViewStore';

/**
 * Stage 3 Phase 3 (3B) — the linked/unlink scope. `selectEffectiveScope` is the single resolver the
 * scheduling-cluster panels read: the live `viewRange` while linked, the frozen `pinnedRange` while unlinked.
 */
beforeEach(() => {
  useProfilerViewStore.setState({ scopeLinked: true, pinnedRange: null });
  useProfilerViewStore.getState().commitViewRange({ startUs: 1000, endUs: 5000 });
});

describe('useProfilerViewStore — link/unlink scope (3B)', () => {
  it('defaults to linked; effective scope is the live viewRange', () => {
    expect(useProfilerViewStore.getState().scopeLinked).toBe(true);
    expect(selectEffectiveScope(useProfilerViewStore.getState())).toEqual({ startUs: 1000, endUs: 5000 });
  });

  it('unlinking freezes the current window; later viewRange changes are ignored by the effective scope', () => {
    useProfilerViewStore.getState().setScopeLinked(false);
    expect(useProfilerViewStore.getState().scopeLinked).toBe(false);
    expect(useProfilerViewStore.getState().pinnedRange).toEqual({ startUs: 1000, endUs: 5000 });
    // The timeline keeps moving while unlinked …
    useProfilerViewStore.getState().commitViewRange({ startUs: 8000, endUs: 9000 });
    // … but the cluster's effective scope stays frozen on the pinned window.
    expect(selectEffectiveScope(useProfilerViewStore.getState())).toEqual({ startUs: 1000, endUs: 5000 });
  });

  it('re-linking clears the pin and resumes following the live viewRange', () => {
    useProfilerViewStore.getState().setScopeLinked(false);
    useProfilerViewStore.getState().commitViewRange({ startUs: 8000, endUs: 9000 });
    useProfilerViewStore.getState().setScopeLinked(true);
    expect(useProfilerViewStore.getState().pinnedRange).toBeNull();
    expect(selectEffectiveScope(useProfilerViewStore.getState())).toEqual({ startUs: 8000, endUs: 9000 });
  });

  it('returns an existing object reference (selector is allocation-free → zustand-safe)', () => {
    const linked = useProfilerViewStore.getState();
    expect(selectEffectiveScope(linked)).toBe(linked.viewRange);
    useProfilerViewStore.getState().setScopeLinked(false);
    const unlinked = useProfilerViewStore.getState();
    expect(selectEffectiveScope(unlinked)).toBe(unlinked.pinnedRange);
  });
});
