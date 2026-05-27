// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import SystemsQueriesNavigatorPanel from '@/panels/SystemsQueriesNavigator/SystemsQueriesNavigatorPanel';
import { useSessionStore } from '@/stores/useSessionStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import type { ProfilerMetadataDto, SystemDefinitionDto } from '@/api/generated/model';

// Mock the cross-panel reveal commands — they call `ensureDockPanel` which needs the registered
// DockviewApi (not set up in jsdom). The spies let us assert the verb-button wiring without
// dragging in the dock host.
const revealSpies = {
  dag: vi.fn(),
  cp: vi.fn(),
  flow: vi.fn(),
  inspector: vi.fn(),
};
vi.mock('@/shell/commands/openDbMap', async () => {
  const actual = await vi.importActual<typeof import('@/shell/commands/openDbMap')>('@/shell/commands/openDbMap');
  return {
    ...actual,
    revealSystemInDag: (n: string) => revealSpies.dag(n),
    revealSystemInCriticalPath: (n: string) => revealSpies.cp(n),
    revealSystemInDataFlow: (n: string) => revealSpies.flow(n),
    revealSystemInInspector: (n: string) => revealSpies.inspector(n),
  };
});

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
  for (const spy of Object.values(revealSpies)) spy.mockReset();
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

  // ── Hover-revealed verb buttons (#379 follow-up, 2026-05-26) ─────────────────────────────────
  // Each System row carries four mouse-hover-revealed verb buttons (`Reveal in System DAG / CP /
  // Flow / Inspector`) so the navigator's outcomes are visible regardless of which cluster panels
  // the user has open — fixing the previous "click writes the bus invisibly" feel.
  describe('System row — hover-revealed reveal verbs', () => {
    it('renders the four reveal verbs per system row', () => {
      renderNav();
      // Testid lookup is unambiguous (the icon-only buttons share no visible text with anything else).
      expect(screen.getByTestId('systems-nav-reveal-dag-Movement')).toBeTruthy();
      expect(screen.getByTestId('systems-nav-reveal-cp-Movement')).toBeTruthy();
      expect(screen.getByTestId('systems-nav-reveal-flow-Movement')).toBeTruthy();
      expect(screen.getByTestId('systems-nav-reveal-inspector-Movement')).toBeTruthy();
      // Per-system: each system gets its own four — the testid suffix carries the system name.
      expect(screen.getByTestId('systems-nav-reveal-dag-Damage')).toBeTruthy();
    });

    it('clicking "Reveal in System DAG" invokes revealSystemInDag with the system name', () => {
      renderNav();
      fireEvent.click(screen.getByTestId('systems-nav-reveal-dag-Movement'));
      expect(revealSpies.dag).toHaveBeenCalledWith('Movement');
      // The other reveals must NOT have fired — verbs are unambiguous.
      expect(revealSpies.cp).not.toHaveBeenCalled();
      expect(revealSpies.flow).not.toHaveBeenCalled();
      expect(revealSpies.inspector).not.toHaveBeenCalled();
    });

    it('clicking "Reveal in Critical Path" invokes revealSystemInCriticalPath', () => {
      renderNav();
      fireEvent.click(screen.getByTestId('systems-nav-reveal-cp-Damage'));
      expect(revealSpies.cp).toHaveBeenCalledWith('Damage');
    });

    it('clicking "Reveal in Data Flow" invokes revealSystemInDataFlow', () => {
      renderNav();
      fireEvent.click(screen.getByTestId('systems-nav-reveal-flow-Movement'));
      expect(revealSpies.flow).toHaveBeenCalledWith('Movement');
    });

    it('clicking "Reveal in Inspector" invokes revealSystemInInspector', () => {
      renderNav();
      fireEvent.click(screen.getByTestId('systems-nav-reveal-inspector-Movement'));
      expect(revealSpies.inspector).toHaveBeenCalledWith('Movement');
    });

    it('verb-button click does NOT bubble to the row body (no double bus-write)', () => {
      renderNav();
      // Body click would set the bus leaf type=system; verb click should NOT (it delegates to the
      // mocked reveal which doesn't write the bus here). Pre-click leaf is null (cleared in beforeEach).
      expect(useSelectionStore.getState().leaf).toBeNull();
      fireEvent.click(screen.getByTestId('systems-nav-reveal-dag-Movement'));
      // No leaf write happened — stopPropagation prevented the row body's onSelect from firing.
      expect(useSelectionStore.getState().leaf).toBeNull();
      // Confirm the reveal spy ran (proves the verb click DID work, just didn't double-fire).
      expect(revealSpies.dag).toHaveBeenCalledWith('Movement');
    });

    it('verb buttons are not Tab-reachable (tabIndex=-1) — keyboard cursor stays on row bodies', () => {
      renderNav();
      const verb = screen.getByTestId('systems-nav-reveal-dag-Movement');
      // `tabIndex` reflects the attribute; -1 = focusable programmatically, not via Tab. This both
      // documents the keyboard-traversal contract and satisfies the upstream "every button has an
      // explicit tabindex" invariant in the roving-focus test below.
      expect(verb.getAttribute('tabindex')).toBe('-1');
    });
  });
});
