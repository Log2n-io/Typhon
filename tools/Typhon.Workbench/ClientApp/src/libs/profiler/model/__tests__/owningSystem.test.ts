import { describe, expect, it } from 'vitest';
import type { ChunkSpan, SpanData } from '@/libs/profiler/model/traceModel';
import { resolveOwningSystem } from '@/libs/profiler/model/owningSystem';

// AC3.2 — a span carries only its thread slot; its owning system is the scheduler chunk occupying that slot
// at the span's instant. resolveOwningSystem is the pure half of the bus projection (TimeArea calls setSystem
// with its result). Infra spans (no owning chunk) and non-span/chunk kinds resolve to null (clears the slot).

function chunk(systemName: string, threadSlot: number, startUs: number, endUs: number): ChunkSpan {
  return {
    systemIndex: 0, systemName, chunkIndex: 0, threadSlot,
    startUs, endUs, durationUs: endUs - startUs,
    entitiesProcessed: 0, totalChunks: 1, isParallel: false,
  };
}

function span(threadSlot: number, startUs: number): SpanData {
  return { kind: 100 as SpanData['kind'], name: 's', threadSlot, startUs, endUs: startUs + 1, durationUs: 1 };
}

describe('resolveOwningSystem', () => {
  const chunks = [chunk('Movement', 3, 100, 200), chunk('Physics', 5, 100, 200)];

  it('chunk selection → its systemName (a chunk is a system slot)', () => {
    expect(resolveOwningSystem({ kind: 'chunk', chunk: chunk('Render', 1, 0, 10) }, chunks)).toBe('Render');
  });

  it('span inside a chunk on the same slot → that chunk system', () => {
    expect(resolveOwningSystem({ kind: 'span', span: span(3, 150) }, chunks)).toBe('Movement');
    expect(resolveOwningSystem({ kind: 'span', span: span(5, 100) }, chunks)).toBe('Physics'); // inclusive start
  });

  it('infra span — no containing chunk on its slot/time → null', () => {
    expect(resolveOwningSystem({ kind: 'span', span: span(9, 150) }, chunks)).toBeNull(); // slot with no chunk
    expect(resolveOwningSystem({ kind: 'span', span: span(3, 200) }, chunks)).toBeNull(); // exclusive end
    expect(resolveOwningSystem({ kind: 'span', span: span(3, 999) }, chunks)).toBeNull(); // outside window
  });

  it('tick / phase / phase-marker are not system-scoped → null', () => {
    expect(resolveOwningSystem({ kind: 'tick', tickNumber: 1 }, chunks)).toBeNull();
    expect(resolveOwningSystem({ kind: 'phase', phase: undefined as never, tickNumber: 1 }, chunks)).toBeNull();
    expect(resolveOwningSystem({ kind: 'phase-marker', marker: undefined as never, tickNumber: 1 }, chunks)).toBeNull();
  });
});
