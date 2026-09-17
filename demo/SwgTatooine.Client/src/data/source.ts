/**
 * Where the client's world comes from. The renderer and the UI only ever see the SDK's store, clock and aggregate grid;
 * a source fills them. Today that is the mock server; at M1 it is a Typhon Subscriptions v2 connection.
 */

export interface SourceStats {
  readonly name: string;
  readonly running: boolean;
  readonly ticksApplied: number;
  readonly lastTick: number;
  readonly viewComplete: boolean;
  /** Estimated wire bytes per second over the last second. */
  readonly wireBytesPerSec: number;
  /** Main-thread time spent applying the last frame into the store. */
  readonly applyMs: number;
  /** Server-side figures when the source can report them (the mock can; a real server sends `STATS`). */
  readonly server: {
    readonly simMs: number;
    readonly replicationMs: number;
    readonly watched: number;
    readonly effectiveRadius: number;
    readonly worldEntities: number;
  } | null;
}

/** Receives events as frames are applied, after enters and updates and before leaves (`03-wire-protocol.md` § 5). */
export interface EventSink {
  /** A hit. A netId of 0 is an end the client does not know; `hpAfter` is the target's health fraction after the hit. */
  onAttack(tick: number, attackerNetId: number, targetNetId: number, damage: number, hpAfter: number): void;
}

/** A simulated network link: the mock has one, a real connection does not. */
export interface LatencyControl {
  setLatency(latencyMs: number, jitterMs: number): void;
}

export interface DataSource {
  start(): void;
  dispose(): void;
  /** The god camera's region of interest: a ground point and a radius (the built-in `ClientRegion` command). */
  setRegion(x: number, z: number, radius: number): void;
  setPaused(paused: boolean): void;
  readonly stats: SourceStats;
  /** The simulated link's control, or null for a real connection. */
  readonly latency: LatencyControl | null;
}
