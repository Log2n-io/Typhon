// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { QueryDefinitionDto } from '@/api/generated/model';
import { useSessionStore } from '@/stores/useSessionStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useQueryAnalyzerStore } from '../useQueryAnalyzerStore';
import { makeDef } from './fixtures';

const hoisted = vi.hoisted(() => ({ defs: [] as QueryDefinitionDto[] }));

vi.mock('@/panels/QueryAnalyzer/useQueryDefinitions', () => ({
  useQueryDefinitions: () => ({ definitions: hoisted.defs, isLoading: false, isError: false, error: null }),
}));
vi.mock('@/hooks/useProfilerNameMaps', () => ({
  useProfilerNameMaps: () => ({ archetypeNames: new Map<number, string>(), systemNames: new Map<number, string>() }),
}));

import QueryAnalyzerPanel from '../QueryAnalyzerPanel';

/**
 * The Query Analyzer used to gate on `kind === 'trace' || kind === 'attach'` and answer
 * "available in Trace and Attach sessions only" to everything else.
 *
 * That was wrong from the moment #617 let a capture attach TO an open database: such a session is `kind === 'open'`,
 * it has a full query catalog, and the panel refused it — on the very path F4 made the primary way to reach a capture.
 * The existing suite never caught it because every test set `kind: 'trace'`, which is the case that worked.
 *
 * These tests are written from the *capability* the panel actually needs, so the database-hosted session is a
 * first-class case rather than an afterthought.
 */
function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      {/* eslint-disable-next-line @typescript-eslint/no-explicit-any -- the panel ignores dockview params */}
      <QueryAnalyzerPanel {...({} as any)} />
    </QueryClientProvider>,
  );
}

const REFUSAL = /needs a capture/i;

beforeEach(() => {
  hoisted.defs = [makeDef({ localId: 1, target: 1, totalWallNs: 1000 })];
  useSessionStore.setState({ sessionId: 'sess-1', kind: 'open', capabilities: [] });
  useQueryAnalyzerStore.getState().reset();
  useSelectionStore.getState().clear();
});
afterEach(() => cleanup());

describe('QueryAnalyzerPanel — capability gate (#621 AC10)', () => {
  it('renders for an OPEN database with a capture attached — the case the kind check rejected', () => {
    useSessionStore.setState({ kind: 'open', capabilities: ['database', 'profiler'] });
    renderPanel();

    expect(screen.queryByText(REFUSAL)).toBeNull();
    expect(screen.getByTestId('query-analyzer')).toBeTruthy();
  });

  it('still renders for a live attach session', () => {
    useSessionStore.setState({ kind: 'attach', capabilities: ['profiler'] });
    renderPanel();

    expect(screen.queryByText(REFUSAL)).toBeNull();
    expect(screen.getByTestId('query-analyzer')).toBeTruthy();
  });

  it('refuses a plain database with no capture — there is no catalog to analyse', () => {
    useSessionStore.setState({ kind: 'open', capabilities: ['database'] });
    renderPanel();

    expect(screen.getByText(REFUSAL)).toBeTruthy();
    expect(screen.queryByTestId('query-analyzer')).toBeNull();
  });

  it('refuses a PAUSED session that never had a capture attached', () => {
    // Pausing drops `database` and keeps `profiler` only when a profile is attached. With none, there is nothing to
    // analyse and the panel must say so rather than render an empty analyzer.
    useSessionStore.setState({ kind: 'open', capabilities: [], isPaused: true });
    renderPanel();

    expect(screen.getByText(REFUSAL)).toBeTruthy();
  });

  it('renders for a PAUSED session that still has its capture — the dev-loop case', () => {
    // The database is released to the running application, but the capture is a file on disk and stays fully usable.
    // This is the whole point of pausing rather than closing.
    useSessionStore.setState({ kind: 'open', capabilities: ['profiler'], isPaused: true });
    renderPanel();

    expect(screen.queryByText(REFUSAL)).toBeNull();
    expect(screen.getByTestId('query-analyzer')).toBeTruthy();
  });
});
