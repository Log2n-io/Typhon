import { beforeEach, describe, expect, it } from 'vitest';
import {
  ThreadKind,
  useProfilerSessionStore,
  type BuildProgressPayload,
} from '@/stores/useProfilerSessionStore';
import type {
  ChunkManifestEntryDto,
  GlobalMetricsDto,
  ProfilerMetadataDto,
  TickSummaryDto,
} from '@/api/generated/model';

/**
 * #289 — post-unification tests. The store no longer keeps a per-batch ring buffer; instead the SSE growth deltas
 * mutate `metadata.tickSummaries` / `metadata.chunkManifest` / `metadata.globalMetrics` directly via dedicated
 * appender / updater actions.
 */

function makeMetadata(overrides: Partial<ProfilerMetadataDto> = {}): ProfilerMetadataDto {
  return {
    fingerprint: 'abc',
    header: { timestampFrequency: 10_000_000 } as ProfilerMetadataDto['header'],
    systems: [],
    archetypes: [],
    componentTypes: [],
    spanNames: {},
    globalMetrics: {} as ProfilerMetadataDto['globalMetrics'],
    tickSummaries: [],
    chunkManifest: [],
    gcSuspensions: [],
    phases: [],
    systemTickSummaries: [],
    queueTickSummaries: [],
    postTickSummaries: [],
    queueIdToName: {},
    ...overrides,
  };
}

function makeTickSummary(tickNumber: number, durationUs = 16): TickSummaryDto {
  return {
    tickNumber,
    startUs: tickNumber * 1000,
    durationUs,
    eventCount: 5,
    maxSystemDurationUs: 10,
    activeSystemsBitmask: '0',
  } as TickSummaryDto;
}

function makeChunkEntry(fromTick: number, toTick: number): ChunkManifestEntryDto {
  return {
    fromTick,
    toTick,
    eventCount: 1,
    isContinuation: false,
  };
}

describe('useProfilerSessionStore — lifecycle', () => {
  beforeEach(() => {
    useProfilerSessionStore.getState().reset();
  });

  it('setMetadata clears a prior buildError', () => {
    useProfilerSessionStore.getState().setBuildError('oops');
    expect(useProfilerSessionStore.getState().buildError).toBe('oops');

    useProfilerSessionStore.getState().setMetadata(makeMetadata());
    expect(useProfilerSessionStore.getState().metadata).not.toBeNull();
    expect(useProfilerSessionStore.getState().buildError).toBeNull();
  });

  it('setBuildProgress updates the current frame without touching other fields', () => {
    const p: BuildProgressPayload = { phase: 'building', bytesRead: 100, totalBytes: 1000 };
    useProfilerSessionStore.getState().setBuildProgress(p);
    expect(useProfilerSessionStore.getState().buildProgress).toBe(p);
    expect(useProfilerSessionStore.getState().metadata).toBeNull();
  });

  it('setIsLive and setConnectionStatus mirror the server runtime state', () => {
    useProfilerSessionStore.getState().setIsLive(true);
    expect(useProfilerSessionStore.getState().isLive).toBe(true);

    useProfilerSessionStore.getState().setConnectionStatus('connected');
    expect(useProfilerSessionStore.getState().connectionStatus).toBe('connected');
  });
});

describe('useProfilerSessionStore — growth deltas', () => {
  beforeEach(() => {
    useProfilerSessionStore.getState().reset();
  });

  it('appendTickSummary mutates metadata.tickSummaries and tracks latestTickNumber', () => {
    useProfilerSessionStore.getState().setMetadata(makeMetadata());
    useProfilerSessionStore.getState().appendTickSummary(makeTickSummary(1));
    useProfilerSessionStore.getState().appendTickSummary(makeTickSummary(2));

    const s = useProfilerSessionStore.getState();
    expect((s.metadata!.tickSummaries ?? []).map((t) => t.tickNumber)).toEqual([1, 2]);
    expect(s.latestTickNumber).toBe(2);
  });

  it('appendTickSummary is a no-op when metadata is null', () => {
    useProfilerSessionStore.getState().appendTickSummary(makeTickSummary(1));
    expect(useProfilerSessionStore.getState().metadata).toBeNull();
  });

  it('appendChunkEntry grows metadata.chunkManifest', () => {
    useProfilerSessionStore.getState().setMetadata(makeMetadata());
    useProfilerSessionStore.getState().appendChunkEntry(makeChunkEntry(1, 10));
    useProfilerSessionStore.getState().appendChunkEntry(makeChunkEntry(10, 20));

    const manifest = useProfilerSessionStore.getState().metadata!.chunkManifest ?? [];
    expect(manifest).toHaveLength(2);
    expect(manifest[0].fromTick).toBe(1);
    expect(manifest[1].toTick).toBe(20);
  });

  it('updateGlobalMetrics replaces metadata.globalMetrics', () => {
    useProfilerSessionStore.getState().setMetadata(makeMetadata());
    const next: GlobalMetricsDto = {
      globalStartUs: 0,
      globalEndUs: 1000,
      maxTickDurationUs: 50,
      maxSystemDurationUs: 30,
      p95TickDurationUs: 16,
      totalEvents: 12345,
      totalTicks: 60,
      systemAggregates: [],
    };
    useProfilerSessionStore.getState().updateGlobalMetrics(next);

    expect(useProfilerSessionStore.getState().metadata!.globalMetrics).toBe(next);
  });
});

describe('useProfilerSessionStore — reset', () => {
  beforeEach(() => {
    useProfilerSessionStore.getState().reset();
  });

  it('reset wipes every field back to the initial state', () => {
    useProfilerSessionStore.getState().setMetadata(makeMetadata());
    useProfilerSessionStore.getState().setBuildProgress({ phase: 'done' });
    useProfilerSessionStore.getState().setBuildError('prior failure');
    useProfilerSessionStore.getState().setIsLive(true);
    useProfilerSessionStore.getState().setConnectionStatus('reconnecting');
    useProfilerSessionStore.getState().appendTickSummary(makeTickSummary(1));
    useProfilerSessionStore.getState().setLiveFollowActive(false);
    useProfilerSessionStore.getState().setSlotVisibility(3, false);
    useProfilerSessionStore.getState().setSystemVisibility(7, false);

    useProfilerSessionStore.getState().reset();

    const s = useProfilerSessionStore.getState();
    expect(s.metadata).toBeNull();
    expect(s.buildProgress).toBeNull();
    expect(s.buildError).toBeNull();
    expect(s.isLive).toBe(false);
    expect(s.connectionStatus).toBeNull();
    expect(s.latestTickNumber).toBe(0);
    expect(s.liveFollowActive).toBe(true);
    expect(s.liveThreadInfos.size).toBe(0);
    expect(s.slotVisibility).toEqual({});
    expect(s.systemVisibility).toEqual({});
  });
});

describe('useProfilerSessionStore — thread infos', () => {
  beforeEach(() => {
    useProfilerSessionStore.getState().reset();
  });

  it('upsertThreadInfo stores name and kind keyed by slot', () => {
    useProfilerSessionStore.getState().upsertThreadInfo({ threadSlot: 5, name: 'Typhon.Worker-2', managedThreadId: 17, kind: ThreadKind.Worker });
    const info = useProfilerSessionStore.getState().liveThreadInfos.get(5);
    expect(info).toBeDefined();
    expect(info!.name).toBe('Typhon.Worker-2');
    expect(info!.kind).toBe(ThreadKind.Worker);
  });

  it('upsertThreadInfo is idempotent on identical name + kind (referential equality preserved)', () => {
    useProfilerSessionStore.getState().upsertThreadInfo({ threadSlot: 0, name: 'Main', managedThreadId: 1, kind: ThreadKind.Main });
    const before = useProfilerSessionStore.getState().liveThreadInfos;
    useProfilerSessionStore.getState().upsertThreadInfo({ threadSlot: 0, name: 'Main', managedThreadId: 1, kind: ThreadKind.Main });
    expect(useProfilerSessionStore.getState().liveThreadInfos).toBe(before);
  });

  it('upsertThreadInfo updates when kind changes (slot reclaim by a different thread category)', () => {
    useProfilerSessionStore.getState().upsertThreadInfo({ threadSlot: 5, name: 'X', managedThreadId: 17, kind: ThreadKind.Worker });
    useProfilerSessionStore.getState().upsertThreadInfo({ threadSlot: 5, name: 'X', managedThreadId: 99, kind: ThreadKind.Other });
    expect(useProfilerSessionStore.getState().liveThreadInfos.get(5)!.kind).toBe(ThreadKind.Other);
  });
});

describe('useProfilerSessionStore — slot/system visibility', () => {
  beforeEach(() => {
    useProfilerSessionStore.getState().reset();
  });

  it('setSlotVisibility(false) records false; setSlotVisibility(true) deletes the key', () => {
    useProfilerSessionStore.getState().setSlotVisibility(3, false);
    expect(useProfilerSessionStore.getState().slotVisibility).toEqual({ 3: false });
    useProfilerSessionStore.getState().setSlotVisibility(3, true);
    expect(useProfilerSessionStore.getState().slotVisibility).toEqual({});
  });

  it('setSlotVisibility is a no-op when state already matches (referential equality)', () => {
    useProfilerSessionStore.getState().setSlotVisibility(3, false);
    const before = useProfilerSessionStore.getState().slotVisibility;
    useProfilerSessionStore.getState().setSlotVisibility(3, false);
    expect(useProfilerSessionStore.getState().slotVisibility).toBe(before);
  });

  it('setManySlotVisibility merges and clearSlotVisibility wipes', () => {
    useProfilerSessionStore.getState().setManySlotVisibility({ 1: false, 2: false, 3: true });
    expect(useProfilerSessionStore.getState().slotVisibility).toEqual({ 1: false, 2: false });
    useProfilerSessionStore.getState().clearSlotVisibility();
    expect(useProfilerSessionStore.getState().slotVisibility).toEqual({});
  });

  it('setSystemVisibility behaves the same way', () => {
    useProfilerSessionStore.getState().setSystemVisibility(7, false);
    expect(useProfilerSessionStore.getState().systemVisibility).toEqual({ 7: false });
    useProfilerSessionStore.getState().clearSystemVisibility();
    expect(useProfilerSessionStore.getState().systemVisibility).toEqual({});
  });
});

describe('useProfilerSessionStore — applyLiveBatch (rAF-coalesced SSE)', () => {
  beforeEach(() => {
    useProfilerSessionStore.getState().reset();
  });

  function tick(n: number): TickSummaryDto {
    return {
      tickNumber: n,
      durationUs: 1.0,
      eventCount: 0,
      maxSystemDurationUs: 0,
      activeSystemsBitmask: '0',
      startUs: n * 1000,
      // v9 + v11 fields — required on the DTO (Orval emits them as `number | string`); 0 mirrors the
      // server-side default for traces that pre-date the additions.
      overloadLevel: 0,
      tickMultiplier: 0,
      metronomeWaitUs: 0,
      metronomeIntentClass: 0,
      consecutiveOverrun: 0,
      consecutiveUnderrun: 0,
    };
  }

  function chunk(fromTick: number, toTick: number): ChunkManifestEntryDto {
    return {
      fromTick,
      toTick,
      eventCount: 0,
      isContinuation: false,
    };
  }

  it('appends tickSummaries from a multi-event batch in a single mutation', () => {
    useProfilerSessionStore.getState().setMetadata(makeMetadata());
    const before = useProfilerSessionStore.getState().metadata!;

    useProfilerSessionStore.getState().applyLiveBatch([
      { kind: 'tickSummaryAdded', tickSummary: tick(1) },
      { kind: 'tickSummaryAdded', tickSummary: tick(2) },
      { kind: 'tickSummaryAdded', tickSummary: tick(3) },
    ]);

    const after = useProfilerSessionStore.getState().metadata!;
    expect(after.tickSummaries).toHaveLength(3);
    expect(after.tickSummaries!.map((t) => Number(t.tickNumber))).toEqual([1, 2, 3]);
    // Single mutation = single new metadata reference (vs 3 in the per-event path).
    expect(after).not.toBe(before);
    expect(useProfilerSessionStore.getState().latestTickNumber).toBe(3);
  });

  it('coalesces ticks + chunks + threadInfos in one batch into a single set() call', () => {
    useProfilerSessionStore.getState().setMetadata(makeMetadata());
    const before = useProfilerSessionStore.getState();

    useProfilerSessionStore.getState().applyLiveBatch([
      { kind: 'tickSummaryAdded', tickSummary: tick(1) },
      { kind: 'chunkAdded', chunkEntry: chunk(0, 1) },
      { kind: 'threadInfoAdded', threadInfo: { threadSlot: 5, name: 'Worker', managedThreadId: 42, kind: ThreadKind.Worker } },
      { kind: 'tickSummaryAdded', tickSummary: tick(2) },
      { kind: 'chunkAdded', chunkEntry: chunk(1, 3) },
    ]);

    const after = useProfilerSessionStore.getState();
    expect(after.metadata!.tickSummaries).toHaveLength(2);
    expect(after.metadata!.chunkManifest).toHaveLength(2);
    expect(after.liveThreadInfos.get(5)).toEqual({ name: 'Worker', kind: ThreadKind.Worker });
    // Both arrays must share a single new metadata reference (the batch produced one clone, not two).
    expect(after.metadata).not.toBe(before.metadata);
  });

  it('honours last-wins for globalMetricsUpdated within a batch', () => {
    useProfilerSessionStore.getState().setMetadata(makeMetadata());
    const m1: GlobalMetricsDto = { p95TickDurationUs: 1, totalEvents: 1 } as GlobalMetricsDto;
    const m2: GlobalMetricsDto = { p95TickDurationUs: 2, totalEvents: 2 } as GlobalMetricsDto;
    const m3: GlobalMetricsDto = { p95TickDurationUs: 3, totalEvents: 3 } as GlobalMetricsDto;

    useProfilerSessionStore.getState().applyLiveBatch([
      { kind: 'globalMetricsUpdated', globalMetrics: m1 },
      { kind: 'globalMetricsUpdated', globalMetrics: m2 },
      { kind: 'globalMetricsUpdated', globalMetrics: m3 },
    ]);

    expect(useProfilerSessionStore.getState().metadata!.globalMetrics).toBe(m3);
  });

  it('mid-batch metadata snapshot resets pending appends from before it', () => {
    useProfilerSessionStore.getState().setMetadata(makeMetadata());
    const fresh = makeMetadata({ tickSummaries: [tick(100)] });

    useProfilerSessionStore.getState().applyLiveBatch([
      { kind: 'tickSummaryAdded', tickSummary: tick(1) },  // pre-snapshot — discarded
      { kind: 'tickSummaryAdded', tickSummary: tick(2) },  // pre-snapshot — discarded
      { kind: 'metadata', metadata: fresh },
      { kind: 'tickSummaryAdded', tickSummary: tick(101) }, // post-snapshot — kept
    ]);

    const after = useProfilerSessionStore.getState().metadata!;
    expect(after.tickSummaries!.map((t) => Number(t.tickNumber))).toEqual([100, 101]);
    expect(useProfilerSessionStore.getState().latestTickNumber).toBe(101);
  });

  it('treats shutdown event as terminal connection-status change', () => {
    useProfilerSessionStore.getState().setMetadata(makeMetadata());
    useProfilerSessionStore.getState().applyLiveBatch([
      { kind: 'tickSummaryAdded', tickSummary: tick(1) },
      { kind: 'shutdown', status: 'engine_shutdown' },
    ]);
    expect(useProfilerSessionStore.getState().connectionStatus).toBe('disconnected');
    // Tick before shutdown still applies — the user can still inspect what was captured.
    expect(useProfilerSessionStore.getState().metadata!.tickSummaries).toHaveLength(1);
  });

  it('skips redundant threadInfo updates already present with same name + kind', () => {
    useProfilerSessionStore.getState().setMetadata(makeMetadata());
    const liveBefore = useProfilerSessionStore.getState().liveThreadInfos;

    useProfilerSessionStore.getState().applyLiveBatch([
      { kind: 'threadInfoAdded', threadInfo: { threadSlot: 0, name: 'Main', managedThreadId: 1, kind: ThreadKind.Main } },
    ]);
    useProfilerSessionStore.getState().applyLiveBatch([
      { kind: 'threadInfoAdded', threadInfo: { threadSlot: 0, name: 'Main', managedThreadId: 1, kind: ThreadKind.Main } },
    ]);
    // Second batch was a no-op for liveThreadInfos identity (same name + kind already present).
    const after = useProfilerSessionStore.getState().liveThreadInfos;
    expect(after.get(0)).toEqual({ name: 'Main', kind: ThreadKind.Main });
    expect(after).not.toBe(liveBefore);
  });
});
