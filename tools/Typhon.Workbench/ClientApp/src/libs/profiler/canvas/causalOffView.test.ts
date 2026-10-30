import { describe, expect, it } from 'vitest';
import { appendOffViewFlowPoints, type CausalHighlight } from '@/libs/profiler/canvas/timeArea';
import { buildLayout, SPAN_BAR_HEIGHT, SPAN_BAR_MARGIN, SPAN_ROW_HEIGHT } from '@/libs/profiler/canvas/timeAreaLayout';
import type { FlowPoint } from '@/libs/profiler/correlation/causalFlow';
import { indexSpansByCausal, type RequestRow } from '@/libs/profiler/correlation/requests';
import { packRootLanes, ROOT_COMMANDS_TRACK_ID } from '@/libs/profiler/correlation/rootLanes';
import { buildSpan, buildTick } from '@/libs/profiler/model/__tests__/spanBuilder';
import type { SpanData } from '@/libs/profiler/model/traceModel';

/** The flow reaches what is outside the view (#1517): a selected root's spans and its own bar get points placed where they would be drawn. */

const caused = (startUs: number, slot: number, renderDepth: number): SpanData =>
  ({ ...buildSpan({ startUs, endUs: startUs + 10, threadSlot: slot }), traceIdHi: '11', traceIdLo: '22', renderDepth });
const root: RequestRow = {
  id: 'cmd', key: '11:22', kind: 'command', label: 'command 24', startUs: 0, endUs: 500, totalUs: 500, spanCount: 2, engineUs: 20, markers: [],
};
const layout = (collapsed: Record<string, 'summary'> = {}) => buildLayout({
  activeSlots: [1, 2], slotsWithChunks: new Set([1]), spanMaxDepthBySlot: new Map([[1, 2], [2, 0]]), threadNames: null, collapseState: collapsed,
  gaugeRegionVisible: false, gaugeCollapse: {}, activeSystems: [], systemNames: null, perSystemLanesVisible: false, rootRows: { http: 0, commands: 1 },
}).tracks;
// 1 px per µs from x = 100: the view starts at 1 000 µs, so everything before it lies left of the canvas.
const pxOfUs = (us: number): number => 100 + (us - 1_000);

describe('appendOffViewFlowPoints', () => {
  const left = caused(200, 1, 2);
  const right = caused(400, 2, 0);
  const causal: CausalHighlight = { key: '11:22', hi: '11', lo: '22', markers: [], spans: [left, right], root };

  it('places the spans and the root the draw did not reach on their lane and row, off the canvas', () => {
    const tracks = layout();
    const points: FlowPoint[] = [];
    appendOffViewFlowPoints(points, causal, tracks, 0, pxOfUs, packRootLanes([root]));

    const slot1 = tracks.find((t) => t.id === 'slot-1')!;
    const commands = tracks.find((t) => t.id === ROOT_COMMANDS_TRACK_ID)!;
    const at = (p: FlowPoint) => [p.kind, p.x, p.y, p.nav.trackId];
    expect(points.map(at)).toEqual([
      ['span', -700, slot1.y + SPAN_ROW_HEIGHT + 2 * SPAN_ROW_HEIGHT + SPAN_BAR_MARGIN + SPAN_BAR_HEIGHT / 2, 'slot-1'], // under the chunk row, depth 2
      ['span', -500, tracks.find((t) => t.id === 'slot-2')!.y + SPAN_BAR_MARGIN + SPAN_BAR_HEIGHT / 2, 'slot-2'],
      ['root', -900, commands.y + SPAN_BAR_MARGIN + SPAN_BAR_HEIGHT / 2, ROOT_COMMANDS_TRACK_ID],
    ]);
    expect(points[2].root).toBe(true);
  });

  it('adds nothing the draw already placed, and nothing on a collapsed lane', () => {
    const drawn: FlowPoint = { ...({} as FlowPoint), span: left, kind: 'span', root: false };
    const rootDrawn: FlowPoint = { ...({} as FlowPoint), kind: 'root', root: true };
    const points = [drawn, rootDrawn];
    appendOffViewFlowPoints(points, causal, layout({ 'slot-2': 'summary' }), 0, pxOfUs, packRootLanes([root]));
    expect(points).toHaveLength(2);
  });

  it('follows the vertical scroll', () => {
    const tracks = layout();
    const a: FlowPoint[] = [];
    const b: FlowPoint[] = [];
    appendOffViewFlowPoints(a, causal, tracks, 0, pxOfUs, undefined);
    appendOffViewFlowPoints(b, causal, tracks, 40, pxOfUs, undefined);
    expect(b.map((p) => p.y)).toEqual(a.map((p) => p.y - 40));
    expect(b.map((p) => p.nav.rowOffset)).toEqual(a.map((p) => p.nav.rowOffset));
  });
});

describe('indexSpansByCausal', () => {
  it('lists each key\'s spans across ticks in start order, and skips spans without one', () => {
    const late = caused(900, 1, 0);
    const early = caused(100, 2, 0);
    const other = { ...caused(500, 1, 0), traceIdLo: '99' };
    const plain = buildSpan({ startUs: 50, endUs: 60, threadSlot: 1 });
    const index = indexSpansByCausal([
      buildTick({ tickNumber: 1, startUs: 0, durationUs: 600, spans: [plain, other, early] }),
      buildTick({ tickNumber: 2, startUs: 600, durationUs: 600, spans: [late] }),
    ]);
    expect([...index.keys()].sort()).toEqual(['11:22', '11:99']);
    expect(index.get('11:22')).toEqual([early, late]);
  });
});
