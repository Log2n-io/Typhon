import { describe, expect, it } from 'vitest';
import { isInstantKind } from '../chunkDecoder';
import { TraceEventKind } from '@/libs/profiler/model/types';

// `isInstantKind` is a hand-maintained MIRROR of the C# ladder in
// `src/Typhon.Profiler/TraceEventKind.cs` (`TraceEventKindExtensions.IsSpan`). Nothing in the build ties the two
// together, so a kind added on the engine side drifts here silently: the record still parses (`pos += size` keeps
// everything aligned) but 25 bytes of payload are read as a span header, producing a fabricated duration and
// parent/child links, and the event renders as a phantom span named `Kind[N]`.
//
// The engine side has its own guard — TraceEventShapeConsistencyTests cross-checks every `[TraceEvent(Shape = …)]`
// declaration against IsSpan by reflection. This is the client half: the instant list transcribed from that ladder,
// so a divergence fails here rather than in someone's timeline.
//
// Source of truth, in ladder order (C# returns false ⇒ instant):
//   <10 · 76, 77 · 36 · 90-116 · 127-135, 137, 140-142, 144, 145 · 146-148, 151, 153, 154, 156-158, 161, 162 ·
//   166-172 · 176, 178, 180, 182, 183, 185, 186 · 191, 197, 200, 202, 203, 206-208, 211-213 ·
//   217, 218, 220, 225, 228, 233, 234 · 242, 244 · 247, 248 · 254
const INSTANT_KINDS: readonly number[] = [
  0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
  36, 76, 77,
  90, 100, 116,
  127, 131, 135, 137, 140, 142, 144, 145,
  146, 147, 148, 151, 153, 154, 156, 157, 158, 161, 162,
  166, 169, 172,
  176, 178, 180, 182, 183, 185, 186,
  191, 197, 200, 202, 203, 206, 207, 208, 211, 212, 213,
  217, 218, 220, 225, 228, 233, 234,
  242, 244,
  247, 248,
  254,
];

// Deliberately sampled from ranges the ladder does NOT carve out — several sit immediately beside an instant, which is
// where an off-by-one in a range check would land.
const SPAN_KINDS: readonly number[] = [
  10, 20, 23, 30, 31, 32, 33, 34, 35, 40, 41,
  117, 126, 136, 138, 139, 143,
  149, 150, 152, 155, 159, 160, 163, 164, 165,
  173, 174, 175, 177, 179, 181, 184,
  187, 190, 192, 196, 198, 199, 201, 204, 205, 209, 210,
  214, 216, 219, 221, 226, 227, 229, 232,
  235, 240, 241, 243, 245,
];

describe('isInstantKind — mirror of the C# IsSpan ladder', () => {
  it.each(INSTANT_KINDS)('kind %i is an instant', (kind) => {
    expect(isInstantKind(kind)).toBe(true);
  });

  it.each(SPAN_KINDS)('kind %i is a span', (kind) => {
    expect(isInstantKind(kind)).toBe(false);
  });

  it('EcsSpawnBatch (36) is an instant, unlike its span neighbours', () => {
    // The regression: declared Shape = Instant on the producer (#620) but absent from both ladders, so it decoded as a
    // span and showed up on a thread lane as `Kind[36]` with a fabricated duration.
    expect(isInstantKind(TraceEventKind.EcsSpawnBatch)).toBe(true);
    expect(isInstantKind(TraceEventKind.EcsSpawn)).toBe(false);
    expect(isInstantKind(TraceEventKind.EcsDestroy)).toBe(false);
  });
});
