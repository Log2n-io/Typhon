// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import ContextBar from '@/shell/ContextBar';
import { useSessionStore } from '@/stores/useSessionStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useEnvTagStore } from '@/stores/useEnvTagStore';

beforeEach(() => {
  useSelectionStore.getState().clear();
  useEnvTagStore.setState({ tags: {} });
  useSessionStore.setState({ kind: 'open', filePath: 'C:/data/AntHill.typhon', sessionId: null });
  useProfilerViewStore.setState({ scopeLinked: true, pinnedRange: null });
});
afterEach(cleanup);

describe('ContextBar (zone B)', () => {
  it('shows identity (file + kind) and the Open-session scope', () => {
    render(<ContextBar />);
    expect(screen.getByText('AntHill.typhon')).toBeTruthy();
    expect(screen.getByText('Open')).toBeTruthy();
    expect(screen.getByText('@HEAD')).toBeTruthy();
  });

  it('shows the trace time-window scope from the profiler view range', () => {
    useSessionStore.setState({ kind: 'trace' });
    useProfilerViewStore.getState().commitViewRange({ startUs: 1000, endUs: 5000 });
    render(<ContextBar />);
    expect(screen.getByText(/1\.0ms–5\.0ms/)).toBeTruthy();
  });

  it('shows a link/unlink scope toggle in trace sessions and flips it on click (3B)', () => {
    useSessionStore.setState({ kind: 'trace' });
    useProfilerViewStore.getState().commitViewRange({ startUs: 1000, endUs: 5000 });
    render(<ContextBar />);
    const toggle = screen.getByRole('button', { name: /click to unlink/i });
    expect(toggle.getAttribute('aria-pressed')).toBe('true');
    fireEvent.click(toggle);
    expect(useProfilerViewStore.getState().scopeLinked).toBe(false);
    // The cluster's window is now frozen at the value present when unlinked.
    expect(useProfilerViewStore.getState().pinnedRange).toEqual({ startUs: 1000, endUs: 5000 });
    // The button now offers to re-link.
    expect(screen.getByRole('button', { name: /click to re-link/i })).toBeTruthy();
  });

  it('has no scope toggle in Open sessions (revision scope, not a time window)', () => {
    render(<ContextBar />); // beforeEach sets kind 'open'
    expect(screen.queryByRole('button', { name: /unlink|re-link/i })).toBeNull();
  });

  it('renders the breadcrumb chain and navigates the bus when a crumb is clicked', () => {
    // A field leaf → breadcrumb is "Position › X" (component ancestor + field leaf).
    useSelectionStore.getState().select('field', { component: 'Position', field: 'X' });
    render(<ContextBar />);
    expect(screen.getByRole('button', { name: 'X' })).toBeTruthy();
    const componentCrumb = screen.getByRole('button', { name: 'Position' });
    fireEvent.click(componentCrumb);
    // Clicking the ancestor crumb re-targets the bus leaf to that component.
    expect(useSelectionStore.getState().leaf).toMatchObject({ type: 'component', ref: 'Position' });
  });

  it('sets and persists a per-file environment tag that tints the bar', () => {
    const { container } = render(<ContextBar />);
    fireEvent.click(screen.getByRole('button', { name: /^Tag$/ }));
    fireEvent.click(screen.getByRole('button', { name: /^PROD$/ }));
    expect(useEnvTagStore.getState().get('C:/data/AntHill.typhon')).toBe('prod');
    // The bar carries the prod tint border.
    expect(container.querySelector('.border-l-red-500')).toBeTruthy();
  });
});
