import { afterEach, describe, expect, it, vi } from 'vitest';
import { AutoReleaseWorkerPool } from '@babylonjs/core/Misc/workerPool';
import { configureDecoders, DECODER_WORKER_IDLE_MS } from '../src/assets/decoders';

/**
 * The KTX2 decoder's workers outlive a pause in the streaming: Babylon terminated each after one idle second, and the next
 * burst of textures paid for new workers and a fresh wasm compile before its first decode.
 */
describe('configureDecoders', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('keeps an idle decoder worker, where Babylon let it go after a second', async () => {
    vi.stubGlobal('location', { href: 'http://localhost/' });
    configureDecoders('/vendor/babylon-test/');
    expect(AutoReleaseWorkerPool.DefaultOptions.idleTimeElapsedBeforeRelease).toBe(DECODER_WORKER_IDLE_MS);
    expect(DECODER_WORKER_IDLE_MS, "setTimeout's ceiling: past it a delay fires at once").toBeLessThan(2 ** 31);

    vi.useFakeTimers();
    const terminate = vi.fn();
    const worker = { terminate } as unknown as Worker;
    const pool = new AutoReleaseWorkerPool(1, () => Promise.resolve(worker));
    await new Promise<void>((done) => {
      pool.push((_w, onComplete) => {
        onComplete();
        done();
      });
      void vi.advanceTimersByTimeAsync(0);
    });

    await vi.advanceTimersByTimeAsync(10 * 60 * 1000);
    expect(terminate).not.toHaveBeenCalled();
  });
});
