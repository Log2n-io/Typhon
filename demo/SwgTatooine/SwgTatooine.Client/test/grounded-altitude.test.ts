import { describe, expect, it } from 'vitest';
import { groundedAltitude, Placement } from '../src/data/placement';

// A slope rising 0.1 m per metre east: the drawn position lags the server's by the render delay.
const slope = { heightAt: (x: number) => 0.1 * x };

function drawnAt(x: number, z: number): Placement {
  const p = new Placement();
  p.x = x;
  p.z = z;
  return p;
}

describe('a moving body stands on the ground where it is drawn', () => {
  it('on open ground: the ground under the drawn position, not the server sample taken 5 m ahead', () => {
    // The server sampled y at x = 105 (10.5 m); the body is drawn 5 m behind, at x = 100.
    const y = groundedAltitude(new Float32Array([10.5]), 0, drawnAt(100, 0), 105, 0, slope);
    expect(y).toBeCloseTo(10, 6);
  });

  it('on a deck: the height of the deck over the ground, carried to the drawn position', () => {
    // 2 m of deck under the server's position.
    const y = groundedAltitude(new Float32Array([12.5]), 0, drawnAt(100, 0), 105, 0, slope);
    expect(y).toBeCloseTo(12, 6);
  });

  it('a server sample a quantum under the ground, or a hair over it, is the ground', () => {
    expect(groundedAltitude(new Float32Array([10.49]), 0, drawnAt(100, 0), 105, 0, slope)).toBeCloseTo(10, 6);
    expect(groundedAltitude(new Float32Array([10.52]), 0, drawnAt(100, 0), 105, 0, slope)).toBeCloseTo(10, 6);
  });

  it('without the server field: the old guess, the third axis over the ground', () => {
    const at = drawnAt(100, 0);
    at.y = 3;
    expect(groundedAltitude(null, 0, at, 105, 0, slope)).toBeCloseTo(13, 6);
  });
});
