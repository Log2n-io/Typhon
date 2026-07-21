import { useEffect, useMemo, useRef, useState } from 'react';
import { applyWorkbenchAuthHeaders } from '@/api/bootstrapToken';

/**
 * Per-session shared state for the unified data stream (#308 Phase B/C). One open EventSource per
 * sessionId, multiplexed across N panel-level consumers. The wrapper layered around it
 * (`useDataStream`) reference-counts subscriptions so a consumer mounting / unmounting on a
 * subset of events only affects the union of what the server should send — peers stay
 * unaffected.
 *
 * Module-level singletons (one entry per sessionId) keep the connection alive across React
 * re-mounts of individual panels. The connection only closes when the last consumer unmounts.
 */
interface SharedConnection {
  sessionId: string;
  source: EventSource;
  /** streamId emitted by the server on first frame; targeted by /subscribe & /unsubscribe. */
  streamId: string | null;
  /** Per-event-type subscriber lists (handler array). Multiple consumers can listen to the same type. */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  handlers: Map<string, Set<(data: any) => void>>;
  /** Per-event-type ref count — total panels currently asking for this type. */
  refCounts: Map<string, number>;
  /** Set last sent to /subscribe. Used to compute add/remove deltas vs the live ref-count map. */
  serverWantedSet: Set<string>;
  /** Pending sync pass — one in-flight at a time. */
  syncing: boolean;
  /** Number of consumers currently mounted on this connection — when it hits zero we close. */
  mountCount: number;
  /** Pending bootstrap-event handlers fired before streamId was known — replayed after register. */
  pendingBootstrapHandlers: ((id: string) => void)[];
}

const _connections = new Map<string, SharedConnection>();

function getOrCreateConnection(sessionId: string): SharedConnection {
  let conn = _connections.get(sessionId);
  if (conn) return conn;

  // EventSource is browser-only — bail safely in test envs (the surrounding hook will simply not
  // dispatch any events; the test passes a `null` sessionId or stubs EventSource itself).
  if (typeof EventSource === 'undefined') {
    throw new Error('EventSource is not available in this environment');
  }

  const source = new EventSource(`/api/sessions/${sessionId}/stream`);
  conn = {
    sessionId,
    source,
    streamId: null,
    handlers: new Map(),
    refCounts: new Map(),
    serverWantedSet: new Set(),
    syncing: false,
    mountCount: 0,
    pendingBootstrapHandlers: [],
  };

  // Capture streamId on the first `stream-id` event — every subsequent /subscribe call needs it.
  source.addEventListener('stream-id', (event: MessageEvent) => {
    try {
      const payload = JSON.parse(event.data) as { streamId: string };
      conn!.streamId = payload.streamId;
      // Replay any handlers queued before streamId arrived. Cleared once flushed.
      for (const cb of conn!.pendingBootstrapHandlers) {
        try {
          cb(payload.streamId);
        } catch {
          /* swallow */
        }
      }
      conn!.pendingBootstrapHandlers = [];
      // Also sync any subscriptions that landed before we had a streamId.
      void syncSubscriptions(conn!);
    } catch {
      /* malformed — ignore */
    }
  });

  _connections.set(sessionId, conn);
  return conn;
}

function teardownConnection(sessionId: string): void {
  const conn = _connections.get(sessionId);
  if (!conn) return;
  conn.source.close();
  _connections.delete(sessionId);
}

/**
 * Reconciles the server's known subscription set with the live ref-counted set. Adds events that
 * have ref-count > 0 and aren't on the server; removes events that have ref-count == 0 but are
 * still on the server. POST batched in one call each (no per-event fetches).
 */
async function syncSubscriptions(conn: SharedConnection): Promise<void> {
  if (!conn.streamId) return;
  if (conn.syncing) return;
  conn.syncing = true;
  try {
    // Converge to a fixed point — refCounts can mutate during awaits as panels mount/unmount.
    let converged = false;
    while (!converged) {
      const wanted = new Set<string>();
      for (const [type, count] of conn.refCounts) {
        if (count > 0) wanted.add(type);
      }
      const toAdd: string[] = [];
      const toRemove: string[] = [];
      for (const t of wanted) {
        if (!conn.serverWantedSet.has(t)) toAdd.push(t);
      }
      for (const t of conn.serverWantedSet) {
        if (!wanted.has(t)) toRemove.push(t);
      }
      if (toAdd.length === 0 && toRemove.length === 0) {
        converged = true;
        continue;
      }
      if (toAdd.length > 0) {
        const ok = await postSubscription(conn.sessionId, '/subscribe', conn.streamId, toAdd);
        if (ok) {
          for (const t of toAdd) conn.serverWantedSet.add(t);
        }
      }
      if (toRemove.length > 0) {
        const ok = await postSubscription(conn.sessionId, '/unsubscribe', conn.streamId, toRemove);
        if (ok) {
          for (const t of toRemove) conn.serverWantedSet.delete(t);
        }
      }
    }
  } finally {
    conn.syncing = false;
  }
}

async function postSubscription(
  sessionId: string,
  path: '/subscribe' | '/unsubscribe',
  streamId: string,
  events: string[],
): Promise<boolean> {
  try {
    const resp = await fetch(`/api/sessions/${sessionId}${path}`, {
      method: 'POST',
      headers: applyWorkbenchAuthHeaders(new Headers({ 'Content-Type': 'application/json' })),
      body: JSON.stringify({ streamId, events }),
    });
    return resp.ok;
  } catch {
    return false;
  }
}

/**
 * Subscribe to events on the unified data stream. Each consumer declares the set of event types it
 * cares about and a handler map; the hook ensures (a) one EventSource per session, shared across
 * consumers, and (b) the server-side subscription set is the union of what mounted consumers want.
 *
 * ```ts
 * useDataStream(sessionId, {
 *   tick: (data) => store.applyTick(data),
 *   'topology-changed': () => queryClient.invalidateQueries(['topology']),
 * });
 * ```
 *
 * Bootstrap events (`stream-id`, `metadata`, `session-state`, `heartbeat`, `shutdown`) are
 * delivered to handlers without needing explicit subscription — those are always sent by the
 * server. Domain events (`tick`, `log`, `topology-changed`, `error`) require a subscribed handler.
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type EventHandlers = Record<string, (data: any) => void>;

export type DataStreamConnectionState = 'idle' | 'connecting' | 'open' | 'closed';

export function useDataStream(
  sessionId: string | null,
  handlers: EventHandlers,
): { state: DataStreamConnectionState; streamId: string | null } {
  const [state, setState] = useState<DataStreamConnectionState>(sessionId ? 'connecting' : 'idle');
  const [streamId, setStreamId] = useState<string | null>(null);

  // Stabilise the handler map across renders without forcing consumers to memoize. Each render's
  // latest handler is captured in a ref; the actual addEventListener closures resolve from the
  // ref at dispatch time so re-renders don't tear down the SSE connection.
  const handlersRef = useRef(handlers);
  handlersRef.current = handlers;

  // Stable list of event types this consumer wants. Memoised over the *keys* of the handler map —
  // adding / removing event types between renders is rare, but supported.
  const wantedTypes = useMemo(() => Object.keys(handlers).sort().join(','), [handlers]);

  useEffect(() => {
    if (!sessionId) {
      setState('idle');
      setStreamId(null);
      return;
    }

    let conn: SharedConnection;
    try {
      conn = getOrCreateConnection(sessionId);
    } catch {
      // EventSource unavailable (e.g., SSR) — render the hook idle.
      setState('idle');
      return;
    }
    setState(conn.source.readyState === EventSource.OPEN ? 'open' : 'connecting');
    if (conn.streamId) setStreamId(conn.streamId);

    const types = wantedTypes.split(',').filter((s) => s.length > 0);

    // ── Per-event-type listener installation ──────────────────────────────────────────────────
    // For each event type the consumer cares about, add to the shared handler set and bump the
    // ref count. The dispatcher (a single per-type listener on the underlying EventSource)
    // forwards into all currently-registered consumer handlers.
    const installedTypes: string[] = [];
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const registeredHandlers = new Map<string, (data: any) => void>();
    for (const type of types) {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const userHandler = (data: any) => {
        const live = handlersRef.current[type];
        if (live) live(data);
      };
      registeredHandlers.set(type, userHandler);

      let set = conn.handlers.get(type);
      if (!set) {
        set = new Set();
        conn.handlers.set(type, set);
        // First mount of this event type — install the EventSource listener that fans out to
        // all consumers. There's only ever one per type per connection.
        conn.source.addEventListener(type, (event: MessageEvent) => {
          try {
            const data = JSON.parse(event.data);
            const subs = conn.handlers.get(type);
            if (subs) {
              for (const fn of subs) {
                try {
                  fn(data);
                } catch {
                  /* swallow consumer-side errors */
                }
              }
            }
          } catch {
            /* malformed payload — ignore */
          }
        });
      }
      set.add(userHandler);
      conn.refCounts.set(type, (conn.refCounts.get(type) ?? 0) + 1);
      installedTypes.push(type);
    }

    conn.mountCount += 1;

    const onOpen = () => setState('open');
    const onError = () => setState('closed');
    conn.source.addEventListener('open', onOpen);
    conn.source.addEventListener('error', onError);

    // Hand off the streamId once it lands (or immediately if already known).
    const onStreamId = (id: string) => setStreamId(id);
    if (conn.streamId) {
      onStreamId(conn.streamId);
    } else {
      conn.pendingBootstrapHandlers.push(onStreamId);
    }

    void syncSubscriptions(conn);

    return () => {
      conn.source.removeEventListener('open', onOpen);
      conn.source.removeEventListener('error', onError);

      for (const type of installedTypes) {
        const set = conn.handlers.get(type);
        const userHandler = registeredHandlers.get(type);
        if (set && userHandler) {
          set.delete(userHandler);
        }
        const next = (conn.refCounts.get(type) ?? 1) - 1;
        if (next <= 0) {
          conn.refCounts.delete(type);
        } else {
          conn.refCounts.set(type, next);
        }
      }

      conn.mountCount -= 1;
      if (conn.mountCount <= 0) {
        teardownConnection(sessionId);
      } else {
        void syncSubscriptions(conn);
      }
    };
  }, [sessionId, wantedTypes]);

  return { state, streamId };
}
