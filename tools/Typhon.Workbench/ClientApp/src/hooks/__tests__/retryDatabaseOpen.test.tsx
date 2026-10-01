// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useCanRetryDatabaseOpen } from '@/hooks/useRetryDatabaseOpen';
import RetryOpenButton from '@/shell/components/RetryOpenButton';
import SchemaReopenPrompt from '@/panels/options/SchemaReopenPrompt';
import IncompatibleBanner from '@/shell/banners/IncompatibleBanner';
import { useSessionStore } from '@/stores/useSessionStore';

/**
 * Retrying the open after a schema fix (#1083 follow-up).
 *
 * **The gap.** Registering a schema directory wrote a setting and stopped: `PATCH /api/options/schema` normalises the
 * list and returns, nothing re-resolves the schema, and the blocked banner stayed up worded identically. The user did
 * the right thing and the app gave no sign of it — the only way forward was to close the database and open it again,
 * which nothing said. Schema is resolved at OPEN, so reopening is the whole mechanism.
 */

const openDatabaseFile = vi.fn<(filePath: string, dlls: string[]) => Promise<unknown>>();
vi.mock('@/hooks/useOpenDatabaseFile', () => ({
  useOpenDatabaseFile: () => ({ openDatabaseFile, mutation: {} }),
}));

function withClient(ui: React.ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{ui}</QueryClientProvider>;
}

function setSession(over: Record<string, unknown>) {
  useSessionStore.setState({
    kind: 'open',
    filePath: 'C:\\db\\world.typhon',
    sessionState: 'Incompatible',
    ...over,
  } as never);
}

/** Reads the predicate through a throwaway component, so no QueryClient is involved — which is the point of it. */
function CanRetryProbe() {
  return <span data-testid="can-retry">{String(useCanRetryDatabaseOpen())}</span>;
}

beforeEach(() => {
  openDatabaseFile.mockReset().mockResolvedValue({});
  setSession({});
  useSessionStore.setState({ schemaDiagnostics: [] } as never);
});

afterEach(cleanup);

describe('when a retry is on offer', () => {
  it('offers it for an open file session that is Incompatible or MigrationRequired', () => {
    for (const state of ['Incompatible', 'MigrationRequired']) {
      setSession({ sessionState: state });
      const { unmount } = render(<CanRetryProbe />);
      expect(screen.getByTestId('can-retry').textContent).toBe('true');
      unmount();
    }
  });

  /** A healthy database has nothing to retry, and offering it would imply something is wrong. */
  it('does not offer it for a session a reopen cannot improve', () => {
    for (const over of [
      { sessionState: 'Ready' },
      { sessionState: null },
      { kind: 'attach' }, // an attach session has no file to reopen
      { filePath: null },
      { kind: 'none', filePath: null, sessionState: null },
    ]) {
      setSession(over);
      const { unmount } = render(<CanRetryProbe />);
      expect(screen.getByTestId('can-retry').textContent).toBe('false');
      unmount();
    }
  });

  /**
   * **The predicate must not need a QueryClient**, which is why it is split from the retry itself: the blocked-state
   * banners decide whether to offer a retry, and five existing tests render them with no provider. Rendering the probe
   * outside `withClient` above is that assertion — it would throw "No QueryClient set" if the split regressed.
   */
  it('is readable without a QueryClient', () => {
    expect(() => render(<CanRetryProbe />)).not.toThrow();
  });
});

describe('the retry itself', () => {
  /**
   * **Re-resolves rather than re-uses.** The session DTO's `schemaDllPaths` is what the last open *resolved*, not what
   * was asked for; passing it back would pin the retry to the paths that just failed and skip the directory the user
   * just registered — the exact opposite of the intent.
   */
  it('reopens the same file with no explicit DLL list', async () => {
    render(withClient(<RetryOpenButton />));

    fireEvent.click(screen.getByRole('button', { name: /retry/i }));

    await vi.waitFor(() => expect(openDatabaseFile).toHaveBeenCalledTimes(1));
    expect(openDatabaseFile).toHaveBeenCalledWith('C:\\db\\world.typhon', []);
  });

  /** A reopen that fails for a NEW reason must not read as "nothing happened" — the very complaint this answers. */
  it('surfaces a failed reopen beside the button', async () => {
    openDatabaseFile.mockRejectedValue(new Error('file_locked'));
    render(withClient(<RetryOpenButton />));

    fireEvent.click(screen.getByRole('button', { name: /retry/i }));

    await vi.waitFor(() => expect(screen.getByText(/reopen failed/i)).toBeTruthy());
  });
});

describe('where the retry is offered', () => {
  it('the blocked banner carries it while the session is Incompatible', () => {
    render(withClient(<IncompatibleBanner />));

    expect(screen.getByRole('button', { name: /retry/i })).toBeTruthy();
  });

  /** Registering a directory now says what it did and what to do next, instead of looking like nothing happened. */
  it('the schema form confirms the registration and offers the reopen', () => {
    render(withClient(<SchemaReopenPrompt directory="C:\\build\\net10.0" />));

    expect(screen.getByText(/^Registered/)).toBeTruthy();
    expect(screen.getByRole('button', { name: /reopen the database/i })).toBeTruthy();
  });

  /** On a healthy session the prompt says nothing: the registration still applies to the next open. */
  it('the schema form stays silent when there is nothing to reopen', () => {
    setSession({ sessionState: 'Ready' });

    const { container } = render(withClient(<SchemaReopenPrompt directory="C:\\build\\net10.0" />));

    expect(container.textContent).toBe('');
  });
});
