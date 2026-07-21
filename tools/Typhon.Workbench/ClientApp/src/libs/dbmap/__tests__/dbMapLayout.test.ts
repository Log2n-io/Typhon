import { describe, expect, it } from 'vitest';
import { buildLayout } from '../dbMapLayout';
import { PAGE_SIZE } from '../types';

// Covers the down-sampling layout math (Module 15, A4 — §5.5): the WAL region must be measured in the same
// cell unit as the data grid, so the data ↔ WAL area ratio stays strictly proportional to bytes on disk even
// when the coarse map is down-sampled.

describe('buildLayout', () => {
  it('hides the WAL region by default even for a non-empty WAL (Module 08 pending)', () => {
    // Production path: showWal defaults false, so the empty WAL placeholder is never laid out.
    const layout = buildLayout(256, PAGE_SIZE * 1000, 4, 1);
    expect(layout.dataRect.w).toBe(layout.side);
    expect(layout.walRect).toBeNull();
    // The camera fits just the data square — no dead space where the WAL rectangle used to be.
    expect(layout.worldBounds.w).toBe(layout.side);
  });

  it('places the data square and a WAL region for a non-empty WAL when showWal is enabled', () => {
    const layout = buildLayout(256, PAGE_SIZE * 1000, 4, 1, true);
    expect(layout.dataRect.w).toBe(layout.side);
    expect(layout.walRect).not.toBeNull();
  });

  it('omits the WAL region when the database has no WAL', () => {
    const layout = buildLayout(256, 0, 4, 1, true);
    expect(layout.walRect).toBeNull();
  });

  it('keeps the data↔WAL area ratio honest under down-sampling (showWal)', () => {
    // The same database rendered exact vs down-sampled ×4: the data grid has 4× fewer cells, so the WAL —
    // measured in that same cell unit — is 4× narrower. The WAL-width ÷ data-width ratio must be unchanged.
    const exact = buildLayout(1024, PAGE_SIZE * 4096, 5, 1, true);
    const sampled = buildLayout(256, PAGE_SIZE * 4096, 4, 4, true);
    const exactRatio = exact.walRect!.w / exact.dataRect.w;
    const sampledRatio = sampled.walRect!.w / sampled.dataRect.w;
    expect(sampledRatio).toBeCloseTo(exactRatio, 5);
  });
});
