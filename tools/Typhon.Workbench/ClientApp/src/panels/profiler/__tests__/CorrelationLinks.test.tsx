// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { RequestRow } from '@/libs/profiler/correlation/requests';
import { buildSpan } from '@/libs/profiler/model/__tests__/spanBuilder';
import type { SpanData } from '@/libs/profiler/model/traceModel';
import { CorrelationLinks, RequestDetail } from '@/panels/profiler/CorrelationLinks';
import { useProfilerRequestsStore } from '@/stores/useProfilerRequestsStore';

/** The Details pane's correlation section (#1517): the root a span served, and links to the other spans that root caused. */

const KEY = '11:22';
const span = (name: string, startUs: number, slot = 1): SpanData =>
  ({ ...buildSpan({ startUs, endUs: startUs + 50, threadSlot: slot }), name, traceIdHi: '11', traceIdLo: '22' });
const root: RequestRow = {
  id: 'http:0:1', key: KEY, kind: 'http', label: 'GET /swg/inventory', status: 200, startUs: 1_000, endUs: 2_000, totalUs: 1_000,
  spanCount: 2, engineUs: 100, markers: [],
};

beforeEach(() => {
  useProfilerRequestsStore.getState().clear();
});
afterEach(cleanup);

describe('CorrelationLinks', () => {
  it('names the root, what it cost, and links every span it caused', () => {
    const a = span('BTree.Insert', 1_100);
    const b = span('Ecs.Query', 1_400, 3);
    useProfilerRequestsStore.setState({ allRows: [root], related: { key: KEY, spans: [a, b] } });
    render(<CorrelationLinks span={a} causalKey={KEY} />);

    expect(screen.getByText('GET /swg/inventory 200')).toBeTruthy();
    expect(screen.getByText('Related spans (2)')).toBeTruthy();
    expect(screen.getAllByTestId('correlation-link').map((el) => el.textContent)).toEqual([
      expect.stringContaining('BTree.Insert'), expect.stringContaining('Ecs.Query'),
    ]);
  });

  it('a link asks the time area to select its span and bring it into view', () => {
    const a = span('BTree.Insert', 1_100);
    const b = span('Ecs.Query', 1_400, 3);
    useProfilerRequestsStore.setState({ allRows: [root], related: { key: KEY, spans: [a, b] } });
    render(<CorrelationLinks span={a} causalKey={KEY} />);

    fireEvent.click(screen.getAllByTestId('correlation-link')[1]);

    // The time area selects it, after recording the view it leaves in the navigation history.
    expect(useProfilerRequestsStore.getState().nav).toMatchObject({ kind: 'span', span: b });
  });

  it('pins and unpins the root flow', () => {
    const a = span('BTree.Insert', 1_100);
    useProfilerRequestsStore.setState({ allRows: [root], related: { key: KEY, spans: [a] } });
    render(<CorrelationLinks span={a} causalKey={KEY} />);

    fireEvent.click(screen.getByTestId('correlation-pin'));
    expect(useProfilerRequestsStore.getState().selectedKey).toBe(KEY);
    fireEvent.click(screen.getByTestId('correlation-pin'));
    expect(useProfilerRequestsStore.getState().selectedKey).toBeNull();
  });

  it('a request picked in a root lane shows its breakdown, its lifecycle steps and the spans it caused', () => {
    const cmdKey = '6077962839392321536:7733249';
    const caused = { ...span('Data.Transaction.Prepare', 1_300, 16), traceIdHi: '6077962839392321536', traceIdLo: '7733249' };
    const row: RequestRow = {
      id: `${cmdKey}:1100`, key: cmdKey, kind: 'command', label: 'command 31 → PlayerSessions', session: 7, startUs: 1_000, endUs: 1_600,
      totalUs: 600, spanCount: 1, engineUs: 50, ringWaitUs: 100, tickWaitUs: 150, sendWaitUs: 250,
      markers: [
        { slot: 5, us: 1_000, kind: 'received' }, { slot: 0, us: 1_100, kind: 'admitted' },
        { slot: 16, us: 1_250, kind: 'consumed' }, { slot: 7, us: 1_600, kind: 'sent' },
      ],
    };
    useProfilerRequestsStore.setState({ allRows: [row], related: { key: cmdKey, spans: [caused] } });
    render(<RequestDetail row={row} />);

    expect(screen.getByText('command 31 → PlayerSessions')).toBeTruthy();
    expect(screen.getAllByTestId('request-step').map((el) => el.textContent)).toEqual([
      expect.stringContaining('Received by the transport'), expect.stringContaining('Admitted to a tick'),
      expect.stringContaining('Read by a system'), expect.stringContaining('Answer frame sent'),
    ]);
    expect(screen.getByText('Caused spans (1)')).toBeTruthy();

    fireEvent.click(screen.getAllByTestId('request-step')[2]);
    expect(useProfilerRequestsStore.getState().nav).toMatchObject({ kind: 'target', target: { trackId: 'slot-16', startUs: 1_250 } });
  });

  it('says so when the root is outside the loaded ticks', () => {
    const a = span('BTree.Insert', 1_100);
    useProfilerRequestsStore.setState({ allRows: [], related: { key: KEY, spans: [a] } });
    render(<CorrelationLinks span={a} causalKey={KEY} />);
    expect(screen.getByText(/outside the loaded range/)).toBeTruthy();
  });
});
