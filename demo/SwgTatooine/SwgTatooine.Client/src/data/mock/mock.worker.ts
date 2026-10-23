import { TICK_PERIOD_MS } from '../swg-schema';
import { isEmptyTick, type MainToWorker, type WorkerToMain } from './protocol';
import { MockServer } from './server';

/**
 * The mock server's thread: builds the world, then ticks it at 10 Hz on a drift-free schedule and posts one message per
 * tick, transferring its buffers. Off the main thread so its cost never shows in the client's frame budget — which is
 * where the real server's cost lives too.
 */

declare const self: {
  postMessage(message: WorkerToMain, transfer: Transferable[]): void;
  onmessage: ((event: MessageEvent<MainToWorker>) => void) | null;
};

const KEEPALIVE_TICKS = 5;

let server: MockServer | null = null;
let paused = false;
let timer: ReturnType<typeof setTimeout> | null = null;
let nextTickAt = 0;

function loop(): void {
  timer = null;
  if (server === null) {
    return;
  }

  const message = server.step(() => performance.now(), paused);
  // Like the real server: frames only when something happened, plus a header-only keepalive every 500 ms.
  if (!isEmptyTick(message) || message.tick % KEEPALIVE_TICKS === 0) {
    self.postMessage(message, MockServer.transferables(message));
  }

  const now = performance.now();
  nextTickAt += TICK_PERIOD_MS;
  if (nextTickAt < now - TICK_PERIOD_MS * 5) {
    // Fell far behind (a suspended tab): resume on schedule rather than replaying the missed ticks at once.
    nextTickAt = now;
  }

  timer = setTimeout(loop, Math.max(0, nextTickAt - now));
}

self.onmessage = (event) => {
  const message = event.data;
  switch (message.type) {
    case 'start': {
      const started = performance.now();
      server = new MockServer(message.seed, message.population);
      self.postMessage(
        {
          type: 'ready',
          population: message.population,
          worldEntities: server.worldEntities,
          buildMs: performance.now() - started,
        },
        [],
      );
      nextTickAt = performance.now();
      if (timer === null) {
        loop();
      }

      break;
    }
    case 'region':
      server?.setRegion(message.x, message.z, message.radius);
      break;
    case 'pause':
      paused = message.paused;
      break;
    case 'stop':
      if (timer !== null) {
        clearTimeout(timer);
        timer = null;
      }

      server = null;
      break;
  }
};
