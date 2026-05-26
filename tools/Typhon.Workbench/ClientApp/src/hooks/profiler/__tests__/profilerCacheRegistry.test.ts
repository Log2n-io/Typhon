// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  acquireSessionCache,
  releaseSessionCache,
  subscribeSessionCache,
  getSessionCacheSnapshot,
  _entryByIdOrNull,
  _registeredSessionIds,
  _resetRegistry,
  _scheduleBumpForTest,
  _bumpImmediateForTest,
  _pendingBumpRafIdForTest,
} from '../profilerCacheRegistry';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import type { TraceMetadata } from '@/libs/profiler/model/types';

// Stub `assembleTickViewAndNumbers` so the bump tests can exercise the dedup + cache-version guard
// without needing a real chunk cache populated with decoded chunks. The real function is internally
// memoised on `cache.entriesVersion` (chunkCache.ts:282) — exactly the gate #4b leans on; the spy
// here lets us count how many times the bump-immediate codepath chose to invoke it.
vi.mock('@/libs/profiler/cache/chunkCache', async () => {
  const actual = await vi.importActual<typeof import('@/libs/profiler/cache/chunkCache')>(
    '@/libs/profiler/cache/chunkCache',
  );
  return {
    ...actual,
    assembleTickViewAndNumbers: vi.fn(() => ({
      tickData: [],
      gaugeSeries: new Map(),
      gaugeCapacities: new Map(),
      memoryAllocEvents: [],
      gcEvents: [],
      gcSuspensions: [],
      threadNames: new Map(),
      threadKinds: new Map(),
      offCpuBySlot: new Map(),
    })),
  };
});

/**
 * Registry-level tests for the per-session shared cache (#377 perf follow-up). Covers the contract
 * promises:
 *   - `acquire` is idempotent on sessionId — multiple consumers get the *same* entry.
 *   - `release` is refcounted — the entry survives until the last consumer releases.
 *   - `release` past zero destroys the entry: in-flight aborted, store subscriptions detached,
 *     entry evicted from the registry.
 *   - `subscribe`/`getSnapshot` return stable references between data changes (useSyncExternalStore
 *     contract).
 *   - `_entryByIdOrNull` is a non-acquiring lookup — refCount untouched.
 */

afterEach(() => {
  _resetRegistry();
  useProfilerSessionStore.getState().reset();
  useProfilerViewStore.setState({ viewRange: { startUs: 0, endUs: 0 } });
});

describe('profilerCacheRegistry — acquire / release lifecycle', () => {
  it('returns the same entry instance for the same sessionId across multiple acquires (dedup)', () => {
    const a = acquireSessionCache('sess-A', true);
    const b = acquireSessionCache('sess-A', true);
    expect(a).toBe(b);
    expect(_registeredSessionIds()).toEqual(['sess-A']);
  });

  it('keeps the entry alive while refCount > 0', () => {
    acquireSessionCache('sess-A', true);
    acquireSessionCache('sess-A', true);
    releaseSessionCache('sess-A'); // refCount: 2 → 1
    expect(_entryByIdOrNull('sess-A')).not.toBeNull();
  });

  it('evicts the entry when the last consumer releases', () => {
    acquireSessionCache('sess-A', true);
    releaseSessionCache('sess-A'); // refCount: 1 → 0
    expect(_entryByIdOrNull('sess-A')).toBeNull();
    expect(_registeredSessionIds()).toEqual([]);
  });

  it('separate sessionIds get separate entries', () => {
    const a = acquireSessionCache('sess-A', true);
    const b = acquireSessionCache('sess-B', false);
    expect(a).not.toBe(b);
    expect([..._registeredSessionIds()].sort()).toEqual(['sess-A', 'sess-B']);
  });

  it('upgrades isLive from false → true if a later consumer asks for live', () => {
    const a = acquireSessionCache('sess-A', false);
    expect(a.isLive).toBe(false);
    const b = acquireSessionCache('sess-A', true);
    expect(b).toBe(a);
    expect(a.isLive).toBe(true);
  });

  it('does NOT downgrade isLive from true → false (live is sticky)', () => {
    const a = acquireSessionCache('sess-A', true);
    expect(a.isLive).toBe(true);
    acquireSessionCache('sess-A', false);
    expect(a.isLive).toBe(true);
  });

  it('release of an unknown sessionId is a no-op (no throw)', () => {
    expect(() => releaseSessionCache('does-not-exist')).not.toThrow();
  });
});

describe('profilerCacheRegistry — subscribe / snapshot semantics', () => {
  it('subscribers fire when the registry marks the entry dirty', () => {
    const entry = acquireSessionCache('sess-A', true);
    const listener = vi.fn();
    const unsub = subscribeSessionCache(entry, listener);
    // A no-op store mutation that still triggers the subscription handler — flip viewRange.
    useProfilerViewStore.setState({ viewRange: { startUs: 1, endUs: 2 } });
    expect(listener).toHaveBeenCalled();
    unsub();
  });

  it('unsubscribed listeners no longer fire', () => {
    const entry = acquireSessionCache('sess-A', true);
    const listener = vi.fn();
    const unsub = subscribeSessionCache(entry, listener);
    unsub();
    useProfilerViewStore.setState({ viewRange: { startUs: 1, endUs: 2 } });
    expect(listener).not.toHaveBeenCalled();
  });

  it('getSnapshot returns the same reference when nothing has changed (useSyncExternalStore contract)', () => {
    const entry = acquireSessionCache('sess-A', true);
    const s1 = getSessionCacheSnapshot(entry);
    const s2 = getSessionCacheSnapshot(entry);
    expect(s1).toBe(s2);
  });

  it('getSnapshot returns a new reference after a dirty mark', () => {
    const entry = acquireSessionCache('sess-A', true);
    const s1 = getSessionCacheSnapshot(entry);
    // Trigger a dirty mark by mutating the view store.
    useProfilerViewStore.setState({ viewRange: { startUs: 1, endUs: 2 } });
    const s2 = getSessionCacheSnapshot(entry);
    expect(s2).not.toBe(s1);
  });
});

describe('profilerCacheRegistry — store subscriptions detached on destroy', () => {
  it('does not respond to store mutations after the entry is destroyed', () => {
    const entry = acquireSessionCache('sess-A', true);
    const listener = vi.fn();
    subscribeSessionCache(entry, listener);
    releaseSessionCache('sess-A'); // destroys the entry + detaches store subscriptions
    listener.mockReset();
    useProfilerViewStore.setState({ viewRange: { startUs: 99, endUs: 100 } });
    expect(listener).not.toHaveBeenCalled();
  });
});

describe('profilerCacheRegistry — _entryByIdOrNull is non-acquiring', () => {
  it('returns null when no consumer has acquired the session', () => {
    expect(_entryByIdOrNull('sess-A')).toBeNull();
  });

  it('returns the entry without bumping refCount', () => {
    acquireSessionCache('sess-A', true);
    const peek1 = _entryByIdOrNull('sess-A');
    const peek2 = _entryByIdOrNull('sess-A');
    expect(peek1).toBe(peek2);
    // One acquire → one release should destroy. If _entryByIdOrNull bumped refCount, we'd need more releases.
    releaseSessionCache('sess-A');
    expect(_entryByIdOrNull('sess-A')).toBeNull();
  });
});

// ── #4a rAF-coalesce bumpEntriesVersion ─────────────────────────────────────────────────────────
// Stub `requestAnimationFrame`/`cancelAnimationFrame` with an explicit queue so coalesce behaviour
// is observable from the test (count enqueued rAFs, drain manually). Same pattern as
// `useProfilerStatsWriter.test.tsx`.
describe('profilerCacheRegistry — #4a bumpEntriesVersion rAF coalescing', () => {
  const rafQueue: Array<{ id: number; cb: FrameRequestCallback }> = [];
  let nextRafId = 1;

  function flushRaf(): void {
    const drained = rafQueue.splice(0);
    for (const { cb } of drained) cb(performance.now());
  }

  beforeEach(() => {
    rafQueue.length = 0;
    nextRafId = 1;
    globalThis.requestAnimationFrame = ((cb: FrameRequestCallback) => {
      const id = nextRafId++;
      rafQueue.push({ id, cb });
      return id;
    }) as typeof requestAnimationFrame;
    globalThis.cancelAnimationFrame = ((id: number) => {
      const idx = rafQueue.findIndex((q) => q.id === id);
      if (idx >= 0) rafQueue.splice(idx, 1);
    }) as typeof cancelAnimationFrame;
  });

  it('collapses N rapid scheduled bumps into a single rAF', () => {
    const entry = acquireSessionCache('sess-A', true);
    entry.traceMetadata = { systems: [] } as unknown as TraceMetadata;
    entry.cache.entriesVersion = 1; // Pretend a chunk landed.

    _scheduleBumpForTest(entry);
    _scheduleBumpForTest(entry);
    _scheduleBumpForTest(entry);
    expect(rafQueue.length).toBe(1); // Three calls, one pending rAF.

    const listener = vi.fn();
    subscribeSessionCache(entry, listener);
    flushRaf();
    expect(listener).toHaveBeenCalledTimes(1); // Single markDirty notification for all three calls.
  });

  it('subsequent bumps after the rAF drains schedule a new rAF', () => {
    const entry = acquireSessionCache('sess-A', true);
    entry.traceMetadata = { systems: [] } as unknown as TraceMetadata;
    entry.cache.entriesVersion = 1;

    _scheduleBumpForTest(entry);
    flushRaf();
    expect(_pendingBumpRafIdForTest(entry)).toBeNull();

    entry.cache.entriesVersion = 2; // New chunk landed → guard lets the bump through.
    _scheduleBumpForTest(entry);
    expect(rafQueue.length).toBe(1);
  });

  it('cancels the pending rAF on release/destroy', () => {
    const entry = acquireSessionCache('sess-A', true);
    entry.traceMetadata = { systems: [] } as unknown as TraceMetadata;
    entry.cache.entriesVersion = 1;
    const listener = vi.fn();
    subscribeSessionCache(entry, listener);

    _scheduleBumpForTest(entry);
    expect(rafQueue.length).toBe(1);
    releaseSessionCache('sess-A'); // destroys the entry
    expect(rafQueue.length).toBe(0); // rAF was cancelled.

    flushRaf();
    expect(listener).not.toHaveBeenCalled();
  });
});

// ── #4b bumpEntriesVersionImmediate cache.entriesVersion guard ──────────────────────────────────
describe('profilerCacheRegistry — #4b bumpEntriesVersionImmediate cache-version guard', () => {
  it('first bump always proceeds (lastBumpedCacheVersion starts at -1 sentinel)', () => {
    const entry = acquireSessionCache('sess-A', true);
    entry.traceMetadata = { systems: [] } as unknown as TraceMetadata;
    const listener = vi.fn();
    subscribeSessionCache(entry, listener);

    expect(entry.cache.entriesVersion).toBe(0);
    _bumpImmediateForTest(entry); // First bump — sentinel guard lets it through.
    expect(listener).toHaveBeenCalledTimes(1);
    expect(entry.assembled).not.toBeNull();
  });

  it('skips when cache.entriesVersion is unchanged since the previous bump (no spurious notifications)', () => {
    const entry = acquireSessionCache('sess-A', true);
    entry.traceMetadata = { systems: [] } as unknown as TraceMetadata;
    entry.cache.entriesVersion = 1;
    _bumpImmediateForTest(entry); // Seeds lastBumpedCacheVersion = 1 + entry.assembled non-null.

    const listener = vi.fn();
    subscribeSessionCache(entry, listener);
    _bumpImmediateForTest(entry); // cache.entriesVersion still 1 → guard short-circuits.
    expect(listener).not.toHaveBeenCalled();
  });

  it('proceeds when cache.entriesVersion advances (new chunk landed)', () => {
    const entry = acquireSessionCache('sess-A', true);
    entry.traceMetadata = { systems: [] } as unknown as TraceMetadata;
    entry.cache.entriesVersion = 1;
    _bumpImmediateForTest(entry); // Seed.

    const listener = vi.fn();
    subscribeSessionCache(entry, listener);

    entry.cache.entriesVersion = 2; // Simulate chunk-arrival mutation.
    _bumpImmediateForTest(entry);
    expect(listener).toHaveBeenCalledTimes(1);
  });

  it('resets to first-bump behaviour after a fingerprint change clears entry.assembled', () => {
    const entry = acquireSessionCache('sess-A', true);
    entry.traceMetadata = { systems: [] } as unknown as TraceMetadata;
    entry.cache.entriesVersion = 1;
    _bumpImmediateForTest(entry);
    expect(entry.assembled).not.toBeNull();

    // Simulate the fingerprint-reset block in the session-store subscriber: assembled cleared,
    // sentinel restored. The next bump must proceed even if cache.entriesVersion happens to
    // collide with the previous fingerprint's value.
    entry.assembled = null;
    entry.lastBumpedCacheVersion = -1;
    const listener = vi.fn();
    subscribeSessionCache(entry, listener);
    _bumpImmediateForTest(entry);
    expect(listener).toHaveBeenCalledTimes(1);
  });
});
