import { bandRanges, placeBand, type BandRequest, type BandResult } from './band-bake';
import { FIELD_ORIGIN_M, POSTS, POST_SPACING_M } from './heightfield';
import { TERRAIN_SEED } from './tatooine-terrain';
import type { FinishRequest, TerrainBaked } from './terrain.worker';

/**
 * Runs the planet's bake across every core, then finishes it, and calls back once with the result.
 *
 * Two stages, both off the main thread:
 *
 * 1. **{@link workerCount} band workers**, pulling from a queue of {@link BANDS_PER_WORKER} × that many bands. Bands are
 *    exact rather than approximate — see `band-bake.ts` — so this is a pure speed-up with no seam to hide.
 * 2. **One finishing worker**, which applies the town pads to the assembled field (they cannot be banded: a pad levels the
 *    ground to the height at the site's centre, which may lie in another band) and measures the quadtree's per-node error.
 *
 * The main thread's only work is writing each band's rows into one buffer — a 67 MB memcpy spread over however many bands
 * arrive — and the two transfers, which move ownership rather than copying.
 *
 * Measured at 4096 posts on a 7950X: **13.2 s of work**, of which 12.4 s is the landform, 0.7 s the quadtree and 0.08 s
 * the pads. One thread would show a flat planet for thirteen seconds.
 *
 * It is separated from `client-app.ts` mainly so the `new Worker(new URL(...))` form — which Vite rewrites at build time
 * and which no test environment can execute — sits in one place that tests do not import.
 */

/** A bake in flight. `terminate` is safe at any point and safe to call twice. */
export interface TerrainBake {
  terminate(): void;
}

export interface BakeRequest {
  /** Overrides the default seed; the mock and the live client use the same planet, so this is for experiments. */
  readonly seed?: number;
  /** Overrides the worker count. Tests use it; nothing else should. */
  readonly workers?: number;
}

/**
 * Bands per worker.
 *
 * More than one because the bands are **not** equal work: a band that crosses the mesa belt evaluates two extra layers
 * over its whole width, and one carrying only the global layers does not. Cut eight ways the measured spread was
 * 1 237–1 853 ms, so the slowest band alone cost 600 ms of tail. Three bands each lets a worker that drew light ones take
 * another, and the makespan falls back toward the average.
 */
export const BANDS_PER_WORKER = 3;

/**
 * How many band workers to start.
 *
 * Capped at 8, and not because of the CPU: each worker holds its own halo grid while it works, and past about eight the
 * per-worker startup stops being noise against the bake. Floored at 1, so a browser that reports nothing still works —
 * single-threaded, exactly as it did before there were bands.
 */
export function workerCount(): number {
  // `navigator` itself is what is missing outside a browser; `hardwareConcurrency` is required once it exists.
  const cores = typeof navigator === 'undefined' ? 4 : navigator.hardwareConcurrency;

  return Math.max(1, Math.min(8, Math.floor(cores)));
}

export function startTerrainBake(onBaked: (baked: TerrainBaked) => void, request: BakeRequest = {}): TerrainBake {
  const seed = request.seed ?? TERRAIN_SEED;
  const threads = Math.max(1, Math.min(request.workers ?? workerCount(), POSTS));
  const queue = bandRanges(POSTS, threads * BANDS_PER_WORKER);
  const started = performance.now();
  const assembled = {
    posts: POSTS,
    spacingM: POST_SPACING_M,
    originM: FIELD_ORIGIN_M,
    rowOffset: 0,
    rows: POSTS,
    height: new Float32Array(POSTS * POSTS),
  };
  const workers: Worker[] = [];
  let done = false;
  let next = 0;
  let outstanding = queue.length;

  // Terminate FIRST, then guard on `done`: the caller may dispose while a message is already queued, and delivering it
  // then drives a texture upload on a disposed texture.
  const stop = (): void => {
    done = true;
    for (const worker of workers) {
      worker.terminate();
    }

    workers.length = 0;
  };

  const fail = (what: string): void => {
    if (!done) {
      console.warn('terrain bake failed; the planet stays flat', what);
      stop();
    }
  };

  const watch = (worker: Worker, what: string): void => {
    worker.onerror = (event: ErrorEvent): void => {
      fail(`${what}: ${event.message}`);
    };

    worker.onmessageerror = (): void => {
      fail(`${what}: the result could not be deserialised`);
    };
  };

  const finish = (): void => {
    // Every band thread is idle now and each is holding its last halo grid. Releasing them before the finisher starts
    // keeps the peak footprint to the assembled field plus one worker rather than plus nine.
    for (const worker of workers) {
      worker.terminate();
    }

    workers.length = 0;
    const finisher = new Worker(new URL('./terrain.worker.ts', import.meta.url), { type: 'module' });
    workers.push(finisher);
    watch(finisher, 'finishing the bake');
    finisher.onmessage = (event: MessageEvent<TerrainBaked>): void => {
      if (done) {
        return;
      }

      // The clock belongs here: the finisher has no idea when the first band started, and the number the HUD shows is the
      // wall time the player waited, not the time any one thread spent.
      const baked = { ...event.data, bakeMs: performance.now() - started };
      try {
        onBaked(baked);
      } finally {
        stop();
      }
    };

    const message: FinishRequest = {
      height: assembled.height,
      posts: POSTS,
      spacingM: POST_SPACING_M,
      originM: FIELD_ORIGIN_M,
    };
    finisher.postMessage(message, [assembled.height.buffer]);
  };

  /** Hands a worker the next band, or lets it go when the queue is empty. */
  const pump = (worker: Worker): void => {
    if (next >= queue.length) {
      // Nothing left for this thread, and it is holding its halo grid. Letting it go here rather than at the end keeps
      // the peak footprint near one band per live worker rather than one per band.
      worker.terminate();
      return;
    }

    const range = queue[next];
    next++;
    const bandRequest: BandRequest = {
      posts: POSTS,
      spacingM: POST_SPACING_M,
      originM: FIELD_ORIGIN_M,
      seed,
      ...range,
    };
    worker.postMessage(bandRequest);
  };

  for (let i = 0; i < threads; i++) {
    const worker = new Worker(new URL('./terrain-band.worker.ts', import.meta.url), { type: 'module' });
    workers.push(worker);
    watch(worker, 'baking a band');
    worker.onmessage = (event: MessageEvent<BandResult>): void => {
      if (done) {
        return;
      }

      try {
        placeBand(assembled, event.data);
      } catch (error) {
        fail(String(error));
        return;
      }

      outstanding--;
      if (outstanding === 0) {
        finish();
        return;
      }

      pump(worker);
    };

    pump(worker);
  }

  return { terminate: stop };
}
