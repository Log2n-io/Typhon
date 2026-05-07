import { useEffect, useRef, useState } from 'react';

type ConnectionState = 'connecting' | 'open' | 'closed';

const RECONNECT_DELAY_MS = 3000;

/**
 * Map of SSE event type -> listener. The listener receives the parsed JSON payload for events of
 * that type. Use this overload when the server emits typed `event: <type>` frames (#308).
 *
 * Each handler can declare its own payload type — the hook does not narrow them at compile time
 * because EventSource's `addEventListener` is itself untyped. Cast inside the handler if needed.
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type EventListenerMap = Record<string, (data: any) => void>;

/**
 * Subscribe to a Server-Sent Events stream.
 *
 * Two call shapes:
 *
 * 1. **Untyped (legacy)** — passes a single `onMessage` callback that receives every default
 *    `message` event. Used by streams that emit `data: ...\n\n` frames without an `event:` prefix.
 *
 *    ```ts
 *    useEventSource<HeartbeatPayload>(url, (data) => { ... });
 *    ```
 *
 * 2. **Typed (#308)** — passes a `Record<eventType, handler>`. The hook installs one
 *    `addEventListener` per key. Use for streams that emit `event: <type>\ndata: ...\n\n`. The
 *    server's typed events carry the discriminator on the `event:` line, not in the JSON payload —
 *    consumers narrow by handler key, not by switching on a `kind` field.
 *
 *    ```ts
 *    useEventSource(url, {
 *      heartbeat: (data: HeartbeatPayload) => { ... },
 *      shutdown: () => { ... },
 *    });
 *    ```
 *
 * Both shapes share the same auto-reconnect behaviour (3 s delay) and `null`-url gating.
 */
export function useEventSource<T>(
  url: string | null,
  onMessage: (data: T) => void,
): ConnectionState;
export function useEventSource(
  url: string | null,
  listeners: EventListenerMap,
): ConnectionState;
export function useEventSource<T>(
  url: string | null,
  arg: ((data: T) => void) | EventListenerMap,
): ConnectionState {
  const [state, setState] = useState<ConnectionState>('closed');
  const argRef = useRef(arg);
  argRef.current = arg;

  useEffect(() => {
    if (!url) {
      setState('closed');
      return;
    }

    let es: EventSource;
    let reconnectTimer: ReturnType<typeof setTimeout>;
    let cancelled = false;

    const connect = () => {
      setState('connecting');
      es = new EventSource(url);

      es.onopen = () => {
        if (!cancelled) setState('open');
      };

      const current = argRef.current;
      if (typeof current === 'function') {
        es.onmessage = (event) => {
          if (cancelled) return;
          try {
            (argRef.current as (data: T) => void)(JSON.parse(event.data) as T);
          } catch {
            // ignore parse errors
          }
        };
      } else {
        // Typed-listener path — install one addEventListener per key. We resolve the handler from
        // argRef.current at dispatch time so consumers swapping handlers between renders still get
        // the latest closure (matches the legacy onMessage path's ref-pinning behaviour).
        for (const eventType of Object.keys(current)) {
          es.addEventListener(eventType, (event: MessageEvent) => {
            if (cancelled) return;
            const map = argRef.current as EventListenerMap;
            const handler = map[eventType];
            if (!handler) return;
            try {
              handler(JSON.parse(event.data));
            } catch {
              // ignore parse errors
            }
          });
        }
      }

      es.onerror = () => {
        es.close();
        if (!cancelled) {
          setState('closed');
          reconnectTimer = setTimeout(connect, RECONNECT_DELAY_MS);
        }
      };
    };

    connect();

    return () => {
      cancelled = true;
      clearTimeout(reconnectTimer);
      es?.close();
      setState('closed');
    };
    // The `arg` value is intentionally not in the dep array — handlers are pinned via argRef so
    // re-renders that recreate the handler reference don't tear down the SSE connection. The
    // initial argument shape (function vs map) is captured on connect and stays consistent for the
    // lifetime of the URL.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [url]);

  return state;
}
