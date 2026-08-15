// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { KnownDatabase, KnownDatabaseList } from '@/libs/databases/knownDatabases';

/**
 * #622 (F9) AC12–AC13 — the Known-databases tab.
 *
 * These are about *claims*, not layout: that a switched-off registry says so instead of rendering an empty list, that a
 * vanished database is still listed (so it can be forgotten) rather than dropped, and that a failed request does not
 * read as "nothing recorded". Those three states would otherwise all render as an empty panel while meaning completely
 * different things.
 *
 * The hook is mocked rather than the fetch layer. Driving a *failure* through React Query leaves its internal promise
 * unhandled, which the runner reports against whichever test is running — and the HTTP contract underneath is already
 * pinned from both ends: `DatabasesControllerTests` on the server and `scratch/api-f9.py` against a live one.
 */

const forgetMutate = vi.fn();
const pruneMutate = vi.fn();
let hookState: {
  list: KnownDatabaseList | null;
  isLoading: boolean;
  isError: boolean;
  error: unknown;
  forgetError: unknown;
};

vi.mock('@/hooks/useKnownDatabases', () => ({
  useKnownDatabases: () => ({
    list: hookState.list,
    isLoading: hookState.isLoading,
    isError: hookState.isError,
    error: hookState.error,
    forget: { mutate: forgetMutate, isPending: false, error: hookState.forgetError },
    pruneMissing: { mutate: pruneMutate, isPending: false, error: null },
  }),
}));

import KnownDatabasesTab from '../tabs/KnownDatabasesTab';

function db(name: string, exists: boolean): KnownDatabase {
  return {
    name,
    bundlePath: `C:\\srv\\data\\${name}.typhon`,
    databaseId: '00000000-0000-0000-0000-000000000001',
    firstSeenUtc: '2026-07-01T10:00:00Z',
    lastOpenedUtc: '2026-08-01T10:00:00Z',
    lastOpenedBy: 'AntHill.Demo',
    exists,
  };
}

function withList(entries: KnownDatabase[], overrides: Partial<KnownDatabaseList> = {}) {
  hookState.list = {
    enabled: true,
    disabledReason: null,
    registryDirectory: 'C:\\Users\\x\\AppData\\Local\\Typhon\\databases',
    entries,
    ...overrides,
  };
}

function renderTab(onOpen = vi.fn()) {
  render(<KnownDatabasesTab onOpen={onOpen} />);
  return onOpen;
}

beforeEach(() => {
  forgetMutate.mockReset();
  pruneMutate.mockReset();
  hookState = { list: null, isLoading: false, isError: false, error: null, forgetError: null };
});
afterEach(() => cleanup());

describe('KnownDatabasesTab', () => {
  it('lists databases this machine has opened', () => {
    withList([db('world', true), db('shard', true)]);
    renderTab();

    expect(screen.getByTestId('known-db-world')).toBeTruthy();
    expect(screen.getByTestId('known-db-shard')).toBeTruthy();
  });

  it('opens a database through the shared open flow, with no schema DLLs of its own', () => {
    // The registry records paths, not the schema assemblies a session was opened with — that is the Recent list's job.
    withList([db('world', true)]);
    const onOpen = renderTab();

    fireEvent.click(screen.getByText('world'));

    expect(onOpen).toHaveBeenCalledWith('C:\\srv\\data\\world.typhon', []);
  });

  it('marks a vanished database instead of dropping it, and offers to prune', () => {
    withList([db('here', true), db('gone', false)]);
    renderTab();

    expect(screen.getByTestId('known-db-missing-gone')).toBeTruthy();
    expect(screen.getByTestId('prune-missing').textContent).toContain('(1)');
  });

  it('will not open a database that is no longer there', () => {
    withList([db('gone', false)]);
    const onOpen = renderTab();

    fireEvent.click(screen.getByText('gone'));

    expect(onOpen).not.toHaveBeenCalled();
  });

  it('offers no prune action when nothing is missing', () => {
    withList([db('here', true)]);
    renderTab();

    expect(screen.queryByTestId('prune-missing')).toBeNull();
  });

  it('says the registry is switched off, and names the switch', () => {
    // AC13. An empty list here would teach the user the feature is useless rather than that it is disabled — the exact
    // failure D-7 argues against — so "off" must be a visible, attributed state.
    withList([], { enabled: false, disabledReason: "A 'disabled' file exists in C:\\registry." });
    renderTab();

    expect(screen.getByTestId('registry-disabled').textContent).toContain('disabled');
  });

  it('keeps serving rows recorded before the registry was switched off', () => {
    withList([db('recorded-earlier', true)], { enabled: false, disabledReason: 'off by env var' });
    renderTab();

    expect(screen.getByTestId('registry-disabled')).toBeTruthy();
    expect(screen.getByTestId('known-db-recorded-earlier')).toBeTruthy();
  });

  it('teaches what an empty list means rather than just showing nothing', () => {
    withList([]);
    renderTab();

    expect(screen.getByText(/No databases recorded yet/)).toBeTruthy();
    expect(screen.queryByTestId('registry-disabled')).toBeNull();
  });

  it('says the registry could not be read, rather than that nothing was recorded', () => {
    hookState.isError = true;
    hookState.error = new Error('connection refused');
    renderTab();

    expect(screen.getByTestId('registry-error').textContent).toContain('connection refused');
    expect(screen.queryByText(/No databases recorded yet/)).toBeNull();
  });

  it('surfaces a failed forget instead of leaving the row silently in place', () => {
    withList([db('locked', true)]);
    hookState.forgetError = new Error('the entry is in use');
    renderTab();

    expect(screen.getByTestId('registry-mutation-error').textContent).toContain('the entry is in use');
  });

  it('forgets exactly the row whose button was clicked', () => {
    withList([db('keep', true), db('drop', true)]);
    renderTab();

    fireEvent.click(screen.getByLabelText('Forget drop'));

    expect(forgetMutate).toHaveBeenCalledExactlyOnceWith('C:\\srv\\data\\drop.typhon');
  });
});
