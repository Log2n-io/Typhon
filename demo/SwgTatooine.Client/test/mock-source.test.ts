import { AggregateGrid, Clock, WorldStore } from '@typhondb/client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MockSource, type WorkerLike } from '../src/data/mock/mock-source';
import type { MainToWorker, TickMessage, WorkerToMain } from '../src/data/mock/protocol';
import { AGG_GRID, SWG_SCHEMA, TICK_PERIOD_MS } from '../src/data/swg-schema';

class FakeWorker implements WorkerLike {
  onmessage: ((event: MessageEvent<WorkerToMain>) => void) | null = null;
  readonly posted: MainToWorker[] = [];
  terminated = false;

  postMessage(message: MainToWorker): void {
    this.posted.push(message);
  }

  terminate(): void {
    this.terminated = true;
  }

  emit(message: WorkerToMain): void {
    this.onmessage?.({ data: message } as MessageEvent<WorkerToMain>);
  }
}

function tick(n: number, wireBytes = 100): TickMessage {
  return {
    type: 'tick',
    tick: n,
    flags: 0,
    blocks: [],
    events: new Float64Array(0),
    eventCount: 0,
    agg: null,
    stats: { simMs: 0, replicationMs: 0, watched: 0, effectiveRadius: 0, wireBytes, worldEntities: 0 },
  };
}

describe('MockSource', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'performance'] });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  const setup = (randoms: number[]) => {
    const worker = new FakeWorker();
    const world = new WorldStore(SWG_SCHEMA, { maxNetId: 1024 });
    const applied: number[] = [];
    const clock = new Clock({ tickPeriodMs: TICK_PERIOD_MS });
    const onFrame = clock.onFrame.bind(clock);
    clock.onFrame = (t: number, ms: number) => {
      applied.push(t);
      onFrame(t, ms);
    };
    let r = 0;
    const source = new MockSource({
      world,
      grid: new AggregateGrid(AGG_GRID),
      clock,
      events: { onAttack: () => undefined },
      seed: 1,
      population: 1,
      latencyMs: 40,
      jitterMs: 100,
      createWorker: () => worker,
      random: () => randoms[r++ % randoms.length],
    });
    source.start();
    return { worker, world, applied, source };
  };

  it('delays frames by the simulated latency but never lets one overtake another', () => {
    const { worker, world, applied, source } = setup([1, 0, 0.5]);
    worker.emit(tick(1)); // released at 140 ms
    vi.advanceTimersByTime(10);
    worker.emit(tick(2)); // would be released at 50 ms: must wait for tick 1
    vi.advanceTimersByTime(50);
    expect(applied).toEqual([]);

    vi.advanceTimersByTime(80); // 140 ms
    expect(applied).toEqual([1, 2]);
    worker.emit(tick(3));
    vi.advanceTimersByTime(200);
    expect(applied).toEqual([1, 2, 3]);
    expect(source.stats.lastTick).toBe(3);
    expect(source.stats.ticksApplied).toBe(3);
    expect(world.anomalies).toBe(0);
  });

  it('rates wire bytes over the last second of ticks, not the last ten frames', () => {
    const { worker, source } = setup([0]);
    // Idle ticks send no frame: ten frames can span far more than a second.
    for (const t of [1, 2, 3, 20]) {
      worker.emit(tick(t, 1000));
    }

    vi.advanceTimersByTime(100);
    expect(source.stats.lastTick).toBe(20);
    expect(source.stats.wireBytesPerSec).toBe(1000);
  });

  it('keeps the order when the latency drops while frames are queued', () => {
    const { worker, applied, source } = setup([1, 0]);
    worker.emit(tick(1)); // released at 140 ms
    source.latency.setLatency(0, 0);
    vi.advanceTimersByTime(10);
    worker.emit(tick(2)); // due at once, but not before tick 1
    vi.advanceTimersByTime(1);
    expect(applied).toEqual([]);
    vi.advanceTimersByTime(130);
    expect(applied).toEqual([1, 2]);
  });

  it('applies a long run in order through queue compaction, and ignores the ready message', () => {
    const { worker, applied, source } = setup([0]);
    source.latency.setLatency(5, 0);
    worker.emit({ type: 'ready', population: 1, worldEntities: 0, buildMs: 0 });
    for (let t = 1; t <= 300; t++) {
      worker.emit(tick(t));
      if (t % 7 === 0) {
        vi.advanceTimersByTime(6);
      }
    }

    vi.advanceTimersByTime(10);
    expect(applied).toEqual(Array.from({ length: 300 }, (_, i) => i + 1));
  });

  it('stops the worker and drops queued frames on dispose', () => {
    const { worker, applied, source } = setup([1]);
    worker.emit(tick(1));
    source.dispose();
    vi.advanceTimersByTime(1000);
    expect(applied).toEqual([]);
    expect(worker.terminated).toBe(true);
    expect(worker.posted.at(-1)).toEqual({ type: 'stop' });
  });
});
