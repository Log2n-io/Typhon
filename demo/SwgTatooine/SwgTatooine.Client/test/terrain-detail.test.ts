import { describe, expect, it } from 'vitest';
import {
  TERRAIN_CALM_MS,
  TERRAIN_MAX_CALM_MS,
  TERRAIN_MAX_PX,
  TERRAIN_TARGET_FPS,
  TERRAIN_WINDOW_MS,
  TerrainDetailGovernor,
} from '../src/app/terrain-detail';

/**
 * Feeds `seconds` of frames from `startMs`, and answers the last tolerance and when the run ended. The interval is either
 * constant — a frame no terrain changes, which is what a capped refresh rate looks like — or a function of the tolerance
 * the terrain is drawn at, which is what a machine the terrain is loading looks like.
 */
function run(
  g: TerrainDetailGovernor,
  startMs: number,
  seconds: number,
  interval: number | ((px: number) => number),
  floorPx = 8,
): { px: number; endMs: number } {
  let now = startMs;
  let px = floorPx;
  const end = startMs + seconds * 1000;
  while (now < end) {
    const intervalMs = typeof interval === 'number' ? interval : interval(px);
    now += intervalMs;
    px = g.update(now, intervalMs, floorPx);
  }

  return { px, endMs: now };
}

/**
 * A machine the terrain loads: 25 ms (40 fps) at the 8 px floor, faster as the terrain coarsens, down to vsync's 16.7 ms —
 * reached a little past 10 px.
 */
const LOADED = (px: number): number => Math.max(1000 / TERRAIN_TARGET_FPS, 25 * (8 / px) ** 1.5);

/** A 60 Hz display holding the target: vsync puts the frame at the budget, give or take. */
const HOLD_MS = 1000 / TERRAIN_TARGET_FPS;
/** 40 fps whatever the terrain: under the target, and a load the governor cannot remove. */
const SLOW_MS = 25;
/** A browser in battery saver: requestAnimationFrame held to 30 fps. */
const CAPPED_MS = 1000 / 30;
/** One window and a frame, so the window a change lands in is flushed before a level is read. */
const FLUSH_S = TERRAIN_WINDOW_MS / 1000 + 0.05;

describe('TerrainDetailGovernor', () => {
  it('holds the slider while the frame rate holds 60', () => {
    const g = new TerrainDetailGovernor();
    const { px } = run(g, 0, 10, HOLD_MS);
    expect(px).toBe(8);
    expect(g.coarsened).toBe(false);
  });

  it('coarsens a step each window under 60 fps, until the frame holds 60 again', () => {
    const g = new TerrainDetailGovernor();
    let r = run(g, 0, TERRAIN_WINDOW_MS / 1000 + 0.03, LOADED);
    expect(r.px).toBeCloseTo(8 * 1.25, 9);
    // At 10 px the frame is 17.9 ms: inside the band between missing 60 and holding it, so the governor stops there.
    r = run(g, r.endMs, 10, LOADED);
    expect(r.px).toBeCloseTo(10, 9);
    expect(g.coarsened).toBe(true);
    expect(g.cappedFps).toBeNull();
  });

  it('goes as far as its ceiling for a load the terrain keeps relieving', () => {
    const g = new TerrainDetailGovernor();
    // 120 ms at the floor, still 30 ms at the ceiling: every step helps, and the last one is not enough.
    const heavy = (px: number): number => 120 * (8 / px);
    const r = run(g, 0, 20, heavy);
    expect(r.px).toBe(TERRAIN_MAX_PX);
  });

  it('finds a cap: coarser and no faster, it gives the viewer their detail back and holds it', () => {
    const g = new TerrainDetailGovernor();
    let r = run(g, 0, 3, CAPPED_MS);
    expect(g.cappedFps).toBeCloseTo(30, 6);
    expect(r.px).toBe(8);
    r = run(g, r.endMs, 30, CAPPED_MS);
    expect(r.px).toBe(8);
    expect(g.coarsened).toBe(false);
  });

  it('treats a load that the terrain cannot relieve as a cap too: coarser ground would buy nothing', () => {
    const g = new TerrainDetailGovernor();
    const r = run(g, 0, 10, SLOW_MS);
    expect(r.px).toBe(8);
    expect(g.cappedFps).toBeCloseTo(40, 6);
  });

  it('coarsens again under a cap when the frame gets clearly slower than it: a load on top', () => {
    const g = new TerrainDetailGovernor();
    let r = run(g, 0, 3, CAPPED_MS);
    expect(g.cappedFps).not.toBeNull();
    const heavier = (px: number): number => Math.max(CAPPED_MS, 50 * (8 / px) ** 1.5);
    r = run(g, r.endMs, 2, heavier);
    expect(r.px).toBeGreaterThan(8);
    expect(g.cappedFps).toBeNull();
  });

  it('forgets a cap once the frame holds 60', () => {
    const g = new TerrainDetailGovernor();
    let r = run(g, 0, 3, CAPPED_MS);
    expect(g.cappedFps).not.toBeNull();
    r = run(g, r.endMs, 2, HOLD_MS);
    expect(g.cappedFps).toBeNull();
    expect(r.px).toBe(8);
  });

  it('does not coarsen for vsync jitter: a frame a few percent past the budget is holding 60', () => {
    const g = new TerrainDetailGovernor();
    const { px } = run(g, 0, 10, HOLD_MS * 1.04);
    expect(px).toBe(8);
  });

  it('ignores intervals past 250 ms: a hidden tab or a debugger is not a frame', () => {
    const g = new TerrainDetailGovernor();
    const { px } = run(g, 0, 10, 400);
    expect(px).toBe(8);
  });

  it('refines back to the slider slowly, a step per calm period at 60 fps, never under it', () => {
    const g = new TerrainDetailGovernor();
    let r = run(g, 0, 3, LOADED);
    r = run(g, r.endMs, FLUSH_S, HOLD_MS);
    const coarse = r.px;
    expect(coarse).toBeGreaterThan(8);

    // Holding 60 — but not for long enough to refine.
    r = run(g, r.endMs, (TERRAIN_CALM_MS - 600) / 1000, HOLD_MS);
    expect(r.px).toBe(coarse);

    r = run(g, r.endMs, 1, HOLD_MS);
    expect(r.px).toBeCloseTo(coarse / 1.25, 9);
    r = run(g, r.endMs, 60, HOLD_MS);
    expect(r.px).toBe(8);
    expect(g.coarsened).toBe(false);
  });

  it('waits twice as long after a refinement the machine could not hold, so it does not bounce between two levels', () => {
    const g = new TerrainDetailGovernor();
    let r = run(g, 0, 3, LOADED);
    r = run(g, r.endMs, FLUSH_S, HOLD_MS);
    const coarse = r.px;

    // Holds 60 long enough to refine one step; at the finer level it drops under 60 again at once.
    r = run(g, r.endMs, TERRAIN_CALM_MS / 1000 + 0.6, HOLD_MS);
    expect(r.px).toBeCloseTo(coarse / 1.25, 9);
    r = run(g, r.endMs, FLUSH_S, SLOW_MS);
    expect(r.px).toBeCloseTo(coarse, 9);
    expect(g.calmWaitMs).toBe(TERRAIN_CALM_MS * 2);

    // The old wait is no longer enough to try again.
    r = run(g, r.endMs, FLUSH_S, HOLD_MS);
    r = run(g, r.endMs, TERRAIN_CALM_MS / 1000 + 0.6, HOLD_MS);
    expect(r.px).toBeCloseTo(coarse, 9);
    r = run(g, r.endMs, TERRAIN_CALM_MS / 1000, HOLD_MS);
    expect(r.px).toBeCloseTo(coarse / 1.25, 9);
  });

  it('caps the backoff', () => {
    const g = new TerrainDetailGovernor();
    let r = run(g, 0, 3, LOADED);
    for (let i = 0; i < 12; i++) {
      r = run(g, r.endMs, FLUSH_S, HOLD_MS);
      r = run(g, r.endMs, g.calmWaitMs / 1000 + 0.6, HOLD_MS);
      r = run(g, r.endMs, FLUSH_S, SLOW_MS);
    }

    expect(g.calmWaitMs).toBe(TERRAIN_MAX_CALM_MS);
  });

  it('starts again from the slider when the viewer moves it', () => {
    const g = new TerrainDetailGovernor();
    const r = run(g, 0, 3, LOADED);
    expect(r.px).toBeGreaterThan(8);
    expect(g.update(r.endMs + 16, 16, 4)).toBe(4);
    expect(g.coarsened).toBe(false);
  });

  it('never goes finer than a slider set past its own ceiling', () => {
    const g = new TerrainDetailGovernor(TERRAIN_TARGET_FPS, 16);
    const { px } = run(g, 0, 10, (px) => 60 * (24 / px), 24);
    expect(px).toBe(24);
  });
});
