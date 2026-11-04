import { describe, expect, it } from 'vitest';
import { isStaleChunkError, useBuild } from '../src/state/build-store';

/** A page older than the server's build (client.md C41): a failed dynamic import recognised, and the page marked stale once. */
describe('a stale build', () => {
  it('is recognised by the failed fetch of one of its chunks, in each browser’s words', () => {
    expect(
      isStaleChunkError(
        new Error(
          'Unable to load from /assets/tat_house_large_interior-5.glb: Failed to fetch dynamically imported module: http://x/app/KHR.js',
        ),
      ),
    ).toBe(true);
    expect(isStaleChunkError(new TypeError('Importing a module script failed.'))).toBe(true);
    expect(isStaleChunkError(new Error('error loading dynamically imported module'))).toBe(true);
    expect(isStaleChunkError(new Error('Unable to load from /assets/x.glb: 404'))).toBe(false);
    expect(isStaleChunkError('nope')).toBe(false);
  });

  it('is marked once and stays marked', () => {
    expect(useBuild.getState().stale).toBe(false);
    useBuild.getState().markStale();
    useBuild.getState().markStale();
    expect(useBuild.getState().stale).toBe(true);
  });
});
