/**
 * Messages between the main thread and the mock server worker.
 *
 * This is NOT the `typhon.3` wire: it is the mock's in-process shortcut, carrying the same content a decoded `TICK` has
 * (`claude/design/Subscriptions/V2/03-wire-protocol.md` § 3–9) as flat typed arrays, so the apply step on the main thread
 * does the same store writes a real decoder will — though not at the same cost: these are doubles, not varints, so the
 * apply time here is no evidence for M1. When the live server arrives, this file and the worker go; the store and
 * everything downstream stay.
 */

export const TickFlags = { ViewComplete: 1, Reset: 2 } as const;

/** Record strides. Every value is a double; netIds and ticks are exact integers. */
export const SEGMENT_RECORD = 7; // netId, p0x, p0z, vx, vz, t0, epoch
export const ENTER_HEADER = 7; // netId, p0x, p0z, vx, vz, t0, epoch, then one value per schema field
export const STATE_HEADER = 2; // netId, groupMask, then one value per schema field (only the masked groups apply)
export const EVENT_RECORD = 5; // kind, attacker netId (0 = unknown), target netId (0 = unknown), damage, target health after

export interface ArchetypeBlock {
  readonly archetype: number;
  readonly enters: Float64Array;
  readonly enterCount: number;
  readonly segments: Float64Array;
  readonly segmentCount: number;
  readonly states: Float64Array;
  readonly stateCount: number;
  readonly leaves: Float64Array;
  readonly leaveCount: number;
}

export interface AggBlock {
  readonly reset: boolean;
  readonly cellCount: number;
  /** Cell index per changed cell. */
  readonly cells: Uint32Array;
  /** `cellCount × archetypes` counts, in the grid's archetype order. */
  readonly counts: Uint32Array;
}

export interface ServerStats {
  /** Worker time spent simulating this tick. */
  readonly simMs: number;
  /** Worker time spent on interest, projection and frame assembly. */
  readonly replicationMs: number;
  readonly watched: number;
  readonly effectiveRadius: number;
  /** Estimated `typhon.3` bytes of this frame on the wire, transport overhead included. */
  readonly wireBytes: number;
  readonly worldEntities: number;
}

export interface TickMessage {
  readonly type: 'tick';
  readonly tick: number;
  readonly flags: number;
  readonly blocks: readonly ArchetypeBlock[];
  readonly events: Float64Array;
  readonly eventCount: number;
  readonly agg: AggBlock | null;
  readonly stats: ServerStats;
}

export interface ReadyMessage {
  readonly type: 'ready';
  readonly population: number;
  readonly worldEntities: number;
  readonly buildMs: number;
}

export type WorkerToMain = TickMessage | ReadyMessage;

export type MainToWorker =
  | { readonly type: 'start'; readonly seed: number; readonly population: number }
  | { readonly type: 'region'; readonly x: number; readonly z: number; readonly radius: number }
  | { readonly type: 'pause'; readonly paused: boolean }
  | { readonly type: 'stop' };

/** A frame carrying nothing: the real server sends only a header every 500 ms for those (`03` § 3). */
export function isEmptyTick(message: TickMessage): boolean {
  return (
    message.blocks.length === 0 &&
    message.eventCount === 0 &&
    message.agg === null &&
    (message.flags & TickFlags.Reset) === 0
  );
}
