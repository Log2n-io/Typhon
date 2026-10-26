import { describe, expect, it } from 'vitest';
import { EYE_HEIGHT_M, EyeCamera, MAX_EYE_DISTANCE_M } from '../src/camera/eye-camera';

/*
 * CLI3D-10 — the camera that stands in the world.
 *
 * The geometry is the part worth pinning: it must agree with `MapCamera`'s conventions exactly, because the scene reads
 * whichever camera is active through the same five values and applies `rotation.set(pitch, yaw, 0)` either way. A sign
 * that disagrees does not fail loudly — it just looks wrong.
 */

/** Settles the smoothing: one long step is enough, the filter is exponential. */
function settle(cam: EyeCamera, seconds = 5): void {
  cam.update(seconds);
}

describe('EyeCamera', () => {
  it('puts the eye at the subject, at head height, in first person', () => {
    const cam = new EyeCamera();
    cam.jumpTo(100, 0, -50, 0);

    expect([cam.eyeX, cam.eyeZ]).toEqual([100, -50]);
    expect(cam.eyeY).toBeCloseTo(EYE_HEIGHT_M, 9);
  });

  it('rides the subject upward: the eye tracks its altitude, not the ground', () => {
    // A subject standing on a 40 m mesa has its eye at 41.7 m, not 1.7 m. Relief has nowhere else to enter the view.
    const cam = new EyeCamera();
    cam.jumpTo(0, 40, 0, 0);

    expect(cam.eyeY).toBeCloseTo(40 + EYE_HEIGHT_M, 9);
  });

  it('agrees with MapCamera on which way yaw points', () => {
    // MapCamera: eye = target − cos(pitch)·sin(yaw)·d on x, − cos(pitch)·cos(yaw)·d on z. At yaw 0 and pitch 0 the eye
    // sits at −z of the target, i.e. the camera looks along +z. Pulling back in eye mode must do the same.
    const cam = new EyeCamera();
    cam.jumpTo(0, 0, 0, 0);
    cam.zoomBy(2);
    settle(cam);

    expect(cam.eyeZ, 'yaw 0 pulls the eye back along −z, so the view runs +z').toBeLessThan(0);
    expect(cam.eyeX).toBeCloseTo(0, 6);
  });

  it('rises as it pitches down, so the subject stays in frame over the shoulder', () => {
    const cam = new EyeCamera();
    cam.jumpTo(0, 0, 0, 0);
    cam.zoomBy(2);
    settle(cam);
    const level = cam.eyeY;

    // Positive pitch is looking DOWN (MapCamera's convention).
    cam.rotateBy(0, 0.6);
    settle(cam);

    expect(cam.eyeY).toBeGreaterThan(level);
  });

  it('can look up, which the map camera is forbidden to do', () => {
    const cam = new EyeCamera();
    cam.jumpTo(0, 0, 0, 0);
    cam.rotateBy(0, -1.2);
    settle(cam);

    expect(cam.pitch).toBeLessThan(0);
  });

  it('stops short of vertical either way, where yaw stops meaning anything', () => {
    const up = new EyeCamera();
    up.jumpTo(0, 0, 0, 0);
    up.rotateBy(0, -99);
    settle(up);
    const down = new EyeCamera();
    down.jumpTo(0, 0, 0, 0);
    down.rotateBy(0, 99);
    settle(down);

    expect(up.pitch).toBeCloseTo((-85 * Math.PI) / 180, 6);
    expect(down.pitch).toBeCloseTo((85 * Math.PI) / 180, 6);
  });

  it('leaves first person on a zoom out, and returns to it', () => {
    // A multiplicative zoom can never escape 0, which would strand the camera in first person for the session's life.
    const cam = new EyeCamera();
    cam.jumpTo(0, 0, 0, 0);
    expect(cam.distance).toBe(0);

    cam.zoomBy(1.3);
    settle(cam);
    expect(cam.distance).toBeGreaterThan(0.5);

    for (let i = 0; i < 40; i++) {
      cam.zoomBy(0.7);
    }

    settle(cam);
    expect(cam.distance).toBeCloseTo(0, 3);
  });

  it('never pulls back further than its bound', () => {
    const cam = new EyeCamera();
    cam.jumpTo(0, 0, 0, 0);
    for (let i = 0; i < 60; i++) {
      cam.zoomBy(1.5);
    }

    settle(cam);
    expect(cam.distance).toBeCloseTo(MAX_EYE_DISTANCE_M, 6);
  });

  it('glides to a moving subject instead of snapping with it', () => {
    const cam = new EyeCamera();
    cam.jumpTo(0, 0, 0, 0);
    cam.follow(1000, 0, 0);

    // One short step: the eye has started toward the subject and is nowhere near it.
    cam.update(1 / 60);
    expect(cam.eyeX).toBeGreaterThan(0);
    expect(cam.eyeX).toBeLessThan(500);

    settle(cam);
    expect(cam.eyeX).toBeCloseTo(1000, 3);
  });

  it('places itself on the first follow, so a subject selected before the first frame is not left at the origin', () => {
    const cam = new EyeCamera();
    cam.follow(-700, 12, 300);

    expect([cam.eyeX, cam.eyeZ]).toEqual([-700, 300]);
    expect(cam.eyeY).toBeCloseTo(12 + EYE_HEIGHT_M, 9);
  });

  it('has a near plane that does not clip what is a metre away', () => {
    const cam = new EyeCamera();
    expect(cam.nearPlane).toBeLessThan(0.5);
    expect(cam.fov).toBeGreaterThan(0.9);
  });

  it('never puts the eye inside the terrain, at any pitch or pull-back', () => {
    // The eye used to travel the full pull-back regardless of what was in the way. Two ways that goes underground: 30 m
    // back at level pitch is inside a mesa, and looking UP while pulled back drives it below the subject's own feet.
    const cam = new EyeCamera();
    // A subject on a 120 m plateau whose ground is that height everywhere: any pull-back at all is into rock.
    cam.ground = { heightAt: () => 120 };
    cam.jumpTo(0, 120, 0, 0);
    for (const pitch of [-1.4, -0.6, 0, 0.6, 1.4]) {
      cam.rotateBy(0, pitch - cam.pitch);
      cam.zoomBy(4);
      for (let i = 0; i < 200; i++) {
        cam.update(0.05);
      }

      expect(cam.eyeY, `pitch ${pitch}`).toBeGreaterThan(120);
    }
  });

  it('still reaches its full pull-back over open ground', () => {
    // The other half: a clamp that always shortened to zero would pass the test above and destroy the over-the-shoulder
    // view entirely.
    const cam = new EyeCamera();
    cam.jumpTo(0, 0, 0, 0);
    cam.zoomBy(4);
    for (let i = 0; i < 400; i++) {
      cam.update(0.05);
    }

    expect(Math.hypot(cam.eyeX, cam.eyeZ)).toBeGreaterThan(1);
  });

  it('is never blocked in first person: distance 0 always places the eye at head height', () => {
    const cam = new EyeCamera();
    // Ground ABOVE the subject's head — a subject inside a cave, or a stale field. First person must still work.
    cam.ground = { heightAt: () => 500 };
    cam.jumpTo(0, 10, 0, 0);
    expect(cam.eyeY).toBeCloseTo(10 + EYE_HEIGHT_M, 9);
  });
});
