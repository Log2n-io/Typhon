import {
  archetypeOf,
  Capabilities,
  CommandQueue,
  FrameApplier,
  NOT_FOUND,
  PingScheduler,
  ReconnectingClient,
  RegionSender,
  slotOf,
  StreamRecorder,
  TickFlags,
  type AggregateGrid,
  type Clock,
  type Connection,
  type EventRecord,
  type MessagePlan,
  type SessionInfo,
  type WorldStore,
} from '@typhondb/client';

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

export interface TyphonSourceOptions {
  /** The server's WebSocket URL, which must speak `typhon.2`. */
  readonly url: string;
  /** The clock render time comes from; the source feeds it every frame. */
  readonly clock: Clock;
  readonly events: EventSink;
  /** The application-declared session kind handed to the server's admission hook. Default `god`. */
  readonly kind?: string;
  readonly token?: string;
  /** The client's byte budget in the `ClientRegion` command. Default 256 KiB/s. */
  readonly budgetKiBps?: number;
  /** The viewpoint altitude in the region; default the camera's radius. */
  readonly altitudeM?: number;
  /** Record every inbound message, for an offline replay. */
  readonly record?: boolean;
}

/**
 * A live Typhon Subscriptions v2 connection as a {@link DataSource}: the SDK's `ReconnectingClient` drives the session,
 * its `FrameApplier` fills a store built from the server's own catalog, and the god camera's disc becomes the built-in
 * `ClientRegion` polygon.
 *
 * It owns its world and grid because only the catalog can size them: `WELCOME` arrives after the app is up, and a
 * reconnect to a server whose catalog moved rebuilds them. The renderer adopts {@link world} and {@link grid} once a
 * session is open — {@link stats}`.running` says when.
 */
export class TyphonSource implements DataSource {
  private readonly options: TyphonSourceOptions;
  private readonly client: ReconnectingClient;
  private readonly recorder: StreamRecorder | null;

  private applier: FrameApplier | null = null;
  private ping: PingScheduler | null = null;
  private commands: CommandQueue | null = null;
  private region: RegionSender | null = null;
  private attack: MessagePlan | null = null;
  private pendingRegion: { x: number; z: number; radius: number } | null = null;
  private paused = false;
  private ticks = 0;
  private lastTick = 0;
  private viewComplete = false;
  private applyMs = 0;
  /** Arrival times and byte counts of the last second's messages. */
  private readonly byteTimes: number[] = [];
  private readonly byteCounts: number[] = [];

  constructor(options: TyphonSourceOptions) {
    this.options = options;
    this.recorder = options.record === true ? new StreamRecorder() : null;
    this.client = new ReconnectingClient({
      url: options.url,
      kind: options.kind ?? 'god',
      token: options.token ?? '',
      caps: Capabilities.Stats,
      handlers: {
        onWelcome: (session, connection) => {
          this.onWelcome(session, connection);
        },
        onTick: (message, recvMs) => {
          this.onTick(message, recvMs);
        },
        onPong: (pong, recvMs) => {
          this.ping?.onPong(pong, recvMs);
        },
        onMessage: (message, recvMs) => {
          this.recorder?.record(message, recvMs);
        },
        onClose: () => {
          this.onClose();
        },
      },
    });
  }

  /** The store the session fills, or `null` before the first `WELCOME`. */
  get world(): WorldStore | null {
    return this.applier?.world ?? null;
  }

  /** The far tier's per-cell counts, or `null`. */
  get grid(): AggregateGrid | null {
    return this.applier?.grids[0] ?? null;
  }

  get clock(): Clock {
    return this.options.clock;
  }

  /** The session's recording, when `record` was asked for: replay it with the SDK's `replayStream`. */
  get recording(): StreamRecorder | null {
    return this.recorder;
  }

  /** The round trip the `PING` loop measures, in milliseconds. */
  get rttMs(): number {
    return this.ping?.rttMs ?? 0;
  }

  get stats(): SourceStats {
    return {
      name: `typhon ${this.options.url}`,
      running: this.applier !== null,
      ticksApplied: this.ticks,
      lastTick: this.lastTick,
      viewComplete: this.viewComplete,
      wireBytesPerSec: this.wireBytesPerSec(),
      applyMs: this.applyMs,
      server: this.serverStats(),
    };
  }

  /** A real link has no simulated latency to control. */
  get latency(): LatencyControl | null {
    return null;
  }

  start(): void {
    this.client.start();
  }

  dispose(): void {
    this.ping?.stop();
    this.client.stop();
  }

  /** The god camera's disc, sent as the smallest quad that contains it (W28: a quad is the minimum footprint). */
  setRegion(x: number, z: number, radius: number): void {
    this.pendingRegion = { x, z, radius };
    this.sendRegion();
  }

  /** A live server does not pause; this only stops the client asking for a new region. */
  setPaused(paused: boolean): void {
    this.paused = paused;
  }

  private onWelcome(session: SessionInfo, connection: Connection): void {
    const plan = session.plan;
    this.applier = new FrameApplier(plan, {
      clock: this.options.clock,
      onEvent: (event) => {
        this.onEvent(event);
      },
    });
    this.attack = plan.eventByName('Attack');
    this.commands = new CommandQueue({ plan });
    this.region =
      plan.clientRegion === null
        ? null
        : new RegionSender({
            plan,
            send: (type, values) => this.commands?.enqueue(type, values),
            onRejected: () => {
              // The server kept the previous region: only a different footprint is worth sending (§ 10).
              this.pendingRegion = null;
            },
          });
    this.ping = new PingScheduler({
      pingHz: plan.catalog.tick.pingHz,
      send: (message) => {
        connection.sendPing(message);
      },
      lastAppliedTick: () => this.lastTick,
    });
    this.ping.start();
    this.sendRegion();
  }

  private onClose(): void {
    this.ping?.stop();
    this.ping = null;
    this.commands?.clear();
    // The store stays: a resume refills it with a RESET frame, and the renderer keeps drawing meanwhile.
  }

  private onTick(message: Uint8Array, recvMs: number): void {
    const applier = this.applier;
    if (applier === null) {
      return;
    }

    const started = performance.now();
    applier.apply(message, recvMs);
    this.applyMs = performance.now() - started;
    this.ticks++;
    this.lastTick = applier.tick;
    this.viewComplete = (applier.flags & TickFlags.ViewComplete) !== 0;
    this.byteTimes.push(recvMs);
    this.byteCounts.push(message.length);
    while (this.byteTimes.length > 0 && recvMs - this.byteTimes[0] > 1000) {
      this.byteTimes.shift();
      this.byteCounts.shift();
    }

    // Command outcomes arrive in the frame that carries their effects (§ 8): a refused region is one of them.
    const region = this.region;
    if (region !== null) {
      for (let i = 0; i < applier.acks.count; i++) {
        region.onAck(applier.acks.seq[i], applier.acks.reason[i]);
      }
    }

    // One batch a frame, sharing its client tick — the newest applied server tick (§ 10).
    region?.poll();
    const connection = this.client.connection;
    if (connection !== null) {
      this.commands?.flush(applier.tick, (bytes) => {
        connection.send(bytes);
      });
    }
  }

  private onEvent(event: EventRecord): void {
    if (this.attack === null || event.type !== this.attack) {
      return;
    }

    const target = event.number('target');
    this.options.events.onAttack(event.tick, 0, target, event.number('amount'), this.healthOf(target));
  }

  /** The target's health after the hit: events apply after this frame's updates, so the store already holds it. */
  private healthOf(netId: number): number {
    const world = this.applier?.world;
    if (world === undefined) {
      return 0;
    }

    const location = world.locate(netId);
    if (location === NOT_FOUND) {
      return 0;
    }

    const store = world.archetypeStore(archetypeOf(location));
    const field = store.fieldIndex('hp');
    return field < 0 ? 0 : store.fieldAt(field)[slotOf(location)];
  }

  private sendRegion(): void {
    const pending = this.pendingRegion;
    if (pending === null || this.region === null || this.paused) {
      return;
    }

    const { x, z, radius } = pending;
    this.region.setRegion(
      [x - radius, z - radius, x + radius, z - radius, x + radius, z + radius, x - radius, z + radius],
      this.options.altitudeM ?? radius,
      this.options.budgetKiBps ?? 256,
    );
  }

  private wireBytesPerSec(): number {
    let total = 0;
    for (const count of this.byteCounts) {
      total += count;
    }

    return total;
  }

  /** What the server's `STATS` block says, when the catalog declares the metrics it comes from. */
  private serverStats(): SourceStats['server'] {
    const applier = this.applier;
    if (applier === null || !applier.stats.received) {
      return null;
    }

    const value = (name: string): number => {
      const metric = applier.plan.metricByName(name);
      return metric === null ? 0 : applier.stats.valueOf(metric);
    };
    return {
      simMs: value('typhon.tick.p50'),
      replicationMs: value('typhon.subscriptions.track.p99'),
      watched: applier.world.entityCount,
      effectiveRadius: this.pendingRegion?.radius ?? 0,
      worldEntities: value('typhon.sessions'),
    };
  }
}
