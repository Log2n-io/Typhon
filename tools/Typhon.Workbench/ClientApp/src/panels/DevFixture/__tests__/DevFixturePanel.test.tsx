// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { IDockviewPanelProps } from 'dockview-react';
import {
  DATABASE_NAME_STORAGE_KEY,
  loadOutputDirFromStorage,
  OUTPUT_DIR_STORAGE_KEY,
  saveDatabaseNameToStorage,
  saveOutputDirToStorage,
  saveUseBulkLoadToStorage,
  USE_BULK_LOAD_STORAGE_KEY,
} from '@/shell/dialogs/tabs/devFixtureFormReducer';

/**
 * Component-level tests for the standalone DevFixturePanel — covers the cold states (probing, not-available)
 * the editable destination-folder behaviour (override → localStorage round-trip → effective-path preview),
 * the database-name validation surface, and the start-generation request shape (asserts the body the panel
 * sends to `/api/fixtures/create`).
 *
 * The customFetch import is mocked so the panel sees a controllable capability + create response without
 * spinning up Kestrel. Polling is mocked at the hook level to keep the test deterministic.
 */

const customFetchMock = vi.fn();
vi.mock('@/api/client', () => ({
  customFetch: (...args: unknown[]) => customFetchMock(...args),
}));

// Mock the polling hook — the panel's start flow only cares about jobState transitions; we drive them by hand.
const useFixtureJobPollingMock = vi.fn();
const cancelFixtureJobMock = vi.fn();
vi.mock('@/shell/dialogs/tabs/useFixtureJobPolling', () => ({
  useFixtureJobPolling: (jobId: string | null) => useFixtureJobPollingMock(jobId),
  cancelFixtureJob: (jobId: string) => cancelFixtureJobMock(jobId),
}));

// Mock the session-opening hook + the close-session deletion. We don't want the panel to actually trigger
// session lifecycle effects in the test — the close-session test below asserts the delete call shape.
const deleteApiSessionsIdMock = vi.fn().mockResolvedValue({ status: 204 });
vi.mock('@/api/generated/sessions/sessions', () => ({
  usePostApiSessionsFile: () => ({
    mutateAsync: vi.fn().mockResolvedValue({ data: { sessionId: 'test', filePath: '/tmp/foo.typhon', state: 'Ready' } }),
    isPending: false,
  }),
  deleteApiSessionsId: (id: string) => deleteApiSessionsIdMock(id),
}));

import DevFixturePanel from '../DevFixturePanel';
import { useSessionStore } from '@/stores/useSessionStore';

const NO_PROPS = {} as IDockviewPanelProps;

function renderPanel(): ReturnType<typeof render> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <DevFixturePanel {...NO_PROPS} />
    </QueryClientProvider>,
  );
}

function mockCapabilityOk(): void {
  customFetchMock.mockImplementation(async (url: string) => {
    if (url === '/api/fixtures/capability') {
      return { data: { available: true, outputDirectory: '/srv/fixtures', defaultDatabaseName: 'base-tests' } };
    }
    if (url === '/api/fixtures/create') {
      return { data: { jobId: 'job-1' } };
    }
    throw new Error(`unexpected fetch: ${url}`);
  });
}

function mockCapability404(): void {
  customFetchMock.mockImplementation(async (url: string) => {
    if (url === '/api/fixtures/capability') {
      throw new Error('404 Not Found');
    }
    throw new Error(`unexpected fetch: ${url}`);
  });
}

beforeEach(() => {
  localStorage.clear();
  customFetchMock.mockReset();
  useFixtureJobPollingMock.mockReset();
  cancelFixtureJobMock.mockReset();
  deleteApiSessionsIdMock.mockClear();
  deleteApiSessionsIdMock.mockResolvedValue({ status: 204 });
  // Default: no job in flight.
  useFixtureJobPollingMock.mockReturnValue(null);
  // Reset session store to a no-session baseline — the panel's no-session gate would otherwise turn most
  // tests into "close session first" cold-state assertions instead of exercising the form behaviour.
  useSessionStore.getState().clearSession();
});

afterEach(() => {
  cleanup();
});

describe('DevFixturePanel — cold states', () => {
  it('shows "Checking capability…" while the probe is in flight', () => {
    customFetchMock.mockImplementation(() => new Promise(() => { /* never resolves */ }));
    renderPanel();
    expect(screen.getByText('Checking capability…')).toBeDefined();
  });

  it('shows "not available in this build" when the capability probe fails (Release build)', async () => {
    mockCapability404();
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText(/Dev Fixture is not available in this build/i)).toBeDefined();
    });
  });

  it('renders the full form once the capability probe succeeds', async () => {
    mockCapabilityOk();
    renderPanel();
    // Wait for the form to render — the preset row is a stable post-probe landmark.
    await waitFor(() => expect(screen.getByTestId('devfixture-preset-default')).toBeDefined());
    expect(screen.getByTestId('devfixture-dbname')).toBeDefined();
    expect(screen.getByTestId('devfixture-outputdir')).toBeDefined();
    expect(screen.getByTestId('devfixture-start')).toBeDefined();
  });
});

describe('DevFixturePanel — no-session gate', () => {
  it('shows "close session first" cold state when a session is open', async () => {
    mockCapabilityOk();
    // Simulate an open session — the panel must refuse to render the form and show the gate instead.
    useSessionStore.setState({ kind: 'open', sessionId: 'session-1', filePath: '/db/existing.typhon' });
    renderPanel();
    // Wait for the capability probe to land — the gate-cold-state UI mounts after `capabilityProbed`.
    await waitFor(() => expect(screen.getByText(/A session is currently open/i)).toBeDefined());
    expect(screen.getByText(/Close the current session before generating a new fixture/i)).toBeDefined();
    expect(screen.getByTestId('devfixture-close-session')).toBeDefined();
    // The form MUST NOT be rendered — no preset buttons, no dbname input, no Start.
    expect(screen.queryByTestId('devfixture-preset-default')).toBeNull();
    expect(screen.queryByTestId('devfixture-dbname')).toBeNull();
    expect(screen.queryByTestId('devfixture-start')).toBeNull();
  });

  it('"Close session" button calls deleteApiSessionsId with the active sessionId and clears the store', async () => {
    mockCapabilityOk();
    useSessionStore.setState({ kind: 'open', sessionId: 'session-42', filePath: '/db/x.typhon' });
    renderPanel();
    const closeBtn = await waitFor(() => screen.getByTestId('devfixture-close-session') as HTMLButtonElement);
    fireEvent.click(closeBtn);
    await waitFor(() => expect(deleteApiSessionsIdMock).toHaveBeenCalledWith('session-42'));
    // After the delete resolves, the store is cleared — sessionKind goes back to 'none'.
    await waitFor(() => expect(useSessionStore.getState().kind).toBe('none'));
  });

  it('still clears the local session store when the server delete fails', async () => {
    // Resilience: a server-side error during close must NOT leave the client stuck on the cold-state screen.
    // The handler force-clears locally regardless, so the user can proceed to generate.
    mockCapabilityOk();
    deleteApiSessionsIdMock.mockRejectedValueOnce(new Error('boom'));
    useSessionStore.setState({ kind: 'open', sessionId: 'session-err', filePath: '/db/x.typhon' });
    renderPanel();
    const closeBtn = await waitFor(() => screen.getByTestId('devfixture-close-session') as HTMLButtonElement);
    fireEvent.click(closeBtn);
    await waitFor(() => expect(useSessionStore.getState().kind).toBe('none'));
  });

  it('flips back to the form after a successful close (cold state → form)', async () => {
    mockCapabilityOk();
    useSessionStore.setState({ kind: 'open', sessionId: 'session-1', filePath: '/db/x.typhon' });
    renderPanel();
    await waitFor(() => expect(screen.getByTestId('devfixture-close-session')).toBeDefined());
    fireEvent.click(screen.getByTestId('devfixture-close-session'));
    await waitFor(() => expect(useSessionStore.getState().kind).toBe('none'));
    // Form now appears.
    await waitFor(() => expect(screen.getByTestId('devfixture-preset-default')).toBeDefined());
  });
});

describe('DevFixturePanel — destination folder', () => {
  it('shows the server default in the effective-path readout when no override is set', async () => {
    mockCapabilityOk();
    renderPanel();
    await waitFor(() => expect(screen.getByTestId('devfixture-dbname')).toBeDefined());
    // Default name is 'base-tests' from the mocked capability; effective path = {root}/{name}.
    const path = screen.getByTestId('devfixture-effective-path');
    expect(path.textContent).toBe('/srv/fixtures/base-tests');
  });

  it('uses the user override verbatim in the effective-path readout', async () => {
    mockCapabilityOk();
    renderPanel();
    await waitFor(() => expect(screen.getByTestId('devfixture-outputdir')).toBeDefined());
    const input = screen.getByTestId('devfixture-outputdir') as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'D:\\scratch\\my-fixture' } });
    const path = screen.getByTestId('devfixture-effective-path');
    expect(path.textContent).toBe('D:\\scratch\\my-fixture');
  });

  it('persists the override to localStorage and the reset button clears it', async () => {
    mockCapabilityOk();
    renderPanel();
    await waitFor(() => expect(screen.getByTestId('devfixture-outputdir')).toBeDefined());
    const input = screen.getByTestId('devfixture-outputdir') as HTMLInputElement;
    fireEvent.change(input, { target: { value: '/custom/path' } });
    expect(localStorage.getItem(OUTPUT_DIR_STORAGE_KEY)).toBe('/custom/path');

    // Reset button removes the localStorage entry.
    const reset = screen.getByTestId('devfixture-outputdir-reset');
    fireEvent.click(reset);
    expect(localStorage.getItem(OUTPUT_DIR_STORAGE_KEY)).toBeNull();
    expect((screen.getByTestId('devfixture-outputdir') as HTMLInputElement).value).toBe('');
  });

  it('restores the user override on remount (panel re-open)', async () => {
    saveOutputDirToStorage('/prefilled/from/storage');
    mockCapabilityOk();
    renderPanel();
    await waitFor(() => expect(screen.getByTestId('devfixture-outputdir')).toBeDefined());
    const input = screen.getByTestId('devfixture-outputdir') as HTMLInputElement;
    expect(input.value).toBe('/prefilled/from/storage');
  });
});

describe('DevFixturePanel — start-generation request', () => {
  it('sends config + databaseName WITHOUT outputDirectory when no override is set', async () => {
    mockCapabilityOk();
    renderPanel();
    await waitFor(() => expect(screen.getByTestId('devfixture-start')).toBeDefined());
    fireEvent.click(screen.getByTestId('devfixture-start'));
    await waitFor(() => {
      expect(customFetchMock).toHaveBeenCalledWith('/api/fixtures/create', expect.objectContaining({ method: 'POST' }));
    });
    const createCall = customFetchMock.mock.calls.find(([url]) => url === '/api/fixtures/create');
    expect(createCall).toBeDefined();
    const body = JSON.parse((createCall![1] as { body: string }).body);
    expect(body).toMatchObject({ force: false, databaseName: 'base-tests' });
    expect(body.outputDirectory).toBeUndefined();
    expect(body.config).toBeDefined();
  });

  it('includes outputDirectory in the request body when the user supplied an override', async () => {
    mockCapabilityOk();
    renderPanel();
    await waitFor(() => expect(screen.getByTestId('devfixture-outputdir')).toBeDefined());
    const input = screen.getByTestId('devfixture-outputdir') as HTMLInputElement;
    fireEvent.change(input, { target: { value: '/custom/path' } });
    fireEvent.click(screen.getByTestId('devfixture-start'));
    await waitFor(() => {
      expect(customFetchMock).toHaveBeenCalledWith('/api/fixtures/create', expect.objectContaining({ method: 'POST' }));
    });
    const createCall = customFetchMock.mock.calls.find(([url]) => url === '/api/fixtures/create');
    const body = JSON.parse((createCall![1] as { body: string }).body);
    expect(body.outputDirectory).toBe('/custom/path');
  });

  it('trims whitespace from the override before sending', async () => {
    mockCapabilityOk();
    renderPanel();
    await waitFor(() => expect(screen.getByTestId('devfixture-outputdir')).toBeDefined());
    const input = screen.getByTestId('devfixture-outputdir') as HTMLInputElement;
    fireEvent.change(input, { target: { value: '   /padded/path   ' } });
    fireEvent.click(screen.getByTestId('devfixture-start'));
    await waitFor(() => {
      expect(customFetchMock).toHaveBeenCalledWith('/api/fixtures/create', expect.objectContaining({ method: 'POST' }));
    });
    const createCall = customFetchMock.mock.calls.find(([url]) => url === '/api/fixtures/create');
    const body = JSON.parse((createCall![1] as { body: string }).body);
    expect(body.outputDirectory).toBe('/padded/path');
  });

  it('omits an all-whitespace override (treated as empty)', async () => {
    mockCapabilityOk();
    renderPanel();
    await waitFor(() => expect(screen.getByTestId('devfixture-outputdir')).toBeDefined());
    const input = screen.getByTestId('devfixture-outputdir') as HTMLInputElement;
    fireEvent.change(input, { target: { value: '   ' } });
    fireEvent.click(screen.getByTestId('devfixture-start'));
    await waitFor(() => {
      expect(customFetchMock).toHaveBeenCalledWith('/api/fixtures/create', expect.objectContaining({ method: 'POST' }));
    });
    const createCall = customFetchMock.mock.calls.find(([url]) => url === '/api/fixtures/create');
    const body = JSON.parse((createCall![1] as { body: string }).body);
    expect(body.outputDirectory).toBeUndefined();
  });
});

describe('DevFixturePanel — start button gating', () => {
  it('disables Generate when the database name is invalid', async () => {
    mockCapabilityOk();
    renderPanel();
    await waitFor(() => expect(screen.getByTestId('devfixture-dbname')).toBeDefined());
    const dbInput = screen.getByTestId('devfixture-dbname') as HTMLInputElement;
    fireEvent.change(dbInput, { target: { value: 'has spaces' } });
    const start = screen.getByTestId('devfixture-start') as HTMLButtonElement;
    expect(start.disabled).toBe(true);
  });

  it('enables Generate for a valid name + clean form', async () => {
    mockCapabilityOk();
    renderPanel();
    await waitFor(() => expect(screen.getByTestId('devfixture-dbname')).toBeDefined());
    const start = screen.getByTestId('devfixture-start') as HTMLButtonElement;
    expect(start.disabled).toBe(false);
  });
});

describe('DevFixturePanel — database name + bulk mode persistence', () => {
  it('persists the database name to localStorage on edit', async () => {
    mockCapabilityOk();
    renderPanel();
    await waitFor(() => expect(screen.getByTestId('devfixture-dbname')).toBeDefined());
    fireEvent.change(screen.getByTestId('devfixture-dbname'), { target: { value: 'my-db' } });
    expect(localStorage.getItem(DATABASE_NAME_STORAGE_KEY)).toBe('my-db');
  });

  it('restores the database name on remount (panel re-open)', async () => {
    saveDatabaseNameToStorage('restored-db');
    mockCapabilityOk();
    renderPanel();
    await waitFor(() => expect(screen.getByTestId('devfixture-dbname')).toBeDefined());
    expect((screen.getByTestId('devfixture-dbname') as HTMLInputElement).value).toBe('restored-db');
  });

  it('persists the bulk-mode toggle to localStorage on change', async () => {
    mockCapabilityOk();
    renderPanel();
    await waitFor(() => expect(screen.getByTestId('devfixture-use-bulkload')).toBeDefined());
    const cb = screen.getByTestId('devfixture-use-bulkload') as HTMLInputElement;
    expect(cb.checked).toBe(false); // default preset (9310 entities) is under the bulk threshold
    fireEvent.click(cb);
    expect(localStorage.getItem(USE_BULK_LOAD_STORAGE_KEY)).toBe('true');
  });

  it('restores the bulk-mode toggle on remount (panel re-open)', async () => {
    saveUseBulkLoadToStorage(true);
    mockCapabilityOk();
    renderPanel();
    await waitFor(() => expect(screen.getByTestId('devfixture-use-bulkload')).toBeDefined());
    expect((screen.getByTestId('devfixture-use-bulkload') as HTMLInputElement).checked).toBe(true);
  });
});

describe('loadOutputDirFromStorage / saveOutputDirToStorage', () => {
  it('round-trips a path through localStorage', () => {
    saveOutputDirToStorage('/some/path');
    expect(loadOutputDirFromStorage()).toBe('/some/path');
  });

  it('clearing with empty string removes the key', () => {
    saveOutputDirToStorage('/some/path');
    saveOutputDirToStorage('');
    expect(loadOutputDirFromStorage()).toBeNull();
    expect(localStorage.getItem(OUTPUT_DIR_STORAGE_KEY)).toBeNull();
  });

  it('returns null when nothing is stored', () => {
    expect(loadOutputDirFromStorage()).toBeNull();
  });
});
