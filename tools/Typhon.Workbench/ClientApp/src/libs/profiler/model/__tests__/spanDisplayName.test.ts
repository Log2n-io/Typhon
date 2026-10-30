import { describe, expect, it } from 'vitest';
import { spanDisplayName } from '../traceModel';
import { TraceEventKind, type SystemDef, type TraceEvent } from '@/libs/profiler/model/types';
import { isInstantKind } from '@/libs/profiler/decode/chunkDecoder';

const sys = (index: number, name: string): SystemDef => ({
  index, name, type: 0, priority: 0, isParallel: false, tierFilter: 0, predecessors: [], successors: [],
});
const systems = [sys(0, 'PlayerSessions'), sys(1, 'Shuttle')];
const evt = (kind: TraceEventKind, extra: Partial<TraceEvent>): TraceEvent =>
  ({ kind, threadSlot: 0, tickNumber: 1, timestampUs: 0, ...extra }) as TraceEvent;

describe('spanDisplayName', () => {
  it('names the system whose completion a fan-out follows', () => {
    expect(spanDisplayName(evt(TraceEventKind.SchedulerDependencyFanOut, { completingSystemIndex: 0 }), systems))
      .toBe('FanOut after PlayerSessions');
  });

  it('names the dispatched successor and its outcome', () => {
    expect(spanDisplayName(evt(TraceEventKind.SchedulerSuccessorDispatch, { completingSystemIndex: 0, successorIndex: 1, dispatchOutcome: 2 }), systems))
      .toBe('Dispatch Shuttle (parallel)');
  });

  it('falls back to the index for a system the metadata does not know', () => {
    expect(spanDisplayName(evt(TraceEventKind.SchedulerDependencyFanOut, { completingSystemIndex: 9 }), systems))
      .toBe('FanOut after System[9]');
  });

  it('decodes the dispatch kind as a span, not an instant', () => {
    expect(isInstantKind(TraceEventKind.SchedulerSuccessorDispatch)).toBe(false);
  });
});
