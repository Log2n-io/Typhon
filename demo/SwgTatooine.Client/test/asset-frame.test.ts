import { describe, expect, it } from 'vitest';
import {
  mirrorQuat,
  placement,
  pointToWorld,
  poseToWorld,
  rotationToWorld,
  yawForHeading,
} from '../src/assets/asset-frame';
import { quatFromYaw, quatMul, rotate, type Quat, type Vec3 } from '../src/math/vec';

const close = (a: readonly number[], b: readonly number[], eps = 1e-9): void => {
  a.forEach((v, i) => {
    expect(v).toBeCloseTo(b[i], -Math.log10(eps));
  });
};

describe('asset frame', () => {
  it('mirrors X like the glTF loader root, and keeps Y and Z', () => {
    close(pointToWorld(placement([0, 0, 0]), [1, 2, 3]), [-1, 2, 3]);
  });

  it('places at the pose: translation plus yaw', () => {
    // yaw 90 degrees turns +Z to +X; a glTF point on +Z (in front) lands at +X of the placement
    close(pointToWorld(placement([10, 0, 5], Math.PI / 2), [0, 0, 1]), [11, 0, 5]);
  });

  it('mirroring a quaternion conjugates its rotation by the X mirror', () => {
    const q: Quat = [0.2, 0.3, -0.1, Math.sqrt(1 - 0.04 - 0.09 - 0.01)];
    const v: Vec3 = [0.3, -0.7, 0.4];
    const m = (p: Vec3): Vec3 => [-p[0], p[1], p[2]];
    close(rotate(mirrorQuat(q), v), m(rotate(q, m(v))));
  });

  it('composes children without a double flip: child of child equals the composed pose', () => {
    const parent = placement([3, 0, -2], 0.7);
    const child = poseToWorld(parent, [1, 0, 2], quatFromYaw(0.4));
    const p: Vec3 = [0.5, 1, -0.25];
    // geometry of the child, mirrored by its own root, placed at the child's world pose
    const viaChild = rotate(child.rot, [-p[0], p[1], p[2]]).map((c, i) => c + child.pos[i]);
    // the same point expressed in the parent's glTF frame first
    const inParent = rotate(quatFromYaw(0.4), p).map((c, i) => c + [1, 0, 2][i]) as unknown as Vec3;
    close(viaChild, pointToWorld(parent, inParent));
  });

  it('turns a +Z-facing model to face the server heading', () => {
    for (const h of [0, 0.5, Math.PI / 2, 2, -2.5]) {
      const yaw = yawForHeading(h);
      close(rotate(quatFromYaw(yaw), [0, 0, 1]), [Math.cos(h), 0, Math.sin(h)]);
    }
  });

  it('rotationToWorld is the mirrored rotation under the placement yaw', () => {
    const q = quatFromYaw(0.3);
    close(rotationToWorld(placement([0, 0, 0], 0.5), q), quatMul(quatFromYaw(0.5), quatFromYaw(-0.3)));
  });
});
