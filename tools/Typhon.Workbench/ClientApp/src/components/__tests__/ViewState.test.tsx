// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import ViewState from '@/components/ViewState';
import { deriveViewPhase } from '@/hooks/use202Query';
import { FetchError } from '@/api/client';

afterEach(cleanup);

// Conformance suite D — PC-2 standard state set.

describe('suite D — deriveViewPhase', () => {
  it('prioritises error > no-selection > loading > building > empty > ready', () => {
    expect(deriveViewPhase({ isLoading: true, error: new Error('x'), building: true, isEmpty: true })).toBe('error');
    expect(deriveViewPhase({ isLoading: true, error: null, building: true, isEmpty: true, noSelection: true })).toBe('no-selection');
    expect(deriveViewPhase({ isLoading: true, error: null, building: true, isEmpty: true })).toBe('loading');
    expect(deriveViewPhase({ isLoading: false, error: null, building: true, isEmpty: true })).toBe('building');
    expect(deriveViewPhase({ isLoading: false, error: null, building: false, isEmpty: true })).toBe('empty');
    expect(deriveViewPhase({ isLoading: false, error: null, building: false, isEmpty: false })).toBe('ready');
  });
});

describe('suite D — ViewState renders each state', () => {
  const child = <div>READY-CONTENT</div>;

  it('loading → a busy skeleton (not the children)', () => {
    render(<ViewState phase="loading">{child}</ViewState>);
    expect(screen.getByTestId('view-state-loading')).toBeTruthy();
    expect(screen.queryByText('READY-CONTENT')).toBeNull();
  });

  it('building (202) → a message, never an error', () => {
    render(<ViewState phase="building" buildingMessage="Building the trace index…">{child}</ViewState>);
    expect(screen.getByText(/building the trace index/i)).toBeTruthy();
  });

  it('empty → a sentence, never a blank panel', () => {
    render(<ViewState phase="empty" emptyMessage="No archetypes in this database.">{child}</ViewState>);
    expect(screen.getByText(/no archetypes/i)).toBeTruthy();
  });

  it('no-selection → an inline picker / hint', () => {
    render(<ViewState phase="no-selection" noSelection={<span>pick a component</span>}>{child}</ViewState>);
    expect(screen.getByText(/pick a component/i)).toBeTruthy();
  });

  it('error → ProblemDetails title + Retry, never a raw status code', () => {
    const onRetry = vi.fn();
    render(
      <ViewState phase="error" error={new FetchError(500, { title: 'Trace build faulted' })} onRetry={onRetry}>
        {child}
      </ViewState>,
    );
    expect(screen.getByText('Trace build faulted')).toBeTruthy();
    expect(screen.queryByText(/500/)).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: /retry/i }));
    expect(onRetry).toHaveBeenCalled();
  });

  it('ready → the children', () => {
    render(<ViewState phase="ready">{child}</ViewState>);
    expect(screen.getByText('READY-CONTENT')).toBeTruthy();
  });
});
