import { describe, expect, it } from 'vitest';
import { buildHoverTooltipLines } from '@/libs/profiler/canvas/hoverTooltipLines';
import { buildLayout, SPAN_ROW_HEIGHT } from '@/libs/profiler/canvas/timeAreaLayout';
import { hitTestTimeArea } from '@/libs/profiler/canvas/timeAreaHitTest';
import type { RequestRow } from '@/libs/profiler/correlation/requests';
import {
  findRootAt, firstRootEndingAfter, MAX_ROOT_ROWS, packRootLanes, ROOT_COMMANDS_TRACK_ID, ROOT_HTTP_TRACK_ID,
} from '@/libs/profiler/correlation/rootLanes';

type Extra = Partial<Omit<RequestRow, 'id' | 'markers'>>;

function root(key: string, kind: RequestRow['kind'], startUs: number, endUs: number, extra: Extra = {}): RequestRow {
  return { id: key, key, kind, label: key, startUs, endUs, totalUs: endUs - startUs, spanCount: 0, engineUs: 0, markers: [], ...extra };
}

const layoutBase = {
  activeSlots: [0],
  slotsWithChunks: new Set<number>(),
  spanMaxDepthBySlot: new Map<number, number>(),
  threadNames: null,
  collapseState: {},
  gaugeRegionVisible: false,
  gaugeCollapse: {},
  activeSystems: [],
  systemNames: null,
  perSystemLanesVisible: false,
};

describe('packRootLanes', () => {
  it('splits HTTP from commands and stacks only roots that overlap', () => {
    const lanes = packRootLanes([
      root('b', 'http', 50, 150),
      root('a', 'http', 0, 100),
      root('c', 'http', 100, 200), // starts as `a` ends: reuses row 0
      root('cmd', 'command', 10, 20),
    ]);
    expect(lanes.http.roots.map((p) => [p.row.key, p.lane])).toEqual([['a', 0], ['b', 1], ['c', 0]]);
    expect(lanes.http.rowCount).toBe(2);
    expect(lanes.commands.roots.map((p) => p.row.key)).toEqual(['cmd']);
    expect(lanes.commands.rowCount).toBe(1);
  });

  it('caps the rows and puts the excess on the last one, flagged', () => {
    const rows = Array.from({ length: MAX_ROOT_ROWS + 2 }, (_, i) => root(`r${i}`, 'command', i, 1_000));
    const lane = packRootLanes(rows).commands;
    expect(lane.rowCount).toBe(MAX_ROOT_ROWS);
    expect(lane.overflowCount).toBe(2);
    expect(lane.roots.filter((p) => p.overflow).map((p) => p.lane)).toEqual([MAX_ROOT_ROWS - 1, MAX_ROOT_ROWS - 1]);
  });

  it('marks a dropped command on row 0 without taking a row', () => {
    const lane = packRootLanes([root('cmd', 'command', 0, 100), root('drop', 'dropped', 50, 50)]).commands;
    expect(lane.rowCount).toBe(1);
    expect(lane.roots.find((p) => p.row.key === 'drop')?.lane).toBe(0);
  });

  it('has no rows when there is no root', () => {
    expect(packRootLanes([]).http.rowCount).toBe(0);
  });
});

describe('findRootAt', () => {
  it('prefers a bar of its own row over an overflow one drawn on it', () => {
    const rows = Array.from({ length: MAX_ROOT_ROWS + 1 }, (_, i) => root(`r${i}`, 'command', i, 1_000));
    const lane = packRootLanes(rows).commands;
    expect(findRootAt(lane, 500, MAX_ROOT_ROWS - 1, 0)?.row.key).toBe(`r${MAX_ROOT_ROWS - 1}`);
    expect(findRootAt(lane, 500, 0, 0)?.row.key).toBe('r0');
    expect(findRootAt(lane, 2_000, 0, 0)).toBeNull();
  });

  it('prefers a dropped notch over the command bar it is drawn on', () => {
    const lane = packRootLanes([root('cmd', 'command', 0, 100), root('drop', 'dropped', 50, 50)]).commands;
    expect(findRootAt(lane, 51, 0, 4)?.row.key).toBe('drop');
    expect(findRootAt(lane, 80, 0, 4)?.row.key).toBe('cmd');
  });

  it('prefers the bar containing the time over an earlier one only within the tolerance', () => {
    const lane = packRootLanes([root('a', 'command', 0, 98), root('b', 'command', 99, 200)]).commands;
    expect(lane.rowCount).toBe(1);
    expect(findRootAt(lane, 100, 0, 5)?.row.key).toBe('b');
  });

  it('finds a root past a long bar on another row, with the per-row index', () => {
    const lane = packRootLanes([root('long', 'command', 0, 1_000_000), ...Array.from({ length: 50 }, (_, i) => root(`s${i}`, 'command', i * 10, i * 10 + 5))]).commands;
    const row1 = lane.rows[1];
    expect(row1.idx.length).toBe(50);
    expect(firstRootEndingAfter(lane, 1, 400)).toBe(40); // s40 ends at 405: no scan from the long bar's row
    expect(findRootAt(lane, 402, 1, 0)?.row.key).toBe('s40');
    expect(findRootAt(lane, 402, 0, 0)?.row.key).toBe('long');
  });

  it('hits a zero-length dropped mark within the tolerance', () => {
    const lane = packRootLanes([root('drop', 'dropped', 50, 50)]).commands;
    expect(findRootAt(lane, 52, 0, 3)?.row.key).toBe('drop');
    expect(findRootAt(lane, 60, 0, 3)).toBeNull();
  });
});

describe('root lanes in the time area', () => {
  it('lays out the two lanes above the slot lanes, each as tall as its rows, and only when present', () => {
    const ids = (http: number, commands: number): string[] =>
      buildLayout({ ...layoutBase, rootRows: { http, commands } }).tracks.map((t) => t.id).slice(0, 4);
    expect(ids(2, 1)).toEqual(['ruler', ROOT_HTTP_TRACK_ID, ROOT_COMMANDS_TRACK_ID, 'slot-0']);
    expect(ids(0, 1)).toEqual(['ruler', ROOT_COMMANDS_TRACK_ID, 'slot-0', 'phases']);
    expect(ids(0, 0)).toEqual(['ruler', 'slot-0', 'phases', 'page-cache']);
    const http = buildLayout({ ...layoutBase, rootRows: { http: 3, commands: 0 } }).tracks.find((t) => t.id === ROOT_HTTP_TRACK_ID);
    expect(http?.height).toBe(3 * SPAN_ROW_HEIGHT);
  });

  it('hit-tests a root bar by its row, and reads it in the tooltip', () => {
    const lanes = packRootLanes([
      root('a', 'command', 0, 100, { session: 7, ringWaitUs: 10 }),
      root('b', 'command', 50, 150),
    ]);
    const { tracks } = buildLayout({ ...layoutBase, rootRows: { http: 0, commands: lanes.commands.rowCount } });
    const track = tracks.find((t) => t.id === ROOT_COMMANDS_TRACK_ID)!;
    const vp = { offsetX: 0, scaleX: 1, scrollY: 0 };
    const at = (mx: number, row: number) => hitTestTimeArea({
      mx: 100 + mx, my: track.y + row * SPAN_ROW_HEIGHT + 2, tracks, ticks: [], vp, gutterWidth: 100,
      legendsVisible: false, offCpuBySlot: new Map(), showOffCpu: false, rootLanes: lanes,
    });

    const first = at(20, 0);
    expect(first?.kind === 'root' && first.root.key).toBe('a');
    const second = at(120, 1);
    expect(second?.kind === 'root' && second.root.key).toBe('b');
    expect(at(120, 0)).toBeNull();
    const lines = buildHoverTooltipLines(first);
    expect(lines).toEqual(expect.arrayContaining(['a', 'Session: 7', 'Ring wait: 10.0us']));
    expect(lines?.some((l) => l.startsWith('Engine'))).toBe(false); // no span carried its id
  });
});
