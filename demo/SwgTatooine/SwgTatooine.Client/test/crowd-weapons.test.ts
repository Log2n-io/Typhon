import { Matrix, Vector3 } from '@babylonjs/core/Maths/math.vector';
import { describe, expect, it } from 'vitest';

import { weaponBindMatrix } from '../src/render/crowd-weapons';

/**
 * A weapon on a crowd body is a mesh skinned to the hand's joint, its vertices put in bind space once:
 * `v_bind = inverse(IBM[joint]) × scale(1/s) × translate(pos) × rotate(rot) × v` (ASSET_FORMAT.md § 5.10). Babylon's
 * matrices are row-vector, so the order is the thing at risk; each case below isolates one factor.
 */
describe("a weapon's bind matrix", () => {
  const POS: [number, number, number] = [0, 0.11, 0.021];
  const NONE: [number, number, number, number] = [0, 0, 0, 1];

  function at(m: Matrix, v: [number, number, number]): number[] {
    return Vector3.TransformCoordinates(Vector3.FromArray(v), m)
      .asArray()
      .map((c) => Math.round(c * 1e6) / 1e6 + 0);
  }

  it('puts the grip at the attach point, the character scale removed', () => {
    expect(at(weaponBindMatrix(Matrix.Identity(), POS, NONE, 0.5), [0, 0, 0])).toEqual([0, 0.22, 0.042]);
  });

  it('rotates the weapon before it is moved to the grip', () => {
    // Half a turn about Y: the weapon's +Z points down -Z, then the grip offset, then the scale.
    expect(at(weaponBindMatrix(Matrix.Identity(), POS, [0, 1, 0, 0], 0.5), [0, 0, 1])).toEqual([0, 0.22, -1.958]);
  });

  it("goes into bind space last, through the joint's inverse bind matrix", () => {
    // A joint bound one metre along +X: its inverse bind moves points back by a metre, so bind space is a metre on.
    const inverseBind = Matrix.Translation(-1, 0, 0);
    expect(at(weaponBindMatrix(inverseBind, POS, NONE, 1), [0, 0, 0])).toEqual([1, 0.11, 0.021]);
  });
});
