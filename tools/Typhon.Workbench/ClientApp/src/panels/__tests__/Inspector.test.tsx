// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import DetailPanel from '@/panels/DetailPanel';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useDbMapStore } from '@/stores/useDbMapStore';
import type { SelectedResource } from '@/stores/useSelectedResourceStore';

// Component/archetype leaf cards fetch a summary via TanStack hooks (disabled without a sessionId → empty),
// so the Inspector needs a QueryClient in this harness.
function renderInspector() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <DetailPanel />
    </QueryClientProvider>,
  );
}

// The Inspector dispatches off the unified bus leaf (Stage 1). These cover the no-fetch leaf types +
// the empty state + the PC-6 affordance audit; the data-fetching cards (field/entity/profiler/dbmap) are
// covered by the load-a-file slice E2E.

const sampleResource: SelectedResource = {
  resourceId: 'r-1',
  kind: 'ComponentTable',
  name: 'ComponentTable_Position',
  path: ['Storage', 'ComponentTable_Position'],
  raw: { id: 'r-1' } as SelectedResource['raw'],
};

beforeEach(() => {
  useSelectionStore.getState().clear();
  // An Open session so the profiler range-stats fallback doesn't pre-empt the empty prompt.
  useSessionStore.setState({ kind: 'open' });
});
afterEach(cleanup);

describe('Inspector — bus-driven dispatch', () => {
  it('renders the empty prompt when nothing is selected', () => {
    renderInspector();
    expect(screen.getByText(/select anything/i)).toBeTruthy();
  });

  it('renders the Resource card for a resource leaf', () => {
    useSelectionStore.getState().select('resource', sampleResource);
    renderInspector();
    expect(screen.getByText('ComponentTable_Position')).toBeTruthy();
    expect(screen.getByText(/Storage \/ ComponentTable_Position/)).toBeTruthy();
  });

  it('renders the component summary card with a live "Open in Component Inspector" verb (Stage 2)', () => {
    useSelectionStore.getState().select('component', 'Position');
    renderInspector();
    expect(screen.getByText('Position')).toBeTruthy();
    const open = screen.getByRole('button', { name: /open in component inspector/i });
    expect((open as HTMLButtonElement).disabled).toBe(false); // a real verb now, not a gated placeholder
  });

  it('renders the archetype summary card with a live "Open in Archetype Inspector" verb (Stage 2)', () => {
    useSelectionStore.getState().select('archetype', '800');
    renderInspector();
    expect(screen.getByText('#800')).toBeTruthy();
    expect(screen.getByRole('button', { name: /open in archetype inspector/i })).toBeTruthy();
  });

  it('renders a gated summary card for a system leaf', () => {
    useSelectionStore.getState().select('system', 'Movement');
    renderInspector();
    expect(screen.getByText('Movement')).toBeTruthy();
  });

  it('renders the owning-segment ancestor section for a file-map page leaf (IA §2.5)', () => {
    useSelectionStore.getState().select('page', { kind: 'page', pageIndex: 5, segmentId: 3 });
    renderInspector();
    expect(screen.getByTestId('inspector-ancestor-segment')).toBeTruthy();
  });

  it('a free page leaf (no owner) shows no segment ancestor section — not a bogus parent', () => {
    useSelectionStore.getState().select('page', { kind: 'page', pageIndex: 9 });
    renderInspector();
    expect(screen.queryByTestId('inspector-ancestor-segment')).toBeNull();
  });

  it('a chunk leaf renders the SAME full page + segment cards as ancestors (not a reduced summary)', () => {
    useSelectionStore.getState().select('chunk', { kind: 'chunk', pageIndex: 101, segmentId: 31, chunkId: 11 });
    renderInspector();
    const seg = screen.getByTestId('inspector-ancestor-segment');
    const page = screen.getByTestId('inspector-ancestor-page');
    // The ancestor renders the identical leaf card, identified by its card title — same UI as selecting it directly.
    expect(within(seg).getByText(/Segment #31/)).toBeTruthy();
    expect(within(page).getByText('Page 101')).toBeTruthy();
  });

  it('a cell leaf renders a FULL chunk ancestor card (not an empty section), plus page + segment', () => {
    useSelectionStore.getState().select('cell', { kind: 'cell', pageIndex: 101, segmentId: 31, chunkId: 11, cellOffset: 64 });
    renderInspector();
    const chunk = screen.getByTestId('inspector-ancestor-chunk');
    expect(screen.getByTestId('inspector-ancestor-page')).toBeTruthy();
    expect(screen.getByTestId('inspector-ancestor-segment')).toBeTruthy();
    // The chunk ancestor renders the full DbMapChunkDetail body (its loading shell), not an empty summary stub.
    expect(within(chunk).getByText(/decoding chunk/i)).toBeTruthy();
  });

  it('orders a cell leaf chain finest-first: chunk precedes page precedes segment in the DOM', () => {
    useSelectionStore.getState().select('cell', { kind: 'cell', pageIndex: 101, segmentId: 31, chunkId: 11, cellOffset: 64 });
    renderInspector();
    const chunk = screen.getByTestId('inspector-ancestor-chunk');
    const page = screen.getByTestId('inspector-ancestor-page');
    const seg = screen.getByTestId('inspector-ancestor-segment');
    expect(chunk.compareDocumentPosition(page) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(page.compareDocumentPosition(seg) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('orders sections finest-first: page (finer) precedes segment (coarser) in the DOM', () => {
    useSelectionStore.getState().select('chunk', { kind: 'chunk', pageIndex: 101, segmentId: 31, chunkId: 11 });
    renderInspector();
    const page = screen.getByTestId('inspector-ancestor-page');
    const seg = screen.getByTestId('inspector-ancestor-segment');
    // page appears before segment → DOCUMENT_POSITION_FOLLOWING set on the comparison from page → seg.
    expect(page.compareDocumentPosition(seg) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('a component leaf card offers Reveal in File Map (AC2.14)', () => {
    useSelectionStore.getState().select('component', 'Position');
    renderInspector();
    expect(screen.getByTestId('leaf-reveal-file-map')).toBeTruthy();
  });

  it('a component-table segment leaf carries the Schema / File Map / Resource handoffs (AC2.14)', () => {
    useSelectionStore.getState().select('segment', { kind: 'segment', segmentId: 9, typeName: 'Position' });
    renderInspector();
    expect(screen.getByTestId('segment-open-schema')).toBeTruthy();
    expect(screen.getByTestId('segment-reveal-file-map')).toBeTruthy();
    expect(screen.getByTestId('segment-reveal-resource')).toBeTruthy();
  });

  it('a segment handoff resolves end-to-end — Reveal in File Map requests its focus (AC2.14)', () => {
    useDbMapStore.getState().clearPendingFocus();
    useSelectionStore.getState().select('segment', { kind: 'segment', segmentId: 9, typeName: 'Position' });
    renderInspector();
    fireEvent.click(screen.getByTestId('segment-reveal-file-map'));
    expect(useDbMapStore.getState().pendingFocusType).toBe('Position');
  });

  it('a non-component segment leaf shows no handoff verbs — no dead affordance (PC-6)', () => {
    useSelectionStore.getState().select('segment', { kind: 'segment', segmentId: 9 });
    renderInspector();
    expect(screen.queryByTestId('segment-open-schema')).toBeNull();
  });

  it('exposes no broken affordance (PC-6 / suite E): no disabled Open in / Reveal in / Go to control', () => {
    useSelectionStore.getState().select('resource', sampleResource);
    const { container } = renderInspector();
    const dead = Array.from(container.querySelectorAll('button, [role="button"]')).filter((el) => {
      const disabled = (el as HTMLButtonElement).disabled || el.getAttribute('aria-disabled') === 'true';
      return disabled && /\b(open in|reveal in|go to)\b/i.test(el.textContent ?? '');
    });
    expect(dead).toEqual([]);
  });
});
