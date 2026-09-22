/**
 * The little of a `WebSocket` and of the timer API the connection uses, declared here rather than taken from the DOM
 * typings: this package compiles against `ES2022` alone, so nothing platform-specific may leak into it (05 § 2). A test
 * hands in its own socket and timers; a browser or Node passes the real ones.
 */

/** `WebSocket.readyState` values (RFC 6455 / the WHATWG interface). */
export const SocketState = {
  Connecting: 0,
  Open: 1,
  Closing: 2,
  Closed: 3,
} as const;

export interface SocketCloseEvent {
  readonly code: number;
  readonly reason: string;
  readonly wasClean: boolean;
}

export interface SocketMessageEvent {
  /** An `ArrayBuffer` while `binaryType` is `'arraybuffer'`; anything else is a protocol error. */
  readonly data: unknown;
}

export interface WebSocketLike {
  binaryType: string;
  /** The subprotocol the server selected, `''` when it selected none. */
  readonly protocol: string;
  readonly readyState: number;
  onopen: (() => void) | null;
  onmessage: ((event: SocketMessageEvent) => void) | null;
  onclose: ((event: SocketCloseEvent) => void) | null;
  onerror: ((event: unknown) => void) | null;
  send(data: Uint8Array): void;
  close(code?: number, reason?: string): void;
}

/** Opens a socket for `url`, offering `protocols`. */
export type WebSocketFactory = (url: string, protocols: readonly string[]) => WebSocketLike;

export type TimerHandle = unknown;

/** The timers the connection, the ping scheduler and the reconnect loop run on. */
export interface TimerApi {
  setTimeout(handler: () => void, ms: number): TimerHandle;
  clearTimeout(handle: TimerHandle): void;
}

interface SocketGlobals {
  WebSocket?: new (url: string, protocols?: string | readonly string[]) => WebSocketLike;
  setTimeout: (handler: () => void, ms: number) => TimerHandle;
  clearTimeout: (handle: TimerHandle) => void;
}

const globals = globalThis as unknown as SocketGlobals;

/** The platform's timers. */
export const systemTimers: TimerApi = {
  setTimeout: (handler, ms) => globals.setTimeout(handler, ms),
  clearTimeout: (handle) => {
    globals.clearTimeout(handle);
  },
};

/** The platform's `WebSocket`; throws where there is none (Node before 22, or a test that forgot its fake). */
export const systemWebSocket: WebSocketFactory = (url, protocols) => {
  const ctor = globals.WebSocket;
  if (ctor === undefined) {
    throw new Error('this runtime has no WebSocket: pass ConnectionOptions.socket');
  }

  return new ctor(url, protocols);
};
