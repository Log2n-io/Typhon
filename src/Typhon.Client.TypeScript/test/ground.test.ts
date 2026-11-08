import { describe, expect, it } from 'vitest';
import { CatalogPlan, evaluateLive, FrameApplier, parseCatalog } from '../src/index.js';
import { goldenBin } from './golden-support.js';

/*
 * #1616: a ground-clamped archetype's grounded segments carry no height. The replica holds NaN there, and evaluateLive's
 * heightAt fills it from the application's terrain — called with the two horizontal axes, x and y when z is up — while an
 * airborne entity, and every velocity, evaluates as decoded.
 */

function frames(stream: Uint8Array): Uint8Array[] {
  const out: Uint8Array[] = [];
  const view = new DataView(stream.buffer, stream.byteOffset, stream.byteLength);
  for (let at = 0; at < stream.length;) {
    const length = view.getUint32(at, true);
    out.push(stream.subarray(at + 4, at + 4 + length));
    at += 4 + length;
  }

  return out;
}

describe('ground-clamped motion (#1616)', () => {
  it('evaluates a grounded height from the terrain and leaves an airborne one as decoded', () => {
    const plan = CatalogPlan.compile(parseCatalog(goldenBin('catalog-ground')));
    const applier = new FrameApplier(plan);
    const messages = frames(goldenBin('stream-ground'));
    // After frame 2: walker 1 airborne, walker 2 grounded.
    applier.apply(messages[0]!);
    applier.apply(messages[1]!);

    const walkers = applier.world.archetypeStore(plan.archetypeByName('Walker')!.idx);
    expect(walkers.upAxis).toBe(2);
    const stride = walkers.motionStride;
    const raw = new Float64Array(walkers.liveCount * stride);
    const grounded = new Float64Array(raw.length);
    evaluateLive(walkers, 501, 0.5, raw);
    evaluateLive(walkers, 501, 0.5, grounded, (x, y) => 100 + x + 1000 * y);
    for (let i = 0; i < walkers.liveCount; i++) {
      const netId = walkers.netIds[walkers.live[i]!]!;
      const o = i * stride;
      if (netId === 2) {
        expect(Number.isNaN(raw[o + 2]!)).toBe(true);
        expect(grounded[o + 2]).toBe(100 + grounded[o]! + 1000 * grounded[o + 1]!);
        expect(grounded[o + 5]).toBe(0);
      } else {
        expect([...grounded.subarray(o, o + stride)]).toEqual([...raw.subarray(o, o + stride)]);
      }
    }
  });
});
