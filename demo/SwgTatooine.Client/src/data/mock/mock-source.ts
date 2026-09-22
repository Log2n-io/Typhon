import type { AggregateGrid, Clock, WorldStore } from '@typhondb/client';
import type { DataSource, EventSink, LatencyControl, SourceStats } from '../source';
import { applyTick } from './apply';
import { TickFlags, type MainToWorker, type TickMessage, type WorkerToMain } from './protocol';

/** The part of a `Worker` the source uses: tests hand in a fake. */
export interface WorkerLike {
  onmessage: ((event: MessageEvent<WorkerToMain>) => void) | null;
  postMessage(message: MainToWorker): void;
  terminate(): void;
}

export interface MockSourceOptions {
  readonly world: WorldStore;
  readonly grid: AggregateGrid;
  readonly clock: Clock;
  readonly events: EventSink;
  readonly seed: number;
  readonly population: number;
  /** Simulated one-way network delay and jitter, so the clock sees what a real link gives it. */
  readonly latencyMs: number;
  readonly jitterMs: number;
  /** Test seams: the worker, and the random source of the jitter. */
  readonly createWorker?: () => WorkerLike;
  readonly random?: () => number;
}

/** Frames remembered for the wire rate: enough for a second of ticks at up to 60 Hz. */
const BYTE_RING = 64;

function createMockWorker(): WorkerLike {
  return new Worker(new URL('./mock.worker.ts', import.meta.url), { type: 'module' });
}

/**
 * The mock server as a {@link DataSource}: a worker produces frames, and this applies them to the store strictly in
 * arrival order. Simulated latency delays whole frames but never reorders them.
 */
export class MockSource implements DataSource, LatencyControl {
  private readonly options: MockSourceOptions;
  private worker: WorkerLike | null = null;
  private readonly queue: { message: TickMessage; releaseAt: number }[] = [];
  private queueHead = 0;
  private drainTimer: ReturnType<typeof setTimeout> | null = null;
  private lastReleaseAt = 0;
  /** Tick and wire bytes of recent frames. Frames are skipped when a tick carries nothing, so a rate counts ticks, not frames. */
  private readonly byteTicks = new Float64Array(BYTE_RING).fill(Number.NEGATIVE_INFINITY);
  private readonly byteCounts = new Float64Array(BYTE_RING);
  private byteNext = 0;
  private current: SourceStats;

  private latencyMs: number;
  private jitterMs: number;

  constructor(options: MockSourceOptions) {
    this.options = options;
    this.latencyMs = options.latencyMs;
    this.jitterMs = options.jitterMs;
    this.current = {
      name: `mock x${options.population}`,
      running: false,
      ticksApplied: 0,
      lastTick: -1,
      viewComplete: false,
      wireBytesPerSec: 0,
      applyMs: 0,
      server: null,
    };
  }

  get stats(): SourceStats {
    return this.current;
  }

  get latency(): LatencyControl {
    return this;
  }

  start(): void {
    if (this.worker !== null) {
      return;
    }

    const worker = (this.options.createWorker ?? createMockWorker)();
    worker.onmessage = (event: MessageEvent<WorkerToMain>) => {
      this.onWorkerMessage(event.data);
    };
    worker.postMessage({ type: 'start', seed: this.options.seed, population: this.options.population });
    this.worker = worker;
    this.current = { ...this.current, running: true };
  }

  dispose(): void {
    if (this.worker !== null) {
      this.worker.postMessage({ type: 'stop' });
      this.worker.terminate();
      this.worker = null;
    }

    if (this.drainTimer !== null) {
      clearTimeout(this.drainTimer);
      this.drainTimer = null;
    }

    this.queue.length = 0;
    this.queueHead = 0;
    this.current = { ...this.current, running: false };
  }

  setRegion(x: number, z: number, radius: number): void {
    this.worker?.postMessage({ type: 'region', x, z, radius });
  }

  /** Changes the simulated network delay; frames already queued keep their release times, so order holds. */
  setLatency(latencyMs: number, jitterMs: number): void {
    this.latencyMs = latencyMs;
    this.jitterMs = jitterMs;
  }

  setPaused(paused: boolean): void {
    this.worker?.postMessage({ type: 'pause', paused });
  }

  private onWorkerMessage(message: WorkerToMain): void {
    if (message.type === 'ready') {
      return;
    }

    const now = performance.now();
    const delay = this.latencyMs + (this.options.random ?? Math.random)() * this.jitterMs;
    // A frame never overtakes the previous one: TCP delivers in order.
    const releaseAt = Math.max(now + delay, this.lastReleaseAt);
    this.lastReleaseAt = releaseAt;
    this.queue.push({ message, releaseAt });
    this.drain();
  }

  private drain(): void {
    if (this.drainTimer !== null) {
      clearTimeout(this.drainTimer);
      this.drainTimer = null;
    }

    const now = performance.now();
    while (this.queueHead < this.queue.length && this.queue[this.queueHead].releaseAt <= now) {
      this.apply(this.queue[this.queueHead++].message);
    }

    if (this.queueHead > 64 && this.queueHead * 2 > this.queue.length) {
      this.queue.splice(0, this.queueHead);
      this.queueHead = 0;
    }

    if (this.queueHead < this.queue.length) {
      this.drainTimer = setTimeout(
        () => {
          this.drain();
        },
        Math.max(0, this.queue[this.queueHead].releaseAt - now),
      );
    }
  }

  private apply(message: TickMessage): void {
    const { world, grid, clock, events } = this.options;
    const started = performance.now();
    applyTick(world, grid, message, events);
    const applied = performance.now();
    clock.onFrame(message.tick, applied);

    // Bytes of the frames of the last second of ticks, at the current tick period.
    this.byteTicks[this.byteNext] = message.tick;
    this.byteCounts[this.byteNext] = message.stats.wireBytes;
    this.byteNext = (this.byteNext + 1) % BYTE_RING;
    const windowTicks = Math.max(1, Math.round(1000 / clock.tickPeriodMs));
    let bytes = 0;
    for (let i = 0; i < BYTE_RING; i++) {
      const age = message.tick - this.byteTicks[i];
      if (age >= 0 && age < windowTicks) {
        bytes += this.byteCounts[i];
      }
    }

    this.current = {
      name: this.current.name,
      running: true,
      ticksApplied: this.current.ticksApplied + 1,
      lastTick: message.tick,
      viewComplete: (message.flags & TickFlags.ViewComplete) !== 0,
      wireBytesPerSec: (bytes * 1000) / (windowTicks * clock.tickPeriodMs),
      applyMs: applied - started,
      server: {
        simMs: message.stats.simMs,
        replicationMs: message.stats.replicationMs,
        watched: message.stats.watched,
        effectiveRadius: message.stats.effectiveRadius,
        worldEntities: message.stats.worldEntities,
      },
    };
  }
}
