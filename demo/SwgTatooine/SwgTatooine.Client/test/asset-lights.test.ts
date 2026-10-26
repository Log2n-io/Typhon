import { describe, expect, it } from 'vitest';
import { NullEngine } from '@babylonjs/core/Engines/nullEngine';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { Scene } from '@babylonjs/core/scene';
import { PointLight } from '@babylonjs/core/Lights/pointLight.pure';
import '../src/assets/asset-library';

/**
 * A placed asset's lamps are CLONED from its container (`instantiateModelsToScene`), and a light clones through a constructor
 * registry that only the light classes' side-effect modules fill. The glTF loader imports the side-effect-free module, so
 * without `asset-library`'s registration the room assets threw on every placement and were never drawn.
 */
describe('asset lamps', () => {
  it('can be cloned once the asset library is loaded', () => {
    const scene = new Scene(new NullEngine());
    const lamp = new PointLight('lamp', new Vector3(1, 2, 3), scene);
    const copy = lamp.clone('copy');
    expect(copy).not.toBeNull();
    expect(copy?.getClassName()).toBe('PointLight');
  });
});
