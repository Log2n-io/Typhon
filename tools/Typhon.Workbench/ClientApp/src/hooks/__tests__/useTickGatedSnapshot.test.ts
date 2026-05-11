// @vitest-environment jsdom
import { StrictMode } from 'react';
import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useDebouncedValue, useTickGatedSnapshot } from '../useTickGatedSnapshot';

beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('useDebouncedValue', () => {
  it('returns initial value synchronously', () => {
    const { result } = renderHook(({ v }) => useDebouncedValue(v, 100), { initialProps: { v: 1 } });
    expect(result.current).toBe(1);
  });

  it('coalesces a burst of changes into the last value', () => {
    const { result, rerender } = renderHook(({ v }) => useDebouncedValue(v, 100), { initialProps: { v: 1 } });
    rerender({ v: 2 });
    rerender({ v: 3 });
    rerender({ v: 4 });
    expect(result.current).toBe(1);
    act(() => { vi.advanceTimersByTime(100); });
    expect(result.current).toBe(4);
  });

  it('passes through immediately when delay is 0', () => {
    const { result, rerender } = renderHook(({ v }) => useDebouncedValue(v, 0), { initialProps: { v: 1 } });
    rerender({ v: 2 });
    // 0-delay path uses setDebounced inside an effect — flush timers/effects.
    act(() => { vi.runAllTimers(); });
    expect(result.current).toBe(2);
  });
});

describe('useTickGatedSnapshot', () => {
  it('first-fills as soon as a non-null value arrives, even before the key advances', () => {
    type V = { n: number } | null;
    const { result, rerender } = renderHook(
      ({ value, key }: { value: V; key: number }) => useTickGatedSnapshot(value, key),
      { initialProps: { value: null as V, key: 0 } },
    );
    expect(result.current).toBeNull();
    rerender({ value: { n: 1 }, key: 0 });
    act(() => { vi.runAllTimers(); });
    expect(result.current).toEqual({ n: 1 });
  });

  it('ignores value mutations when the key is unchanged', () => {
    const { result, rerender } = renderHook(
      ({ value, key }: { value: { n: number }; key: number }) => useTickGatedSnapshot(value, key),
      { initialProps: { value: { n: 1 }, key: 5 } },
    );
    expect(result.current).toEqual({ n: 1 });
    // New reference, same key → snapshot must NOT update.
    rerender({ value: { n: 2 }, key: 5 });
    act(() => { vi.runAllTimers(); });
    expect(result.current).toEqual({ n: 1 });
  });

  it('refreshes to the latest value at the moment the key changes', () => {
    const { result, rerender } = renderHook(
      ({ value, key }: { value: { n: number }; key: number }) => useTickGatedSnapshot(value, key),
      { initialProps: { value: { n: 1 }, key: 5 } },
    );
    rerender({ value: { n: 2 }, key: 5 });
    rerender({ value: { n: 3 }, key: 5 });
    rerender({ value: { n: 4 }, key: 6 });
    // Snapshot reflects the latest value at the moment key flipped, not the closure of any earlier render.
    act(() => { vi.runAllTimers(); });
    expect(result.current).toEqual({ n: 4 });
  });

  it('survives React StrictMode (double-mounted effects converge to the right snapshot)', () => {
    // StrictMode mounts components twice in dev — effects run, clean up, run again. A hook that
    // writes state during render or assumes single-mount lifecycle breaks here. Our hook uses only
    // ref writes during render + setState inside effects, so the second mount should land on the
    // same snapshot as the first.
    type V = { n: number } | null;
    const { result, rerender } = renderHook(
      ({ value, key }: { value: V; key: number }) => useTickGatedSnapshot(value, key),
      { initialProps: { value: null as V, key: 0 }, wrapper: StrictMode },
    );
    expect(result.current).toBeNull();
    rerender({ value: { n: 1 }, key: 0 });
    act(() => { vi.runAllTimers(); });
    expect(result.current).toEqual({ n: 1 });
    // Key flip under StrictMode — both mount cycles see the same valueRef.current.
    rerender({ value: { n: 2 }, key: 1 });
    act(() => { vi.runAllTimers(); });
    expect(result.current).toEqual({ n: 2 });
  });
});
