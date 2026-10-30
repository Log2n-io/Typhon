// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { RequestRow } from '@/libs/profiler/correlation/requests';
import RequestsPanel from '@/panels/profiler/RequestsPanel';
import { useProfilerRequestsStore } from '@/stores/useProfilerRequestsStore';
import { useSessionStore } from '@/stores/useSessionStore';

/** The Requests panel's filter (#1517): it narrows the list, and the store carries it to the time area's root lanes. */

const row = (id: string, label: string, session: number, totalUs: number): RequestRow => ({
  id, key: id, kind: 'command', label, session, startUs: 0, endUs: totalUs, totalUs, spanCount: 0, engineUs: 0, markers: [],
});
const rows = [row('a', 'command 3 → Combat', 1, 500), row('b', 'command 24 → PlayerSessions', 2, 30_000), row('c', 'command 24 → PlayerSessions', 1, 900)];

beforeEach(() => {
  useProfilerRequestsStore.getState().clear();
  useProfilerRequestsStore.setState({ allRows: rows, rows });
  useSessionStore.setState({ capabilities: ['profiler'] });
});
afterEach(cleanup);

const listed = (): string[] => screen.getAllByTestId('request-row').map((el) => el.firstElementChild?.textContent ?? '');

describe('RequestsPanel filter', () => {
  it('narrows the list as typed, and clears', () => {
    render(<RequestsPanel />);
    expect(listed()).toHaveLength(3);
    fireEvent.change(screen.getByLabelText('Filter by label'), { target: { value: 'combat' } });
    expect(listed()).toEqual(['command 3 → Combat']);
    expect(screen.getByText(/1 of 3 in view/)).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Filter by label'), { target: { value: '' } });
    fireEvent.change(screen.getByLabelText('Minimum total in milliseconds'), { target: { value: '0.9' } });
    expect(useProfilerRequestsStore.getState().filter.minTotalUs).toBe(900);
    expect(listed()).toHaveLength(2);

    fireEvent.click(screen.getByText('Clear'));
    expect(listed()).toHaveLength(3);
    expect((screen.getByLabelText('Minimum total in milliseconds') as HTMLInputElement).value).toBe('');
  });

  it('a session click keeps only that client, without selecting the row', () => {
    render(<RequestsPanel />);
    fireEvent.click(screen.getAllByTitle('Show only this client')[0]); // sorted by total: b, session 2, first
    expect(useProfilerRequestsStore.getState().filter.session).toBe(2);
    expect(useProfilerRequestsStore.getState().selectedKey).toBeNull();
    expect(listed()).toEqual(['command 24 → PlayerSessions']);
    expect((screen.getByLabelText('Filter by session') as HTMLInputElement).value).toBe('2');
  });

  it('an endpoint click filters on it', () => {
    render(<RequestsPanel />);
    fireEvent.click(screen.getByText('By endpoint'));
    fireEvent.click(screen.getAllByTestId('request-group').find((el) => el.textContent?.includes('Combat'))!);
    expect(useProfilerRequestsStore.getState().filter.text).toBe('command 3 → Combat');
  });
});
