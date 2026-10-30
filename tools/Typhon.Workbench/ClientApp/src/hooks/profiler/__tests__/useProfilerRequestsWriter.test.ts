// @vitest-environment jsdom
import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { useProfilerRequestsWriter } from '@/hooks/profiler/useProfilerRequestsWriter';
import type { RequestRow } from '@/libs/profiler/correlation/requests';
import { buildSpan } from '@/libs/profiler/model/__tests__/spanBuilder';
import type { ProfilerSelection, SpanData } from '@/libs/profiler/model/traceModel';
import { useProfilerRequestsStore } from '@/stores/useProfilerRequestsStore';
import { useSelectionStore } from '@/stores/useSelectionStore';

/** The pin follows the selection (#1517): it must not outlive what showed it, or it dims the lanes with nothing on screen to say why. */

const row: RequestRow = {
  id: 'cmd', key: '11:22', kind: 'command', label: 'command 24 → PlayerSessions', startUs: 0, endUs: 100, totalUs: 100, spanCount: 1, engineUs: 10,
  markers: [],
};
const span = (hi: string, lo: string): SpanData => ({ ...buildSpan({ startUs: 10, endUs: 20, threadSlot: 1 }), traceIdHi: hi, traceIdLo: lo });
const selectRef = (ref: ProfilerSelection): void => act(() => useSelectionStore.getState().select('span', ref));
const pinned = (): string | null => useProfilerRequestsStore.getState().selectedKey;

beforeEach(() => {
  useProfilerRequestsStore.getState().clear();
  act(() => useSelectionStore.getState().clear());
  renderHook(() => useProfilerRequestsWriter([], [], { startUs: 0, endUs: 1_000 }));
  selectRef({ kind: 'request', row });
  act(() => useProfilerRequestsStore.getState().select(row.key));
});
afterEach(() => act(() => useSelectionStore.getState().clear()));

describe('useProfilerRequestsWriter pin', () => {
  it('keeps the pin while the selection moves to a span the pinned root caused', () => {
    selectRef({ kind: 'span', span: span('11', '22') });
    expect(pinned()).toBe(row.key);
  });

  it('unpins when another span is selected', () => {
    selectRef({ kind: 'span', span: span('33', '44') });
    expect(pinned()).toBeNull();
  });

  it('unpins when the selection is cleared', () => {
    act(() => useSelectionStore.getState().clear());
    expect(pinned()).toBeNull();
  });
});
