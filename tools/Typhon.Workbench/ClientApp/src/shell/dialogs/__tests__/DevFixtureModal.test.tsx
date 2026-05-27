// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useDevFixtureModalStore } from '@/stores/useDevFixtureModalStore';
import { useSessionStore } from '@/stores/useSessionStore';

/**
 * Tests for the Dev Fixture modal fallback — the surface that lets the Welcome screen's "Dev Fixture"
 * button do something useful when no dockview is mounted. Before this fallback, clicking the button on
 * Welcome silently no-op'd (the dock-panel toggle's `registeredApi` check returned null), which is the
 * regression Loïc caught with Playwright.
 *
 * The auto-close on session-change behaviour is the second contract: once a session opens (whether the
 * fixture finished generating + auto-opened, or the user dismissed manually), the modal must close so
 * the dock can take over the main pane.
 */

const customFetchMock = vi.fn();
vi.mock('@/api/client', () => ({
  customFetch: (...args: unknown[]) => customFetchMock(...args),
}));

vi.mock('@/shell/dialogs/tabs/useFixtureJobPolling', () => ({
  useFixtureJobPolling: () => null,
  cancelFixtureJob: vi.fn(),
}));

const deleteApiSessionsIdMock = vi.fn().mockResolvedValue({ status: 204 });
vi.mock('@/api/generated/sessions/sessions', () => ({
  usePostApiSessionsFile: () => ({
    mutateAsync: vi.fn().mockResolvedValue({ data: { sessionId: 'test', filePath: '/tmp/foo.typhon', state: 'Ready' } }),
    isPending: false,
  }),
  deleteApiSessionsId: (id: string) => deleteApiSessionsIdMock(id),
}));

import DevFixtureModal from '../DevFixtureModal';

function renderModal(): ReturnType<typeof render> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <DevFixtureModal />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  customFetchMock.mockReset();
  customFetchMock.mockImplementation(async (url: string) => {
    if (url === '/api/fixtures/capability') {
      return { data: { available: true, outputDirectory: '/srv/fixtures', defaultDatabaseName: 'base-tests' } };
    }
    throw new Error(`unexpected fetch: ${url}`);
  });
  useDevFixtureModalStore.setState({ isOpen: false });
  useSessionStore.getState().clearSession();
});

afterEach(() => {
  cleanup();
});

describe('DevFixtureModal', () => {
  it('does NOT mount the dialog when the store is closed', () => {
    renderModal();
    expect(screen.queryByTestId('devfixture-modal')).toBeNull();
  });

  it('mounts the dialog when the store flag flips to open', async () => {
    renderModal();
    useDevFixtureModalStore.getState().open();
    await waitFor(() => expect(screen.getByTestId('devfixture-modal')).toBeDefined());
    // The dialog shows the panel body — when on Welcome (no session), that body renders the form once
    // the capability probe lands. The preset row is the stable post-probe landmark.
    await waitFor(() => expect(screen.getByTestId('devfixture-preset-default')).toBeDefined());
  });

  it('auto-closes when a session opens (mid-modal session creation)', async () => {
    renderModal();
    useDevFixtureModalStore.getState().open();
    await waitFor(() => expect(screen.getByTestId('devfixture-modal')).toBeDefined());

    // Simulate a session opening — e.g. the panel's handleOpenGenerated calling setSession(dto).
    useSessionStore.setState({ kind: 'open', sessionId: 'just-opened', filePath: '/db/x.typhon' });

    await waitFor(() => expect(useDevFixtureModalStore.getState().isOpen).toBe(false));
    await waitFor(() => expect(screen.queryByTestId('devfixture-modal')).toBeNull());
  });

  it('store actions cover open/close/toggle', () => {
    const s = useDevFixtureModalStore.getState();
    expect(s.isOpen).toBe(false);
    s.open();
    expect(useDevFixtureModalStore.getState().isOpen).toBe(true);
    s.close();
    expect(useDevFixtureModalStore.getState().isOpen).toBe(false);
    s.toggle();
    expect(useDevFixtureModalStore.getState().isOpen).toBe(true);
    s.toggle();
    expect(useDevFixtureModalStore.getState().isOpen).toBe(false);
  });
});
