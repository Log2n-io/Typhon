// @vitest-environment jsdom
import { renderHook, act, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useDataStream } from '../useDataStream';

class StubEventSource {
  static OPEN = 1;
  static CLOSED = 2;
  /** Tracks all currently-live instances so a test can address them by URL. */
  static instances: StubEventSource[] = [];

  url: string;
  readyState = 0;
  onopen: ((this: EventSource) => unknown) | null = null;
  onmessage: ((this: EventSource, ev: MessageEvent) => unknown) | null = null;
  onerror: ((this: EventSource, ev: Event) => unknown) | null = null;
  closed = false;
  private listeners: Map<string, Set<(ev: MessageEvent) => void>> = new Map();

  constructor(url: string) {
    this.url = url;
    StubEventSource.instances.push(this);
  }

  addEventListener(type: string, fn: (ev: MessageEvent) => void): void {
    let set = this.listeners.get(type);
    if (!set) {
      set = new Set();
      this.listeners.set(type, set);
    }
    set.add(fn);
  }

  removeEventListener(type: string, fn: (ev: MessageEvent) => void): void {
    this.listeners.get(type)?.delete(fn);
  }

  dispatch(type: string, data: unknown): void {
    const evt = new MessageEvent(type, { data: JSON.stringify(data) });
    this.listeners.get(type)?.forEach((fn) => fn(evt));
  }

  close(): void {
    this.closed = true;
    this.readyState = StubEventSource.CLOSED;
  }
}

interface FetchCall {
  url: string;
  method: string;
  body: { streamId: string; events: string[] };
}

function setupFetchSpy(): { calls: FetchCall[]; restore: () => void } {
  const calls: FetchCall[] = [];
  const original = globalThis.fetch;
  globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({
      url: typeof input === 'string' ? input : input.toString(),
      method: init?.method ?? 'GET',
      body: init?.body ? JSON.parse(init.body as string) : { streamId: '', events: [] },
    });
    return new Response(null, { status: 204 });
  }) as unknown as typeof fetch;
  return {
    calls,
    restore: () => {
      globalThis.fetch = original;
    },
  };
}

describe('useDataStream (#308 Phase C)', () => {
  let fetchSpy: { calls: FetchCall[]; restore: () => void };

  beforeEach(() => {
    StubEventSource.instances = [];
    (globalThis as unknown as { EventSource: typeof StubEventSource }).EventSource = StubEventSource;
    fetchSpy = setupFetchSpy();
  });

  afterEach(() => {
    fetchSpy.restore();
  });

  it('opens one EventSource per session and fans out typed events to mounted handlers', async () => {
    const tickHandler = vi.fn();
    renderHook(() => useDataStream('session-A', { tick: tickHandler }));

    expect(StubEventSource.instances).toHaveLength(1);
    const es = StubEventSource.instances[0];
    expect(es.url).toBe('/api/sessions/session-A/stream');

    act(() => {
      es.dispatch('stream-id', { streamId: '00000000-0000-0000-0000-000000000001' });
    });

    // After streamId arrives, /subscribe should be POSTed for `tick`.
    await waitFor(() => {
      expect(fetchSpy.calls.some((c) => c.url.endsWith('/subscribe'))).toBe(true);
    });

    const subscribeCall = fetchSpy.calls.find((c) => c.url.endsWith('/subscribe'))!;
    expect(subscribeCall.body.streamId).toBe('00000000-0000-0000-0000-000000000001');
    expect(subscribeCall.body.events).toEqual(['tick']);

    act(() => {
      es.dispatch('tick', { tickSummary: { tickNumber: 1 } });
    });
    expect(tickHandler).toHaveBeenCalledWith({ tickSummary: { tickNumber: 1 } });
  });

  it('shares the connection across two consumers and unions their subscriptions', async () => {
    const tickHandler = vi.fn();
    const logHandler = vi.fn();

    const consumerA = renderHook(() => useDataStream('session-X', { tick: tickHandler }));
    renderHook(() => useDataStream('session-X', { log: logHandler }));

    // Only one underlying EventSource — both hooks share it.
    expect(StubEventSource.instances).toHaveLength(1);
    const es = StubEventSource.instances[0];

    act(() => {
      es.dispatch('stream-id', { streamId: 'shared-stream' });
    });

    await waitFor(() => {
      const subscribed = new Set<string>();
      for (const c of fetchSpy.calls.filter((c) => c.url.endsWith('/subscribe'))) {
        for (const e of c.body.events) subscribed.add(e);
      }
      expect(subscribed.has('tick')).toBe(true);
      expect(subscribed.has('log')).toBe(true);
    });

    act(() => {
      es.dispatch('log', { message: 'hi' });
    });
    expect(logHandler).toHaveBeenCalledWith({ message: 'hi' });
    expect(tickHandler).not.toHaveBeenCalled();

    // Unmount consumer A — log handler should still receive events; tick should be unsubscribed
    // since no one wants it any more.
    consumerA.unmount();

    await waitFor(() => {
      const unsubs = fetchSpy.calls.filter((c) => c.url.endsWith('/unsubscribe'));
      expect(unsubs.some((c) => c.body.events.includes('tick'))).toBe(true);
    });

    // Connection still open because consumer B is still mounted.
    expect(es.closed).toBe(false);
  });

  it('closes the connection when the last consumer unmounts', async () => {
    const { unmount } = renderHook(() => useDataStream('session-Y', { tick: vi.fn() }));
    const es = StubEventSource.instances[0];

    act(() => {
      es.dispatch('stream-id', { streamId: 'lone-stream' });
    });

    unmount();

    await waitFor(() => {
      expect(es.closed).toBe(true);
    });
  });

  it('null sessionId opens no connection and surfaces idle state', () => {
    const { result } = renderHook(() => useDataStream(null, { tick: vi.fn() }));
    expect(StubEventSource.instances).toHaveLength(0);
    expect(result.current.state).toBe('idle');
  });
});
