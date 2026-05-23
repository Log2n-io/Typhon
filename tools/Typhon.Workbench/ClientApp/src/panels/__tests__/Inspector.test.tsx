// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import DetailPanel from '@/panels/DetailPanel';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import type { SelectedResource } from '@/stores/useSelectedResourceStore';

// The Inspector dispatches off the unified bus leaf (Stage 1). These cover the no-fetch leaf types +
// the empty state + the PC-6 affordance audit; the data-fetching cards (field/entity/profiler/dbmap) are
// covered by the load-a-file slice E2E.

const sampleResource: SelectedResource = {
  resourceId: 'r-1',
  kind: 'ComponentTable',
  name: 'ComponentTable_Position',
  path: ['Storage', 'ComponentTable_Position'],
  raw: { id: 'r-1' } as SelectedResource['raw'],
};

beforeEach(() => {
  useSelectionStore.getState().clear();
  // An Open session so the profiler range-stats fallback doesn't pre-empt the empty prompt.
  useSessionStore.setState({ kind: 'open' });
});
afterEach(cleanup);

describe('Inspector — bus-driven dispatch', () => {
  it('renders the empty prompt when nothing is selected', () => {
    render(<DetailPanel />);
    expect(screen.getByText(/select anything/i)).toBeTruthy();
  });

  it('renders the Resource card for a resource leaf', () => {
    useSelectionStore.getState().select('resource', sampleResource);
    render(<DetailPanel />);
    expect(screen.getByText('ComponentTable_Position')).toBeTruthy();
    expect(screen.getByText(/Storage \/ ComponentTable_Position/)).toBeTruthy();
  });

  it('renders a gated summary card for a component leaf (deep view returns later)', () => {
    useSelectionStore.getState().select('component', 'Position');
    render(<DetailPanel />);
    expect(screen.getByText('Position')).toBeTruthy();
    expect(screen.getByText(/deep view returns in a later stage/i)).toBeTruthy();
  });

  it('renders a gated summary card for a system leaf', () => {
    useSelectionStore.getState().select('system', 'Movement');
    render(<DetailPanel />);
    expect(screen.getByText('Movement')).toBeTruthy();
  });

  it('exposes no broken affordance (PC-6 / suite E): no disabled Open in / Reveal in / Go to control', () => {
    useSelectionStore.getState().select('resource', sampleResource);
    const { container } = render(<DetailPanel />);
    const dead = Array.from(container.querySelectorAll('button, [role="button"]')).filter((el) => {
      const disabled = (el as HTMLButtonElement).disabled || el.getAttribute('aria-disabled') === 'true';
      return disabled && /\b(open in|reveal in|go to)\b/i.test(el.textContent ?? '');
    });
    expect(dead).toEqual([]);
  });
});
