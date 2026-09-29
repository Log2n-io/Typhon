import {
  archetypeOf,
  Capabilities,
  CommandQueue,
  CommandRefused,
  DebugGrid,
  DebugSubType,
  FrameApplier,
  NOT_FOUND,
  PingScheduler,
  PushGeometry,
  PushShape,
  ReconnectingClient,
  RegionSender,
  slotOf,
  StreamRecorder,
  TickFlags,
  WireReader,
  type AggregateGrid,
  type Clock,
  type Connection,
  type EventRecord,
  type MessagePlan,
  type SessionInfo,
  type WorldStore,
} from '@typhondb/client';
import { hullInRadiusM } from './replication-stats';
import { realmViewOf, type RealmView } from './realm-view';

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
  /**
   * What this session costs the server, and what the link between them is doing (CLI3D-06). `null` when the source
   * cannot say — the mock, which has no session.
   *
   * **Every figure here is the SERVER's or the link's, never the client's opinion of them.** `outBytesPerSec` is the
   * session-scoped `STATS` metric, not {@link wireBytesPerSec}, which is this client's own estimate over arrival times:
   * the two are shown side by side deliberately, because a gap between them is a fact worth seeing.
   */
  readonly session: SessionCost | null;
}

/** What one session costs the server, from server-authoritative metrics and the link's own counters. */
export interface SessionCost {
  /** Round trip from the `PING` loop, milliseconds; 0 before the first `PONG`. */
  readonly rttMs: number;
  /** `typhon.session.outBytesPerSec` — what the server says it is sending THIS session. */
  readonly outBytesPerSec: number;
  /** `typhon.session.skippedFrames` — frames the server chose not to send this session (a counter, not a rate). */
  readonly skippedFrames: number;
  /** `typhon.session.droppedCommands` — commands the server refused, rate limiting included. */
  readonly droppedCommands: number;
  /** Region requests this client sent, held back as not worth sending, and had refused. */
  readonly regionsSent: number;
  readonly regionsSuppressed: number;
  readonly regionsRejected: number;
}

/** Receives events as frames are applied, after enters and updates and before leaves (`03-wire-protocol.md` § 5). */
export interface EventSink {
  /** A hit. A netId of 0 is an end the client does not know; `hpAfter` is the target's health fraction after the hit. */
  onAttack(tick: number, attackerNetId: number, targetNetId: number, damage: number, hpAfter: number): void;
  /**
   * Something said within earshot (SWG-09).
   *
   * A session receives these only for speakers near its own viewpoint — 50 m, the range SWG's `/say` carried — and only
   * in its own realm. So a quiet view is the routing working, not the feature missing.
   */
  onChat(tick: number, speakerNetId: number, text: string): void;
}

/** A simulated network link: the mock has one, a real connection does not. */
export interface LatencyControl {
  setLatency(latencyMs: number, jitterMs: number): void;
}

/**
 * What the server says about this session's replication, from the `DEBUG` block (CLI3D-03).
 *
 * This is the engine's own account, not the client's: {@link geometry}`.radiusM` is the radius the session is **served**
 * after any clamp, and its window is the cells actually delivered — which is the whole point of drawing it. Both objects
 * are rewritten in place as frames arrive, so a reader compares `version` rather than identity.
 */
export interface DebugView {
  /** The replication grid, or `null` before the session's first frame. */
  readonly grid: DebugGrid | null;
  /** The session's shape and delivered cells, or `null` while the server has sent none. */
  readonly geometry: PushGeometry | null;
}

/** The one retained {@link DebugView} a source rewrites; readers only ever see it through the readonly face. */
interface MutableDebugView {
  grid: DebugGrid | null;
  geometry: PushGeometry | null;
}

export interface DataSource {
  start(): void;
  dispose(): void;
  /** Whether {@link setPaused} does anything a viewer can see. */
  readonly canPause: boolean;
  /**
   * The store this source fills, when it owns one. A live connection does: only the server's catalog can size a store,
   * and it arrives after the app is up — so the app adopts this once it appears, and again after a reconnect to a server
   * whose catalog moved. A source that fills the world it was handed reports that same world here.
   */
  readonly world: WorldStore | null;
  /** The far tier's counts for {@link world}, or `null` when the session has no aggregate grid. */
  readonly grid: AggregateGrid | null;
  /**
   * The realm the session is in, or `null` in none — the scene the renderer must be drawing.
   *
   * <b>An accessor the app polls, not a callback it subscribes to.</b> The SDK's `onRealmChanged` fires inside
   * `applier.apply()`, on the tick path: building a scene there would put the cost of a terrain swap inside the
   * decode, and a throw would take the session down with it. So a source derives this once per change and the app
   * notices it the way it already notices a new store — one reference compare at the top of the frame
   * (`ClientApp.adoptSourceWorld`, whose comment explains why there is no other right moment).
   */
  readonly realm: RealmView | null;
  /**
   * The near tier's radius the source is actually being served, in metres, or 0 when it is not known yet.
   *
   * Its own accessor because the renderer reads it EVERY frame, and {@link stats} is a snapshot that allocates.
   */
  readonly effectiveRadiusM: number;
  /**
   * What the server reports about this session's replication, or `null` when the source cannot say — the mock, or a
   * live session the server did not grant the `DEBUG` cap.
   */
  readonly debug: DebugView | null;
  /** The god camera's region of interest: a ground point and a radius (the built-in `ClientRegion` command). */
  setRegion(x: number, z: number, radius: number): void;
  setPaused(paused: boolean): void;
  /** Whether {@link viewRealm} does anything: a live session whose catalog declares the command. */
  readonly canViewRealm: boolean;
  /**
   * Asks the server to put this session in another realm. The answer is a `RESET|REALM` frame, or an `ACKS` refusal.
   *
   * <b>An ask, not a move.</b> The server decides — a realm that does not exist, or a session that is a player's rather
   * than a camera's, is refused — so nothing here may assume the realm changed.
   */
  viewRealm(realmId: number): void;
  readonly stats: SourceStats;
  /** The simulated link's control, or null for a real connection. */
  readonly latency: LatencyControl | null;
}

export interface TyphonSourceOptions {
  /** The server refused a realm change, or the local rate limit did. Nothing else will ever say so. */
  readonly onRealmRefused?: () => void;
  /** The server's WebSocket URL, which must speak `typhon.3`. */
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
  private chat: MessagePlan | null = null;
  private pauseCommand: MessagePlan | null = null;
  private viewRealmCommand: MessagePlan | null = null;
  /** The sequence of the `ViewRealm` still awaiting an answer, or -1. See {@link viewRealm}. */
  private pendingRealmSeq = -1;
  /** The realm the last `REALM` block put this session in, derived once per change rather than per frame. */
  private currentRealm: RealmView | null = null;
  private pendingRegion: { x: number; z: number; radius: number } | null = null;
  private lastSentRegion: { x: number; z: number; radius: number } | null = null;
  private paused = false;
  /** The `DEBUG` block's two payloads, decoded in place: one object each for the session's life, not one per frame. */
  private readonly debugGrid = new DebugGrid();
  private readonly debugGeometry = new PushGeometry();
  private readonly debugReader = new WireReader();
  private readonly debugView: MutableDebugView = { grid: null, geometry: null };
  private debugGridSeen = false;
  private debugGeometrySeen = false;
  /** The served radius derived from the newest geometry, and the version it came from. */
  private servedRadiusM = 0;
  private servedRadiusVersion = -1;
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
      // DEBUG carries the replication grid and this session's delivered cells (CLI3D-03). The server grants it only under
      // SessionLimits.AllowDebug — the demo's god kind has it, a player's does not — and a refusal is not an error: the
      // SDK checks the granted set against what was asked and the overlay simply has nothing to draw.
      caps: Capabilities.Stats | Capabilities.Debug,
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

  /** What the server has said about this session's replication; both halves stay `null` until the first one arrives. */
  /**
   * The server's account of this session's replication.
   *
   * One retained object, rewritten in place — which is what {@link DebugView}'s own contract says and what this getter did
   * not do. It is read once a frame in the live path, so a fresh literal here was 60 objects a second, invisible against
   * the mock (whose `debug` is `null`) and real against a server.
   */
  get debug(): DebugView {
    this.debugView.grid = this.debugGridSeen ? this.debugGrid : null;
    this.debugView.geometry = this.debugGeometrySeen ? this.debugGeometry : null;
    return this.debugView;
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

  /**
   * The near tier's radius the session is **actually served**, in metres, or 0 before it is known.
   *
   * **This reads the server's own geometry first** (CLI3D-06, possible only since CLI3D-03 put `PUSH_GEOMETRY` on the
   * client): a sphere reports R′ as the session holds it, and a region reports the in-radius of the hull the server
   * kept. Both are what the engine is serving *after* any clamp. Falling back to the requested radius is a last resort
   * for a session with no `DEBUG` cap, and it is the number that was previously shown always — so the ground's
   * near-radius ring and the heatmap fade were drawn at the radius the camera **asked for**, which is exactly the
   * quantity #1075 says the server silently shrinks.
   */
  get effectiveRadiusM(): number {
    const g = this.debugGeometrySeen ? this.debugGeometry : null;
    if (g !== null) {
      // Recomputed only when the server sent a new geometry: this is read once per frame by the renderer, and the hull
      // walk is a square root per edge.
      if (g.version !== this.servedRadiusVersion) {
        this.servedRadiusVersion = g.version;
        this.servedRadiusM = g.shape === PushShape.Sphere ? g.radiusM : hullInRadiusM(g);
      }

      if (this.servedRadiusM > 0) {
        return this.servedRadiusM;
      }
    }

    return this.lastSentRegion?.radius ?? this.pendingRegion?.radius ?? 0;
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
      session: this.sessionCost(),
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

    // Cleared, because stats.running is derived from it: a disposed source reported itself as running for the rest of the page's life.
    this.applier = null;
  }

  /** The god camera's disc, sent as the smallest quad that contains it (W28: a quad is the minimum footprint). */
  setRegion(x: number, z: number, radius: number): void {
    this.pendingRegion = { x, z, radius };
    this.sendRegion();
  }

  /**
   * Stops and starts the SERVER's simulation, when the catalog declares the command for it.
   *
   * The world stops for every session, not just this one — see `SetPaused` in `TatooineReplication.cs` for why that is
   * a demo control and what has to replace it. The region is held too, so the view does not wander while frozen.
   */
  setPaused(paused: boolean): void {
    this.paused = paused;
    if (this.pauseCommand !== null) {
      this.commands?.enqueue(this.pauseCommand, { paused: paused ? 1 : 0 });
    }

    if (!paused) {
      // A region set DURING the pause was stored and never sent, because sendRegion returns early while paused — so the server kept serving the
      // pre-pause disc until the camera happened to move again.
      this.sendRegion();
    }
  }

  /** Whether this session may ask to look at another realm: the catalog declares the command and the role may send it. */
  get canViewRealm(): boolean {
    return this.viewRealmCommand !== null;
  }

  /**
   * Asks to be put in another realm.
   *
   * Rate-limited by the catalog at one per tick with a burst of two, because every accepted ask costs a whole-realm
   * RESET — the dearest frame there is. A refused one is answered in `ACKS` rather than dropped, so a client that is
   * waiting on the switch learns that it is not coming.
   */
  viewRealm(realmId: number): void {
    if (this.viewRealmCommand === null) {
      return;
    }

    // The sequence is KEPT, so the refusal the server sends can be matched to the ask that caused it. Without this the
    // `ACKS` block — which the server was deliberately given reason codes to fill — reaches nothing, and a viewer whose
    // crossing was refused waits out the fade's whole safety limit behind an opaque screen for an answer that had
    // already arrived.
    const seq = this.commands?.enqueue(this.viewRealmCommand, { realm: realmId }) ?? CommandRefused.RateLimited;
    this.pendingRealmSeq = seq >= 0 ? seq : -1;
    if (seq < 0) {
      // The local bucket was empty, so nothing was sent and no ack will ever come for it. Refused here, immediately,
      // rather than by a timeout later.
      this.options.onRealmRefused?.();
    }
  }

  /** The realm this session is in, or `null` before its first `REALM` block and after a `REALM(NONE)`. */
  get realm(): RealmView | null {
    return this.currentRealm;
  }

  /** Whether the session can stop the server's simulation: the catalog declares the command. */
  get canPause(): boolean {
    return this.pauseCommand !== null;
  }

  private onWelcome(session: SessionInfo, connection: Connection): void {
    const plan = session.plan;
    this.applier = new FrameApplier(plan, {
      clock: this.options.clock,
      onEvent: (event) => {
        this.onEvent(event);
      },
      // The server drops the region it held on a realm change: the sender forgets it and the camera's goes out again.
      //
      // Deriving the view here rather than in the app is deliberate, and it is the only work this callback may do.
      // It runs INSIDE `applier.apply()`, on the tick path: the app reads `realm` at the top of its frame instead,
      // so a scene swap costs the frame that notices it rather than the decode that caused it.
      onRealmChanged: (_previous, current) => {
        this.currentRealm = realmViewOf(current);
        this.pendingRealmSeq = -1;
        this.region?.realmChanged();

        // The PENDING region is dropped, not re-sent. It holds the camera's position in the realm being LEFT, and the
        // camera does not move until `resetForRealm` runs at the top of the next frame — so re-sending it here encodes
        // planet coordinates in the new realm's frame. `encodeQuant` clamps rather than throws, so a planet-sized quad
        // collapses every corner onto one corner of a 64 m interior: a degenerate hull the server then refuses. The
        // camera sends its own the moment it has moved.
        this.pendingRegion = null;
        this.lastSentRegion = null;
      },
      onDebug: (subType, data, offset, length) => {
        this.onDebug(subType, data, offset, length);
      },
    });
    this.attack = plan.eventByName('Attack');
    this.chat = plan.eventByName('Chat');

    // A demo control: the server stops simulating for everyone. Absent from a catalog that does not declare it, in which
    // case pausing does what it did before — nothing but hold the region.
    this.pauseCommand = plan.commandByName('SetPaused');
    this.viewRealmCommand = plan.commandByName('ViewRealm');
    this.commands = new CommandQueue({ plan });
    this.region =
      plan.clientRegion === null
        ? null
        : new RegionSender({
            plan,
            send: (type, values) => this.commands?.enqueue(type, values),
            // The session's realm (typhon.3): a region is a polygon in a flat realm, a polyhedron in a deep one.
            realm: () => this.applier?.realmFrame ?? null,
            onRejected: () => {
              // The server kept the previous region, so the NEXT identical request is the one not worth sending — which is a fact about what was last
              // sent, not a reason to forget where the camera is. Clearing pendingRegion also zeroed the reported effective radius and made the
              // altitude default unreachable for the rest of the session, because sendRegion bails when there is nothing pending.
              this.lastSentRegion = this.pendingRegion;
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

  /**
   * A `DEBUG` sub-block. Unknown ones are ignored on purpose: the block is defined to be skippable, and `CLUSTER_AABBS`
   * and `MIGRATIONS` are reserved for sub-types this client does not draw yet.
   *
   * A malformed payload is swallowed with a note rather than closing the session. `DEBUG` is an *overlay*: the rest of
   * the frame has already been applied and is correct, and `PUSH_GEOMETRY` is explicitly experimental — its layout may
   * change without a protocol version, so a server one step ahead of this client must cost the overlay, not the view.
   */
  private onDebug(subType: number, data: Uint8Array, offset: number, length: number): void {
    const r = this.debugReader.reset(data.subarray(offset, offset + length));
    try {
      if (subType === DebugSubType.Grid) {
        this.debugGrid.readFrom(r);
        this.debugGridSeen = true;
      } else if (subType === DebugSubType.PushGeometry) {
        this.debugGeometry.readFrom(r);
        this.debugGeometrySeen = true;
      }
    } catch (error) {
      console.warn(
        `typhon: a DEBUG sub-block 0x${subType.toString(16)} did not decode; the overlay keeps the last one`,
        error,
      );
    } finally {
      r.release();
    }
  }

  private onClose(): void {
    this.ping?.stop();
    this.ping = null;
    this.commands?.clear();
    // The store stays: a resume refills it with a RESET frame, and the renderer keeps drawing meanwhile.
    // So does the realm, for the same reason and more strongly — a resumed session's first frame is a
    // `RESET|REALM` of the realm its token recorded (12-realms § 1.6), so clearing it here would tear the scene
    // down and build the same one back for the length of a reconnect.
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
    for (let i = 0; i < applier.acks.count; i++) {
      const seq = applier.acks.seq[i];
      region?.onAck(seq, applier.acks.reason[i]);

      // A refused realm change. The realm itself never arrives, so this is the only thing that will ever tell the app
      // the crossing is not coming — everything else waits for a frame that was never going to be sent.
      if (seq === this.pendingRealmSeq) {
        this.pendingRealmSeq = -1;
        this.options.onRealmRefused?.();
      }
    }

    // One batch a frame, sharing its client tick — the newest applied server tick (§ 10).
    region?.poll();
    const connection = this.client.connection;
    if (connection !== null) {
      // Realm-framed fields (a region's vertices) travel over the session's realm frame (typhon.3).
      this.commands?.flush(
        applier.tick,
        (bytes) => {
          connection.send(bytes);
        },
        applier.realmFrame,
      );
    }
  }

  private onEvent(event: EventRecord): void {
    if (this.chat !== null && event.type === this.chat) {
      this.options.events.onChat(event.tick, event.number('speaker'), event.text('text'));
      return;
    }

    if (this.attack === null || event.type !== this.attack) {
      return;
    }

    const target = event.number('target');
    // Both ends, and either may be 0: the engine names an entity by the netId the SESSION holds, so an end this session
    // was never shown travels as 0. The sink's contract already says so, and the renderer needs two ends for a line.
    this.options.events.onAttack(
      event.tick,
      event.number('attacker'),
      target,
      event.number('amount'),
      this.healthOf(target),
    );
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
    const quad = [x - radius, z - radius, x + radius, z - radius, x + radius, z + radius, x - radius, z + radius];
    // A deep realm takes a polyhedron: the quad as a box, radius deep on each side of the ground plane.
    const vertices =
      this.region.dims === 3
        ? [-radius, radius].flatMap((y) => [0, 2, 4, 6].flatMap((i) => [quad[i], quad[i + 1], y]))
        : quad;
    this.region.setRegion(vertices, this.options.altitudeM ?? radius, this.options.budgetKiBps ?? 256);
  }

  private wireBytesPerSec(): number {
    // Counted over the last second WITHOUT trimming the window: this is reached through a getter, and a getter that
    // mutates is a trap for whoever reads it next. It still has to ignore entries older than a second, or a stalled
    // session would read as a healthy one — the rate would freeze at the last second it saw, which is the opposite of
    // what the number is for. The window itself is trimmed in `onTick`, the one place that can make it grow.
    const horizon = performance.now() - 1000;
    let total = 0;
    for (let i = 0; i < this.byteTimes.length; i++) {
      if (this.byteTimes[i] >= horizon) {
        total += this.byteCounts[i];
      }
    }

    return total;
  }

  /** What the server's `STATS` block says, when the catalog declares the metrics it comes from. */
  private serverStats(): SourceStats['server'] {
    const applier = this.applier;

    // `received` is "the frame just applied carried a STATS block", and a server emits one a SECOND — so it is false on
    // nine frames out of ten at 10 Hz, and the HUD, which samples four times a second, essentially never saw one. What
    // says a block has ever arrived is `tick`, which stays at the newest one (−1 before the first).
    if (applier === null || applier.stats.tick < 0) {
      return null;
    }

    const value = (name: string): number => {
      const metric = applier.plan.metricByName(name);
      return metric === null ? 0 : applier.stats.valueOf(metric);
    };

    /** A labelled metric summed over its labels: `typhon.archetype.entities` carries one value per archetype. */
    const total = (name: string): number => {
      const metric = applier.plan.metricByName(name);
      if (metric === null) {
        return 0;
      }

      let sum = 0;
      for (let i = 0; i < metric.valueCount; i++) {
        sum += applier.stats.valueOf(metric, i);
      }

      return sum;
    };
    return {
      simMs: value('typhon.tick.p50'),
      replicationMs: value('typhon.subscriptions.track.p99'),
      watched: applier.world.entityCount,
      effectiveRadius: this.effectiveRadiusM,

      // typhon.archetype.entities, not typhon.sessions: the latter is how many clients are connected, which is not the world's population by any
      // reading and was being shown to the HUD under that name. SUMMED over its labels — the metric carries one value per
      // archetype, so reading value 0 showed the world's population as the number of city NPCs in it.
      worldEntities: total('typhon.archetype.entities'),
    };
  }

  /**
   * What this session costs the server (CLI3D-06), or `null` before the first `STATS` block.
   *
   * The three `typhon.session.*` metrics are **session-scoped**: the server publishes them per session, so these are
   * this client's own numbers rather than the server's totals. `skippedFrames` and `droppedCommands` are counters and
   * are reported as such — they only ever rise, and a HUD that showed them as rates would be lying.
   */
  private sessionCost(): SessionCost | null {
    const applier = this.applier;
    if (applier === null || applier.stats.tick < 0) {
      return null;
    }

    const value = (name: string): number => {
      const metric = applier.plan.metricByName(name);
      return metric === null ? 0 : applier.stats.valueOf(metric);
    };

    return {
      rttMs: this.ping?.rttMs ?? 0,
      outBytesPerSec: value('typhon.session.outBytesPerSec'),
      skippedFrames: value('typhon.session.skippedFrames'),
      droppedCommands: value('typhon.session.droppedCommands'),
      regionsSent: this.region?.sentCount ?? 0,
      regionsSuppressed: this.region?.suppressedCount ?? 0,
      regionsRejected: this.region?.rejectedCount ?? 0,
    };
  }
}
