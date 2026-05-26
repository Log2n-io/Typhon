// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { CallTreeResponse } from '@/hooks/profiler/useCallTree';
import { spanKindScope, type CallTreeScope } from '@/stores/useCallTreeScopeStore';
import { useCallTreePrefsStore } from '@/stores/useCallTreePrefsStore';

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
  // The Call Tree's viewMode/direction/groupByCategory now live in a persisted prefs store (AC3.16); reset to
  // defaults so a prior test that set the direction to 'sandwich' doesn't leak into the next render.
  useCallTreePrefsStore.setState({ viewMode: 'wall-clock', direction: 'top-down', groupByCategory: false });
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

describe('CallTree — selected-row focus styling', () => {
  it('a clicked row gets the wb-tree-selected hook (its colour is focus-dependent via CSS)', () => {
    mockData = treeWith(true);
    render(<CallTree />);
    // The row div wraps the per-row crosshair button; clicking the row selects it.
    const row = screen.getByRole('button', { name: 'Focus the call tree on #5' }).closest('div');
    expect(row).toBeTruthy();
    expect(row!.className).not.toContain('wb-tree-selected');
    fireEvent.click(row!);
    // Selection no longer hard-codes bg-primary/30 — it tags the row so `.dv-active-group .wb-tree-selected`
    // (focused) vs `.wb-tree-selected` (grey, unfocused) can branch on pane focus.
    expect(row!.className).toContain('wb-tree-selected');
  });
});

describe('CallTree — sandwich row context menu', () => {
  /** Drill into the only real frame (so sandwich has a focus), then switch to the Sandwich direction. */
  function renderSandwich() {
    mockData = treeWith(true);
    render(<CallTree />);
    // Drill in the top-down view — the crosshair re-roots the tree at frame #5, giving the sandwich a focus.
    fireEvent.click(screen.getByRole('button', { name: 'Focus the call tree on #5' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sandwich' }));
  }

  it('right-clicking a sandwich row opens the same Call Tree context menu', () => {
    renderSandwich();
    // Both panes (callers + callees) now render the frame row, so there are two focus crosshairs.
    const crosshairs = screen.getAllByRole('button', { name: 'Focus the call tree on #5' });
    expect(crosshairs.length).toBe(2);
    const row = crosshairs[0].closest('div');
    expect(row).toBeTruthy();
    fireEvent.contextMenu(row!);
    expect(screen.getByTestId('call-tree-context-menu')).toBeTruthy();
    // The reused menu carries the drill verb — proof it's the full menu, not a stub.
    expect(screen.getByText('Focus tree on this frame')).toBeTruthy();
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
