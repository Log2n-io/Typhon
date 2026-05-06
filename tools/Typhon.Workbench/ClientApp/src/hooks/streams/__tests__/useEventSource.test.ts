// @vitest-environment jsdom
import { renderHook, act } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useEventSource } from '../useEventSource';

/**
 * Minimal EventSource stub that captures registered listeners and exposes a `dispatch` method to
 * simulate server-sent events. Mirrors only the surface useEventSource depends on
 * (addEventListener, onmessage, onopen, onerror, close, readyState).
 */
class StubEventSource {
  static OPEN = 1;
  static CLOSED = 2;
  static lastInstance: StubEventSource | null = null;

  url: string;
  readyState = 0;
  onopen: ((this: EventSource) => unknown) | null = null;
  onmessage: ((this: EventSource, ev: MessageEvent) => unknown) | null = null;
  onerror: ((this: EventSource, ev: Event) => unknown) | null = null;
  private listeners: Map<string, Set<(ev: MessageEvent) => void>> = new Map();

  constructor(url: string) {
    this.url = url;
    StubEventSource.lastInstance = this;
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

  /** Fires the named event with a JSON-stringified payload. */
  dispatch(type: string, data: unknown): void {
    const evt = new MessageEvent(type, { data: JSON.stringify(data) });
    this.listeners.get(type)?.forEach((fn) => fn(evt));
    if (type === 'message' && this.onmessage) this.onmessage.call(this as unknown as EventSource, evt);
  }

  open(): void {
    this.readyState = StubEventSource.OPEN;
    this.onopen?.call(this as unknown as EventSource);
  }

  close(): void {
    this.readyState = StubEventSource.CLOSED;
  }
}

describe('useEventSource — typed-listener overload (#308)', () => {
  beforeEach(() => {
    StubEventSource.lastInstance = null;
    (globalThis as unknown as { EventSource: typeof StubEventSource }).EventSource = StubEventSource;
  });
  afterEach(() => {
    StubEventSource.lastInstance?.close();
    StubEventSource.lastInstance = null;
  });

  it('installs one addEventListener per typed handler key', () => {
    const tickHandler = vi.fn();
    const heartbeatHandler = vi.fn();
    renderHook(() =>
      useEventSource('/api/test/stream', {
        tick: tickHandler,
        heartbeat: heartbeatHandler,
      }),
    );

    const es = StubEventSource.lastInstance!;
    expect(es).not.toBeNull();

    act(() => {
      es.dispatch('tick', { tickNumber: 42 });
    });
    expect(tickHandler).toHaveBeenCalledWith({ tickNumber: 42 });
    expect(heartbeatHandler).not.toHaveBeenCalled();

    act(() => {
      es.dispatch('heartbeat', { status: 'green' });
    });
    expect(heartbeatHandler).toHaveBeenCalledWith({ status: 'green' });
    expect(tickHandler).toHaveBeenCalledTimes(1);
  });

  it('untyped handler still receives default message events', () => {
    const onMessage = vi.fn();
    renderHook(() => useEventSource<{ value: number }>('/api/legacy/stream', onMessage));

    const es = StubEventSource.lastInstance!;
    act(() => {
      es.dispatch('message', { value: 7 });
    });
    expect(onMessage).toHaveBeenCalledWith({ value: 7 });
  });

  it('null url renders the hook closed without opening a connection', () => {
    renderHook(() => useEventSource(null, { tick: vi.fn() }));
    expect(StubEventSource.lastInstance).toBeNull();
  });

  it('handler updates between renders are picked up via ref', () => {
    const firstCall = vi.fn();
    const { rerender } = renderHook(
      ({ handler }: { handler: (data: unknown) => void }) =>
        useEventSource('/api/test/stream', { tick: handler }),
      { initialProps: { handler: firstCall } },
    );

    const es = StubEventSource.lastInstance!;
    const secondCall = vi.fn();
    rerender({ handler: secondCall });

    act(() => {
      es.dispatch('tick', { tickNumber: 99 });
    });
    expect(firstCall).not.toHaveBeenCalled();
    expect(secondCall).toHaveBeenCalledWith({ tickNumber: 99 });
  });
});
