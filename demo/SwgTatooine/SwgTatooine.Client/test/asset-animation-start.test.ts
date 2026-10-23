import { GLTFLoaderAnimationStartMode } from '@babylonjs/loaders/glTF/glTFFileLoader.pure';
import { describe, expect, it } from 'vitest';

/**
 * The one number `asset-library.ts` cannot import without cost.
 *
 * <b>Why the value is spelled out there and checked here.</b> `GLTFLoaderAnimationStartMode` is declared in
 * `glTFFileLoader.pure`, the module that also carries the glTF loader class; `glTFFileLoader.types` exports nothing at
 * run time. Importing the enum as a value in the client would therefore make the loader eager and undo the lazy
 * registration the boot path chose. A test is not bundled, so it may import the real enum — which turns the literal
 * into a checked constant rather than a magic number that would fail silently if Babylon ever renumbered it.
 */
describe('glTF animation start mode', () => {
  it('is the value asset-library hard-codes for NONE', () => {
    expect(GLTFLoaderAnimationStartMode.NONE).toBe(0);
  });

  it('is not the loader default, which is what the leak was', () => {
    expect(GLTFLoaderAnimationStartMode.FIRST).not.toBe(GLTFLoaderAnimationStartMode.NONE);
  });
});
