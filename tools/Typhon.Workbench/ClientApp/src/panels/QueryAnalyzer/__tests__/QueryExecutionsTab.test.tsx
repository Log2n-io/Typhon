// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { ProfilerMetadataDto, TickSummaryDto } from '@/api/generated/model';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useQueryAnalyzerStore } from '../useQueryAnalyzerStore';
import { QueryExecutionsTab } from '../QueryExecutionsTab';
import { makeExecution } from './fixtures';

// Partial-mock so only `jumpToTimeRange` is a spy; the rest of the command module stays real.
const jumpSpy = vi.hoisted(() => vi.fn());
vi.mock('@/shell/commands/profilerCommands', async (orig) => ({
  ...(await orig<typeof import('@/shell/commands/profilerCommands')>()),
  jumpToTimeRange: jumpSpy,
}));

// 3 ticks of 100µs each; a [0,150)µs window overlaps ticks 1-2 only.
const ticks = [
  { tickNumber: 1, startUs: 0, durationUs: 100 },
  { tickNumber: 2, startUs: 100, durationUs: 100 },
  { tickNumber: 3, startUs: 200, durationUs: 100 },
] as unknown as TickSummaryDto[];

const execs = [
  makeExecution({ tickIndex: 1, startTs: 0, endTs: 50 }),
  makeExecution({ tickIndex: 2, startTs: 100, endTs: 160 }),
  makeExecution({ tickIndex: 3, startTs: 200, endTs: 260 }),
];

const systemNames = new Map<number, string>([[0, 'Movement']]);

beforeEach(() => {
  useProfilerSessionStore.setState({ metadata: { tickSummaries: ticks } as unknown as ProfilerMetadataDto });
  useProfilerViewStore.setState({ scopeLinked: true, viewRange: { startUs: 0, endUs: 150 }, pinnedRange: null });
  useQueryAnalyzerStore.getState().reset();
});
afterEach(() => cleanup());

describe('QueryExecutionsTab (AC7 — GAP-11 client-side time scope)', () => {
  it('linked: filters executions to the global time window (ticks 1-2 of 3)', () => {
    render(<QueryExecutionsTab executions={execs} systemNames={systemNames} />);
    expect(screen.getByTestId('query-executions-count').textContent).toBe('2 / 3');
    expect(screen.getAllByTestId('execution-list-row')).toHaveLength(2);
  });

  it('unlink shows all executions (whole trace)', () => {
    render(<QueryExecutionsTab executions={execs} systemNames={systemNames} />);
    fireEvent.click(screen.getByTestId('query-executions-scope-toggle'));
    expect(screen.getByTestId('query-executions-count').textContent).toBe('3');
    expect(screen.getAllByTestId('execution-list-row')).toHaveLength(3);
  });

  it('renders the per-phase breakdown for the auto-selected first execution', () => {
    render(<QueryExecutionsTab executions={execs} systemNames={systemNames} />);
    expect(screen.getAllByTestId('execution-phase-row').length).toBeGreaterThanOrEqual(3);
  });

  it('"Show in plan" flips the store to execution mode + Plan tab', () => {
    render(<QueryExecutionsTab executions={execs} systemNames={systemNames} />);
    fireEvent.click(screen.getByTestId('query-executions-show-in-plan'));
    const st = useQueryAnalyzerStore.getState();
    expect(st.planMode).toBe('execution');
    expect(st.activeTab).toBe('plan');
    expect(st.selectedExecution).not.toBeNull();
  });

  it('AC-D1.4: "Jump to time" sets the global window to the selected execution (ns→µs /1000)', () => {
    render(<QueryExecutionsTab executions={execs} systemNames={systemNames} />);
    // Auto-selects the first in-window execution (tick 1: startTs 0, endTs 50).
    fireEvent.click(screen.getByTestId('query-executions-jump-to-time'));
    expect(jumpSpy).toHaveBeenCalledWith(0, 0.05);
  });
});
