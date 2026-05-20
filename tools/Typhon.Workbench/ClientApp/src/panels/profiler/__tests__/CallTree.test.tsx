// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { CallTreeResponse } from '@/hooks/profiler/useCallTree';
import { spanKindScope, type CallTreeScope } from '@/stores/useCallTreeScopeStore';

/**
 * Component tests for the Call Tree panel. Covers the §8.7 surface (#364) — the view-mode toggle label and the
 * involuntary-stall aggregate node — and the unified breadcrumb: a scope command and a drill both push a crumb,
 * and navigating to the root crumb (or the chip ×) drops the scope. The data/store layer is mocked.
 */

// ─── mock the data + store layer the panel pulls from ────────────────────────────────────────────

let mockData: CallTreeResponse | null = null;
let mockScope: CallTreeScope | null = null;
let mockOwner: string | null = null;

vi.mock('@/hooks/profiler/useCallTree', async (importActual) => ({
  ...(await importActual<typeof import('@/hooks/profiler/useCallTree')>()),
  useCallTree: () => ({ data: mockData, isError: false, error: null }),
}));
vi.mock('@/hooks/profiler/useCpuFrameManifest', () => ({ useCpuFrameManifest: () => undefined }));
vi.mock('@/hooks/profiler/useSampleDensity', () => ({
  useSampleDensity: () => ({ data: null, isError: false }),
}));
vi.mock('@/shell/commands/openSchemaBrowser', () => ({
  openSourcePreview: vi.fn(),
  updateSourcePreviewIfOpen: vi.fn(),
}));
vi.mock('@/stores/useSessionStore', () => ({
  useSessionStore: (sel: (s: unknown) => unknown) => sel({ sessionId: 'session-1', kind: 'trace', token: 'tok' }),
}));
vi.mock('@/stores/useCpuFrameStore', () => ({
  useCpuFrameStore: (sel: (s: unknown) => unknown) => sel({ byId: new Map(), categoryName: new Map() }),
}));
vi.mock('@/stores/useOptionsStore', () => ({
  useOptionsStore: (sel: (s: unknown) => unknown) => sel({ openInEditor: vi.fn() }),
}));
vi.mock('@/stores/useProfilerSessionStore', () => ({
  useProfilerSessionStore: (sel: (s: unknown) => unknown) => sel({ metadata: null }),
}));
vi.mock('@/stores/useCallTreeScopeStore', async (importActual) => {
  const actual = await importActual<typeof import('@/stores/useCallTreeScopeStore')>();
  return {
    ...actual,
    useCallTreeScopeStore: (sel: (s: unknown) => unknown) =>
      sel({ scope: mockScope ?? actual.WHOLE_SESSION_SCOPE, ownerSessionId: mockOwner, setScope: vi.fn(), reset: vi.fn() }),
  };
});

const { default: CallTree } = await import('@/panels/profiler/CallTree');

/** A folded tree: synthetic root → one real method frame + one `[GC suspension]` involuntary aggregate. */
function treeWith(classificationAvailable: boolean): CallTreeResponse {
  return {
    nodes: [
      { frameId: -1, selfSamples: 0, totalSamples: 8, children: [1, 2] },
      { frameId: 5, selfSamples: 5, totalSamples: 5, children: [] },
      { frameId: -2, selfSamples: 3, totalSamples: 3, children: [] }, // [GC suspension]
    ],
    totalSamples: 8,
    managedSamples: 8,
    externalSamples: 0,
    categoryBreakdown: [],
    classificationAvailable,
  };
}

beforeEach(() => {
  mockData = null;
  mockScope = null;
  mockOwner = null;
});
afterEach(cleanup);

describe('CallTree — §8.7 view-mode label', () => {
  it('labels the first view "On-CPU" when classification data is present', () => {
    mockData = treeWith(true);
    render(<CallTree />);
    expect(screen.getByRole('button', { name: 'On-CPU' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Thread time' })).toBeNull();
  });

  it('labels the first view "Thread time" when classification data is absent', () => {
    mockData = treeWith(false);
    render(<CallTree />);
    expect(screen.getByRole('button', { name: 'Thread time' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'On-CPU' })).toBeNull();
  });
});

describe('CallTree — §8.7 involuntary-stall aggregate node', () => {
  it('renders a frameId<-1 node as a labelled aggregate row', () => {
    mockData = treeWith(true);
    render(<CallTree />);
    expect(screen.getByText('[GC suspension]')).toBeTruthy();
  });

  it('renders the aggregate with no expand/collapse control', () => {
    mockData = treeWith(true);
    render(<CallTree />);
    const label = screen.getByText('[GC suspension]');
    // The aggregate row carries no chevron button — only the static label cell + the metric cells.
    const row = label.closest('div');
    expect(row?.querySelector('button')).toBeNull();
  });
});

describe('CallTree — unified breadcrumb navigation', () => {
  /** Renders the panel with a cross-panel `Cluster.Migration` span-kind scope already commanded. */
  function renderScoped() {
    mockData = treeWith(true);
    mockOwner = 'session-1'; // matches the mocked useSessionStore sessionId
    mockScope = spanKindScope(5, 'Cluster.Migration');
    render(<CallTree />);
  }

  it('a cross-panel scope command pushes a breadcrumb crumb and shows the scope chip', () => {
    renderScoped();
    // Root crumb is a clickable "All"; the scope shows both as the current crumb and as the chip.
    expect(screen.getByRole('button', { name: 'All' })).toBeTruthy();
    expect(screen.getAllByText('Span kind: Cluster.Migration').length).toBeGreaterThanOrEqual(2);
  });

  it('clicking the breadcrumb root crumb drops the scope chip and the breadcrumb', () => {
    renderScoped();
    fireEvent.click(screen.getByRole('button', { name: 'All' }));
    expect(screen.queryAllByText('Span kind: Cluster.Migration')).toHaveLength(0);
    expect(screen.queryByRole('button', { name: 'All' })).toBeNull();
  });

  it('the scope chip × also returns to whole session', () => {
    renderScoped();
    fireEvent.click(screen.getByRole('button', { name: 'Clear scope' }));
    expect(screen.queryAllByText('Span kind: Cluster.Migration')).toHaveLength(0);
    expect(screen.queryByRole('button', { name: 'All' })).toBeNull();
  });
});
