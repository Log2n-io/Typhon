// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import SystemsQueriesNavigatorPanel from '@/panels/SystemsQueriesNavigator/SystemsQueriesNavigatorPanel';
import { useSessionStore } from '@/stores/useSessionStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import type { ProfilerMetadataDto, SystemDefinitionDto } from '@/api/generated/model';

// The Trace/Attach navigator (zone C): renders systems from profiler metadata and writes the bus on
// click — the load→navigate→inspect chain for profiler sessions (component-level; the full Trace/Attach
// slice E2E is gated on a trace fixture / live engine, R2).

function renderNav() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SystemsQueriesNavigatorPanel />
    </QueryClientProvider>,
  );
}

const sys = (index: number, name: string): SystemDefinitionDto =>
  ({ index, name, phaseName: 'Simulation' } as unknown as SystemDefinitionDto);

beforeEach(() => {
  useSelectionStore.getState().clear();
  // sessionId null → the data hooks stay disabled (no network); the navigator reads systems from the
  // hydrated profiler-session store, exactly as it does once the metadata fetch has landed.
  useSessionStore.setState({ kind: 'trace', sessionId: null });
  useProfilerSessionStore.setState({
    metadata: { systems: [sys(0, 'Movement'), sys(1, 'Damage')] } as unknown as ProfilerMetadataDto,
    buildError: null,
  });
});
afterEach(() => {
  cleanup();
  useProfilerSessionStore.setState({ metadata: null });
});

describe('SystemsQueriesNavigator', () => {
  it('lists systems from metadata', () => {
    renderNav();
    expect(screen.getByText('Movement')).toBeTruthy();
    expect(screen.getByText('Damage')).toBeTruthy();
  });

  it('writes the bus leaf when a system row is clicked', () => {
    renderNav();
    fireEvent.click(screen.getByRole('button', { name: /Movement/ }));
    expect(useSelectionStore.getState().leaf).toMatchObject({ type: 'system', ref: 'Movement' });
    // It also projects the system scalar slot for cross-panel highlighting.
    expect(useSelectionStore.getState().system).toBe('Movement');
  });

  // PC-8 roving (suite F). The arrow-key *mechanics* are owned by the vetted Radix RovingFocusGroup (PC-8:
  // "never hand-roll roving") and verified in a real browser — jsdom doesn't run Radix's focus collection.
  // Here we assert the jsdom-stable invariant: every row is a roving *item*, so the whole list is ONE tab stop
  // (Radix manages each row's tabindex) rather than N independent tab stops as before.
  it('puts the whole list under one tab stop (every row is a roving item)', () => {
    renderNav();
    const rows = screen.getAllByRole('button');
    expect(rows.length).toBeGreaterThan(1);
    // A roving item always carries an explicit (Radix-managed) tabindex; plain multi-tab-stop buttons don't.
    expect(rows.every((b) => b.getAttribute('tabindex') !== null)).toBe(true);
  });

  it('Esc backs focus out of the list', () => {
    renderNav();
    const movement = screen.getByRole('button', { name: /Movement/ });
    movement.focus();
    expect(document.activeElement).toBe(movement);
    fireEvent.keyDown(movement, { key: 'Escape' });
    expect(document.activeElement).not.toBe(movement);
  });
});
