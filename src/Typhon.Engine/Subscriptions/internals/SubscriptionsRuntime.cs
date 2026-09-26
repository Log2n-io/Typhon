using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// Everything engine-owned replication holds for the life of a runtime: the compiled plan, the catalog a client negotiates against, the session table, the
/// pools and the per-archetype replication state. Built once at <see cref="TyphonRuntime.Start"/>, torn down once at <see cref="TyphonRuntime.Dispose"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is where a later slice adds its field, not <see cref="SubscriptionsContext"/></b> (archive/Subscriptions/09-phase1-build-plan § 4). The context
/// is tick-scoped and shared by every stage; it holds this object through one field and nothing else. Without that boundary, eight independently-built
/// slices would each add a member to one file and every merge would conflict over it.
/// </para>
/// <para>
/// <b>It is also where the replication state is first constructed in production.</b> <see cref="ArchetypeReplicationState"/>,
/// <see cref="ReplicationBlockPool"/> and <see cref="ReplicationDirectory"/> were complete, tested primitives that nothing outside a test ever built, so
/// <see cref="ArchetypeClusterState.ReplicationState"/> was null everywhere and the ECS drain hook could not run. Attaching one state per replicated archetype
/// here is what makes that hook reachable.
/// </para>
/// <para>
/// <b>Nothing is built when nothing is declared.</b> A runtime whose application never touched <see cref="TyphonRuntime.Subscriptions"/> gets an inactive
/// instance: no plan, no catalog, no session table — which is a 544 KiB allocation at the default <see cref="SubscriptionsOptions.MaxSessions"/> — and no
/// block pool. An unused subsystem costs a database exactly nothing.
/// </para>
/// <para>
/// <b>Thread safety.</b> Built on the thread that calls <c>Start</c>, before the scheduler's workers exist, and disposed after they have been joined. Between
/// those two points every member here is read-only; the mutable state lives inside the objects it owns, each with its own contract.
/// </para>
/// <para>
/// <b>It is also the connection layer's <see cref="ISubscriptionsHost"/>, and the only one in production.</b> The interface is implemented EXPLICITLY,
/// member by member, because two of its names mean something else here: the host's <c>Sessions</c> is the application's
/// <see cref="SubscriptionsSessions"/> — declared kinds and the admission hook — while this object's <see cref="Sessions"/> is the native
/// <see cref="SessionTable"/>. Implicit implementation would have forced one of the two to be renamed for the other's benefit. Everything a connection reads is
/// either immutable after <c>Start</c> (the catalog, its hash, the metric flag) or a published counter (the three tick values); none of it is a session row,
/// which is what <c>SUB-05</c> reserves for the tick.
/// </para>
/// </remarks>
internal sealed unsafe class SubscriptionsRuntime : ISubscriptionsHost, IDisposable
{
    /// <summary>Microseconds per <see cref="Stopwatch"/> tick, resolved once: the conversion on the <c>PONG</c> path, which a transport thread runs.</summary>
    private static readonly double MicrosecondsPerStopwatchTick = 1_000_000.0 / Stopwatch.Frequency;

    private ArchetypeReplicationState[] _replicationStates = [];
    private readonly SessionTable _sessions;
    private readonly IngressRingPool _ingressRings;
    private readonly SubscriptionsIngress _ingress;
    private readonly FrameAssembler _frames;
    private readonly SendPump _sendPump;
    private bool _disposed;

    // ── the tick state a transport thread reads ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // Written once per tick by the tick driver (TyphonRuntime.OnTickStartInternal) and read by whichever thread happens to be answering a HELLO or a PING.
    // Three volatile writes and no allocation, which is the whole cost of making WELCOME and PONG tell the truth about where the server is. SUB-05 allows
    // exactly this: numbers published atomically by the tick, read by a transport thread, never a session row and never a managed reference.
    //
    // The origin is a Stopwatch timestamp rather than a microsecond count, because "how far into the tick" has to ADVANCE between ticks — a published count
    // would read the same value for the whole tick and place every round trip at the tick boundary.
    private long _tickOriginTimestamp;
    private uint _currentTick;
    private IngressPolicy _ingressPolicy;
    private uint _tickPeriodUs;

    // COMMANDS messages that arrived well-formed and in state with nowhere to go. P1-05 turns this into a write into the session's ingress ring.
    private long _commandMessagesDropped;

    /// <summary>
    /// Compiles the declarations, builds the catalog and attaches the per-archetype replication state — or does nothing at all when nothing was declared.
    /// </summary>
    /// <param name="engine">The engine whose archetypes, layouts and spatial grid the declarations resolve against.</param>
    /// <param name="registry">The frozen registry.</param>
    /// <param name="options">The runtime's options: the tick rate the codecs are sized against, and the replication options.</param>
    /// <param name="parent">Resource-graph parent for the pools and the session table — the scheduler, as the identity allocator's already is.</param>
    /// <param name="netIds">The database's identity allocator, shared by every replicated archetype and owned by the runtime, not by this object.</param>
    /// <param name="systemNames">The scheduled systems' names, in schedule order: the labels of the built-in per-system metric.</param>
    /// <param name="telemetry">
    /// The track's own per-tick timing, which the stages write and <c>STATS</c> reads. Pass the one the tick context owns; a <see langword="null"/> makes a
    /// private instance, which is right for a harness that drives the stages by hand — nothing writes to it, so the track metric honestly reads zero rather
    /// than reporting another runtime's numbers.
    /// </param>
    /// <exception cref="InvalidOperationException">A declaration cannot be compiled, or names something the engine does not hold.</exception>
    /// <exception cref="CatalogException">The declarations produce a catalog that breaks a wire rule.</exception>
    public SubscriptionsRuntime(DatabaseEngine engine, SubscriptionsRegistry registry, RuntimeOptions options, IResource parent, NetIdAllocator netIds,
        IReadOnlyList<string> systemNames, SubscriptionsTelemetry telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(netIds);

        Registry = registry;
        Options = registry.Options;
        NetIds = netIds;

        if (!HasDeclarations(registry))
        {
            Plans = [];
            return;
        }

        if (Options.CloseStalledAfter <= TimeSpan.Zero)
        {
            // Refused rather than clamped. Zero reads as "never close a stalled session", and the netId quarantine is sized from this window (D1): with no
            // bound on how long a session may be skipped, an identity it still holds could be reissued while its next frame is still owed, which is the
            // leave-then-enter collision in one frame that SUB-06 exists to prevent. A positive duration that converts to very few ticks is floored instead,
            // because that is an operator asking for a short bound on a slow server rather than for no bound at all.
            throw new InvalidOperationException(
                $"SubscriptionsOptions.CloseStalledAfter is {Options.CloseStalledAfter}. It bounds how long a session may be stalled before it is closed, and "
                + "the network-identity quarantine is sized from it, so it must be positive.");
        }

        // ORDER IS THE POINT, and it is the reverse of Dispose's. Each step consumes the one above it: the plan resolves the declarations against the engine's
        // layouts, the catalog is emitted from the plan, and the replication state is carved to the block layout the plan computed. Anything that throws part
        // way leaves a half-built object behind, so the whole of it unwinds through Dispose rather than through a partially-initialised field.
        try
        {
            NominalTickPeriodSeconds = 1.0 / Math.Max(1, options.BaseTickRate);
            NominalTickPeriodUs = NominalTickPeriodUsFor(options.BaseTickRate);

            // From the detector rather than from BaseTickRate / MinTickRateHz: that ratio only FILTERS the fixed ladder, so the real ceiling is the ladder's
            // last surviving entry — at most 6 (finding F1). A vel codec derived from the ratio over-sizes every segment on a runtime whose ratio exceeds it.
            // A second detector instance is not a second copy of that arithmetic: the ladder stays in one place, which is what the exposed property is for.
            LargestTickMultiplier = new OverloadDetector(options.Overload, options.BaseTickRate).MaxTickMultiplier;

            Plans = ProjectionCompiler.Compile(registry, engine, NominalTickPeriodSeconds, LargestTickMultiplier, Options.ReplicationCellM,
                Options.VisibilitySlackMForTest);

            Catalog = CatalogBuilder.Build(registry, Plans, CatalogBuilder.DefaultAppName, appRevision: 0, (int)NominalTickPeriodUs, systemNames);

            // The served realm's frame (typhon.3): what every position a session of it receives, and every position it sends, is quantized over.
            // Every registered realm's kind must be declared (12-realms § 2.7): a kind nobody declared picks no variant.
            var kinds = Catalog.Canonical.RealmKinds ?? [""];
            var registered = engine.RealmTable != null ? engine.RealmTable.Registered : [];
            foreach (var realm in registered)
            {
                var kind = realm.Config?.Replication?.Kind;
                if (kind != null && Array.IndexOf(kinds, kind) < 0)
                {
                    throw new InvalidOperationException(
                        $"Realm {realm.Id.Value} is of kind '{kind}', which Subscriptions.RealmKinds does not declare (12-realms § 2.7).");
                }
            }

            Realm0Frame = BuildRealm0Frame(engine, Options, Math.Max(0, Array.IndexOf(kinds,
                engine.RealmTable?.TryGet(RealmId.Default.Value)?.Config?.Replication?.Kind ?? "")));

            _sessions = new SessionTable("Subscriptions.Sessions", parent, engine.MemoryAllocator, Options, registry.Sessions.SessionEvents);

            _replicationStates = AttachReplicationStates(engine, parent, netIds);

            // Built after the session table, whose rows name each session's profile. It resolves every profile to plan indices here, so the tick path never
            // looks an archetype up by Type.
            Profiles = new SubscriptionProfiles(Plans, registry, _sessions, Catalog.Canonical.RealmKinds);

            // S2b (P1-13b). It owns the frame pool and the per-slot hand-off counters, so a frame's whole lifetime — gathered, encoded, published, released —
            // lives behind one field here rather than spread across the tick-scoped context.
            _frames = new FrameAssembler("Subscriptions.Frames", parent, engine.MemoryAllocator, Options, Plans, Catalog.Canonical, _sessions,
                NominalTickPeriodUs);
            _frames.Profiles = Profiles;
            _frames.Engine = engine;
            _frames.Realm = Realm0Frame;

            // The push path (ADR-067): every archetype some profile observes is served by it.
            var observed = Profiles.ObservedArchetypes;
            var automatic = Profiles.AutomaticArchetypes;

            // An archetype with owner fields has replication state whether or not a profile observes it (11 § 2.4, Q5): owner data follows Control, not
            // geometry, and a controlled entity with no state would have nowhere to be compared. No session's archetype set names it, so it sends no record.
            for (var a = 0; a < Plans.Length; a++)
            {
                if (Plans[a].OwnerFields.Length > 0)
                {
                    if (Plans[a].Position == null)
                    {
                        throw new NotSupportedException(
                            $"Archetype '{Plans[a].Name}' declares owner fields but no position. Owner state is served through the push path, which locates "
                            + "a controlled entity by its position (11 § 2.4); owner data with no place in the world is a shared or keyed source's, "
                            + "which Phase 4 builds. Declare a Position or Motion, or move the fields to the positioned entity the session controls.");
                    }

                    observed[a] = true;
                    Self ??= new SelfTracker(Options.MaxSessions);
                    _replicationStates[a].Self = Self;
                }
            }

            _frames.Self = Self;
            if (Array.IndexOf(automatic, true) >= 0 && !Options.AllowAutomaticPushDetection)
            {
                throw new NotSupportedException(
                    "A profile declares PushDetection.Automatic, which is off: replication is explicit (ADR-067). Call Replicate after each replicated "
                    + "write, or set SubscriptionsOptions.AllowAutomaticPushDetection for the experimental automatic mode.");
            }

            if (Array.IndexOf(observed, true) >= 0)
            {
                // Null only when no spatial grid is configured, and then an observed archetype has no position, which the push path refuses by name first.
                var spatial = engine.Realm0Grid;
                Grid = spatial == null ? null : ReplicationGrid.Resolve(Options.ReplicationCellM, spatial.Config, Profiles.MaxRadius);
                Push = PushReplication.Create(Plans, _replicationStates, observed, automatic, Grid, Options.MaxSessions, Options.PushShadow,
                    Options.ForceDeepReplicationForTest);

                // The engine-wide collector (R4.1), serving realm 0's replication; the stages below loop over its served realms as they are added.
                Hub = new PushHub(_replicationStates, observed, automatic, Push, Options.MaxSessions);

                // SUB-30: what the projection quantizes with (the plans' frames) and what every RESET tells a client (the REALM block) are two computations
                // from the same grid; they must agree to the bit.
                for (var a = 0; a < Plans.Length; a++)
                {
                    var own = Plans[a].Position?.Frame;
                    if (own == null || Realm0Frame == null)
                    {
                        continue;
                    }

                    for (var axis = 0; axis < own.Dims; axis++)
                    {
                        if (own.Bits != Realm0Frame.PositionBits || own.Min[axis] != Realm0Frame.Min[axis] || own.Step[axis] != Realm0Frame.Step[axis])
                        {
                            throw new InvalidOperationException(
                                $"Plan {a}'s position frame and realm 0's REALM block disagree on axis {axis}: a client would decode other places (SUB-30).");
                        }
                    }
                }
                for (var a = 0; a < observed.Length; a++)
                {
                    if (observed[a])
                    {
                        _replicationStates[a].Push = Push;
                        _replicationStates[a].PushArchetypeIndex = a;
                    }
                }

                _frames.Push = Push;
                _engine = engine;
                _observed = observed;
                _automatic = automatic;
                _encodePlans = new ArchetypeEncodePlan[Plans.Length];
                for (var a = 0; a < Plans.Length; a++)
                {
                    _encodePlans[a] = _frames.EncodePlanOf(a);
                }

                ConfigureRealm(Push, Grid, Realm0Frame, first: true);

                // Every other realm is served the first time a session is placed in it (R4.4), from its own grid and replication config.
                Hub.Factory = CreateRealmReplication;
            }

            // The send side (P1-14b). It holds no memory of its own beyond one view and one work item per slot; what it carries is the rule that a frame
            // leaves the engine only after the tick that produced it has flushed.
            _sendPump = new SendPump(_sessions, _frames, Options.MaxSessions, this);

            // Ingress (P1-05). The command registry is bound from the CATALOG, so the decode follows what the client negotiated against rather than a second
            // reading of the declarations; the ring pool is created here because a ring's lifetime is a session's, and sessions live in the table above it.
            CatalogPlan = CatalogPlan.Compile(Catalog.Canonical);
            CommandTypes = CommandRegistry.Build(registry, CatalogPlan);

            // The inbound rails (11 § 4.2). The budget is required once clients can send commands — the application's or ClientRegion — because it is the one
            // bound between a single client and the transport threads' decode work, and only the application knows what its commands cost.
            ValidateIngressRails(Options, CommandTypes.Count);
            _ingressPolicy = new IngressPolicy(Options.IngressBytesPerSecond, (long)(Options.AbuseWindow.TotalSeconds * Stopwatch.Frequency),
                Options.AbuseRefusalsPerWindow, Options.AbuseWindows);
            _ingressRings = new IngressRingPool("Subscriptions.IngressRings", parent, engine.MemoryAllocator, Options);
            _ingress = new SubscriptionsIngress(_sessions, registry, CommandTypes, new CommandTypeBuffers(CommandTypes, Options.MaxSessions), _ingressRings,
                Options.MaxSessions, _sendPump);
            _frames.Ingress = _ingress;
            _ingress.Frames = _frames;
            _ingress.Realm = Realm0Frame;
            _ingress.Realms = engine.RealmTable;
            _ingress.Profiles = Profiles;
            _ingress.MultiRealm = engine.ConfiguredMaxRealms > 1;
            _frames.MultiRealm = _ingress.MultiRealm;
            _ingress.ReplicationStates = _replicationStates;

            // netId → entity for a command's entity references (SUB-26): the projection binds each identity it assigns, and every release unbinds it.
            foreach (var state in _replicationStates)
            {
                if (state != null)
                {
                    state.EntityIndex = _ingress.NetIds;
                }
            }
            // Events (09 § 11): compiled against the catalog, and one commands view per worker slot, so Emit records into the worker's own buffer.
            Events = EventHub.Build(registry, CatalogPlan);
            if (Events != null)
            {
                Events.Realm = Realm0Frame;

                // With several realms a point is a place only with its realm (12-realms § 2.7): a realm-less RouteNear would file every point in realm 0.
                for (var i = 0; engine.ConfiguredMaxRealms > 1 && i < registry.Events.Count; i++)
                {
                    var declaration = registry.Events[i];
                    if (declaration.Routing == EventRouting.Near && declaration.RoutingRealmReader == null)
                    {
                        throw new NotSupportedException(
                            $"Event '{declaration.Name}' routes Near without a realm, and this engine holds several: declare RouteNear(point, realm) " +
                            "(12-realms § 2.7).");
                    }
                }

                // A position field is realm-framed (SUB-30): with no spatial world there is no frame to encode it over, and every emission would be
                // rejected on the tick. Refused here instead.
                for (var i = 0; Realm0Frame == null && i < registry.Events.Count; i++)
                {
                    var body = CatalogPlan.EventByName(registry.Events[i].Name)?.Body;
                    foreach (var field in body?.Fields ?? [])
                    {
                        if (field.Kind is CodecKind.Pos2 or CodecKind.Pos3)
                        {
                            throw new NotSupportedException(
                                $"Event '{registry.Events[i].Name}' carries position '{field.Name}', which needs a spatial world: configure a spatial grid.");
                        }
                    }
                }
            }

            _frames!.Events = Events;
            var workerSlots = (parent as DagScheduler)?.WorkerSlotCount ?? 0;
            Events?.BindWorkerSlots(workerSlots);
            Commands = new SubscriptionsCommands(_ingress, Events);
            _commandsByWorker = new SubscriptionsCommands[workerSlots];
            for (var w = 0; w < workerSlots; w++)
            {
                _commandsByWorker[w] = new SubscriptionsCommands(_ingress, Events, w + 1);
            }

            // STATS (P1-16). Last of the tick-path objects, because it reads across all of them — the session table's open count, the send pump's bytes, the
            // ingress rows' drop counters and the engine's per-archetype entity counts — and attached to the frame assembler rather than constructed by it,
            // which is what keeps the assembler ignorant of every source but the one interface it calls once a tick.
            Stats = new StatsEncoder(CatalogPlan, registry, engine, Plans, _sessions, _sendPump, _ingress, systemNames, NominalTickPeriodUs,
                telemetry ?? new SubscriptionsTelemetry());
            _frames!.AttachStats(Stats);

            // Before the first tick publishes anything, so a client that completes its handshake between Start and the first tick is told the period rather
            // than zero. The tick number and the origin stay zero until a tick runs, which is what they truthfully are.
            _tickPeriodUs = NominalTickPeriodUs;
            IsActive = true;

            // Last, and it escapes `this` deliberately: the acceptor holds the host and nothing else, so nothing it could touch is still half-built. Built
            // here rather than on demand so a transport can be started against a runtime whose acceptor identity never changes.
            Acceptor = new SubscriptionAcceptor(this);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Whether anything was declared, and therefore whether anything was built.</summary>
    public bool IsActive { get; }

    /// <summary>The declarations this was built from.</summary>
    public SubscriptionsRegistry Registry { get; }

    /// <summary>The operator's replication options.</summary>
    public SubscriptionsOptions Options { get; }

    /// <summary>One compiled plan per replicated archetype, in declaration order. Empty on an inactive runtime.</summary>
    public CompiledProjectionPlan[] Plans { get; }

    /// <summary>
    /// The catalog, its canonical bytes and their digest — what <c>WELCOME</c> carries, built exactly once. <see langword="null"/> on an inactive runtime.
    /// </summary>
    /// <remarks>
    /// One object holding all three, from <see cref="CatalogSerializer.Export"/>, so the bytes a client receives and the hash it offers back cannot come to
    /// describe two different declarations. Nothing on the tick path reads it: it is handed to a connection at <c>HELLO</c> and never rebuilt.
    /// </remarks>
    public CatalogExport Catalog { get; }

    /// <summary>The session table. <see langword="null"/> on an inactive runtime, where it would be a 544 KiB allocation for nobody.</summary>
    public SessionTable Sessions => _sessions;

    /// <summary>The database's network identities, shared by every replicated archetype. Used here, owned by <see cref="TyphonRuntime"/>.</summary>
    public NetIdAllocator NetIds { get; }

    /// <summary>Every command type a client may send, bound to the application's structs. <see langword="null"/> on an inactive runtime.</summary>
    public CommandRegistry CommandTypes { get; }

    /// <summary>
    /// The catalog compiled into the plans both directions encode from, built once here. <see langword="null"/> on an inactive runtime.
    /// </summary>
    public CatalogPlan CatalogPlan { get; }

    /// <summary>
    /// The <c>STATS</c> producer: the once-a-second snapshot of every declared metric. <see langword="null"/> on an inactive runtime.
    /// </summary>
    public StatsEncoder Stats { get; }

    /// <summary>
    /// The inbound path: a session's ring, the transport-side decode, and the Engine-Pre drain that turns it into the tick's typed buffers.
    /// <see langword="null"/> on an inactive runtime.
    /// </summary>
    /// <summary>
    /// The nominal tick period a base tick rate implies, in microseconds.
    /// </summary>
    /// <param name="baseTickRate">The runtime's base tick rate, in hertz.</param>
    /// <returns>The period.</returns>
    /// <remarks>
    /// One formula, because more than one thing converts a duration into ticks with it: the frame assembler's stall, lag and silence bounds, and the netId
    /// quarantine the runtime sizes before this object exists. A quarantine computed from a period that rounded differently than the close bound's would be
    /// a SUB-06 hole that nothing in the code reads as one.
    /// </remarks>
    internal static uint NominalTickPeriodUsFor(int baseTickRate)
        => (uint)Math.Round(1_000_000.0 / Math.Max(1, baseTickRate), MidpointRounding.AwayFromZero);

    public SubscriptionsIngress Ingress => _ingress;

    /// <summary>
    /// What an application system reads and answers commands through. <see langword="null"/> on an inactive runtime.
    /// </summary>
    /// <remarks>
    /// The view off a worker — lifecycle hooks and tests — whose events go to slot 0. A system reads <c>ctx.Subscriptions</c>, which is its own worker's
    /// view (<see cref="CommandsFor"/>): holding this one instead would put its events on slot 0 with every other holder's.
    /// </remarks>
    public SubscriptionsCommands Commands { get; }

    private readonly SubscriptionsCommands[] _commandsByWorker = [];

    /// <summary>The commands view a system on worker <paramref name="workerId"/> is handed: its events go to that worker's buffer.</summary>
    /// <param name="workerId">The worker, or <see cref="TickContext.NonWorkerId"/>.</param>
    /// <returns>The view; <see cref="Commands"/> off a worker.</returns>
    public SubscriptionsCommands CommandsFor(int workerId) =>
        (uint)workerId < (uint)_commandsByWorker.Length ? _commandsByWorker[workerId] : Commands;

    /// <summary>
    /// ClientRegion sessions (09 § 7): one window width, sized for the widest extent any profile accepts, bounded like a Sphere's window, and the archetype
    /// sets the near budgets count.
    /// </summary>
    // What every realm's replication is built from (R4.4): the engine, the plans' push flags and encode plans, and the near budgets' archetype sets.
    private DatabaseEngine _engine;
    private bool[] _observed;
    private bool[] _automatic;
    private ArchetypeEncodePlan[] _encodePlans;
    private ArchetypeSet[] _nearSets;

    /// <summary>Why the last realm whose replication could not be built was refused; diagnostics only.</summary>
    internal string LastUnservableRealm { get; private set; }

    // A realm's replication: far fold, encode plans, aggregates over its frame and region windows over its grid. The first (realm 0's, at Start) also binds
    // the profiles' aggregates and near budgets, which are realm-independent.
    private void ConfigureRealm(PushReplication push, ReplicationGrid grid, RealmFrame frame, bool first)
    {
        var (farPhase, farWindow) = Profiles.FarFold;
        push.ConfigureFar(farPhase, farWindow);
        push.AttachEncodePlans(_encodePlans);
        ConfigureAggregates(push, frame, first);
        ConfigureRegions(push, grid);
    }

    /// <summary>
    /// Builds <paramref name="realm"/>'s replication the first time a session is placed in it (R4.4): its grid at its declared cell, its codecs over its
    /// bounds and width, its aggregates and region windows. Null when the realm declares no replication, or when its grid cannot serve the declared
    /// profiles — refused, counted by the hub, never thrown: this runs in the frame prologue.
    /// </summary>
    private PushReplication CreateRealmReplication(ushort realm)
    {
        var entry = _engine?.RealmTable?.TryGet(realm);
        var config = entry?.Config?.Replication;
        if (entry == null || config == null)
        {
            return null;
        }

        try
        {
            // Sized for the variants that serve the realm's kind, not for every profile's (12-realms § 1.4).
            var grid = ReplicationGrid.Resolve(config.CellM, entry.GridConfig, Profiles.MaxRadiusFor(Profiles.KindIndex(config.Kind)));
            var codecs = RealmCodecs.Create(realm, in entry.GridConfig, Plans, config.PositionBits);
            var push = PushReplication.Create(Plans, _replicationStates, _observed, _automatic, grid, Options.MaxSessions, Options.PushShadow,
                Options.ForceDeepReplicationForTest, realm, codecs);
            var frame = BuildRealmFrame(_engine, Options, realm, Math.Max(0, Profiles.KindIndex(config.Kind)));
            ConfigureRealm(push, grid, frame, first: false);
            return push;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            LastUnservableRealm = $"realm {realm}: {e.Message}";
            return null;
        }
    }

    private void ConfigureRegions(PushReplication push, ReplicationGrid grid)
    {
        // ClientRegion (09 § 7): one window width for every region session, sized for the widest extent any profile accepts, and bounded like a Sphere's
        // window — the cells a gather pays for.
        var edge = Profiles.MaxRegionEdgeM;
        if (edge <= 0)
        {
            return;
        }

        // The implementation's depth, not the grid's: the deep one (a flat grid served deep only in tests) keeps W² rows of W cells.
        var window = (long)Math.Ceiling(edge / grid.CellM) + 5;
        var cells = window * window * (push.Deep ? window : 1);
        if (window > 64 || cells > ReplicationGrid.MaxWindowCells)
        {
            var widest = push.Deep ? 9 : 48;
            throw new InvalidOperationException(
                $"A ClientRegion accepts regions {edge} m wide, and with a replication cell of {grid.CellM} m its sessions' window would be " +
                $"{window} cells per axis (⌈maxEdgeM / c⌉ + 5), {cells} cells, past the bound of {ReplicationGrid.MaxWindowCells}. In a " +
                $"{(push.Deep ? "deep" : "flat")} grid maxEdgeM is at most {widest} cells, {widest * grid.CellM} m: lower it, or raise the cell side to at " +
                $"least {Math.Ceiling(edge / widest * 1000d) / 1000d} m.");
        }

        _nearSets ??= Profiles.BindNearCounts().ToArray();
        push.ConfigureRegions((int)window, _nearSets);
    }

    /// <summary>
    /// The aggregate tiers' counts (09 § 8): one per canonical catalog grid, bound to the profiles that read them. A tile must be a whole number of replication
    /// cells over the same origin — the counts follow cell changes, so a tile edge inside a cell would let a move cross it unseen — and every archetype a grid
    /// counts must be one push replication serves.
    /// </summary>
    private void ConfigureAggregates(PushReplication push, RealmFrame realmFrame, bool bindProfiles)
    {
        var observed = _observed;
        var canonical = Catalog.Canonical.Grids ?? [];
        if (canonical.Length == 0)
        {
            return;
        }

        var planOfCanonical = new int[Catalog.Canonical.Archetypes.Length];
        for (var c = 0; c < planOfCanonical.Length; c++)
        {
            planOfCanonical[c] = Array.FindIndex(Plans, p => p.Name == Catalog.Canonical.Archetypes[c].Name);
        }

        var grids = new AggregateCounts[canonical.Length];
        for (var g = 0; g < canonical.Length; g++)
        {
            var grid = canonical[g];

            var columns = new int[Plans.Length];
            Array.Fill(columns, -1);
            for (var j = 0; j < grid.Archetypes.Length; j++)
            {
                var plan = planOfCanonical[grid.Archetypes[j]];
                if (plan < 0 || !observed[plan])
                {
                    throw new NotSupportedException(
                        $"An Aggregate counts '{Catalog.Canonical.Archetypes[grid.Archetypes[j]].Name}', which no profile replicates: its counts come from the " +
                        "push step's events. Observe the archetype in some profile.");
                }

                columns[plan] = j;
            }

            // Laid over the realm's frame (typhon.3, 12-realms § 5.3): the same origin and dimensions a client derives from the REALM block.
            var frame = realmFrame ?? throw new NotSupportedException("An Aggregate needs a spatial world: configure a spatial grid.");
            var tiles = grid.TileCells;

            // A client refuses a grid past the catalog's cell limit when it reads the REALM (1007): refused here, at Start, not on every session's first
            // frame.
            if (frame.AggregateCellCount(tiles) > CatalogValidator.MaxGridCells)
            {
                throw new NotSupportedException(
                    $"Aggregate grid {grid.Idx}: tiles of {tiles} cells of {frame.CellM} m over this world make {frame.AggregateCellCount(tiles)} tiles, " +
                    $"above the {CatalogValidator.MaxGridCells} a client accepts. Declare a larger tile.");
            }

            grids[g] = new AggregateCounts(grid.Idx, frame.Min[0], frame.Min[1], frame.Min[2], tiles * frame.CellM, frame.AggregateDim(0, tiles),
                frame.AggregateDim(1, tiles), frame.AggregateDim(2, tiles), columns, grid.Archetypes.Length);
        }

        push.ConfigureAggregates(grids);
        if (!bindProfiles)
        {
            return;
        }

        // Matched by the tile in cells, as the catalog built it: the metres the grid stores are cells × cellM, which need not equal the declared tile
        // bit for bit (0.3 m over 0.1 m cells is 3 cells, stored as 0.30000000000000004 m).
        var cellM = Realm0Frame.CellM;
        Profiles.BindAggregates((tileM, archetypes) =>
        {
            var tileCells = (long)Math.Round(tileM / cellM);
            for (var g = 0; g < grids.Length; g++)
            {
                if (canonical[g].TileCells != tileCells)
                {
                    continue;
                }

                var same = true;
                var n = 0;
                for (var a = 0; a < grids[g].Columns.Length; a++)
                {
                    if (grids[g].Columns[a] >= 0)
                    {
                        n++;
                        same &= Array.IndexOf(archetypes, a) >= 0;
                    }
                }

                if (same && n == archetypes.Length)
                {
                    return g;
                }
            }

            return -1;
        }, NominalTickPeriodSeconds);
    }

    /// <summary>The declared events' hub, or <see langword="null"/> when no event is declared.</summary>
    public EventHub Events { get; }

    /// <summary>
    /// Realm 0's frame (<c>typhon.3</c>), or <see langword="null"/> without a spatial grid: the <c>REALM</c> block every session's first frame carries, and
    /// the frame its positions — records, events, commands, regions, aggregate grids — are quantized over (SUB-30).
    /// </summary>
    /// <summary>The engine-wide half of push replication: the collector and the served realms' replications (R4.1); null without a push path.</summary>
    public PushHub Hub { get; private set; }

    public RealmFrame Realm0Frame { get; }

    /// <summary>Realm 0's frame: its grid's bounds, the replication cell, the default width, flat when the replication grid is one cell deep.</summary>
    internal static RealmFrame BuildRealm0Frame(DatabaseEngine engine, SubscriptionsOptions options, int kindIdx = 0) =>
        BuildRealmFrame(engine, options, RealmId.Default.Value, kindIdx);

    /// <summary>
    /// The frame a <c>REALM</c> block carries for <paramref name="realm"/> (12-realms § 2.1): its grid's bounds and its replication's cell, position width
    /// and tag. Realm 0 is replicated over <see cref="SubscriptionsOptions.ReplicationCellM"/> — the grid its replication was built on — whatever its
    /// config says; its kind and tag are the config's.
    /// </summary>
    internal static RealmFrame BuildRealmFrame(DatabaseEngine engine, SubscriptionsOptions options, ushort realm, int kindIdx)
    {
        var entry = engine.RealmTable?.TryGet(realm);
        var spatial = realm == RealmId.Default.Value ? engine.Realm0Grid : entry?.Grid;
        if (spatial == null)
        {
            return null;
        }

        var replication = entry?.Config?.Replication;

        ref readonly var config = ref spatial.Config;
        var cellM = realm != RealmId.Default.Value && replication != null ? replication.CellM
            : options.ReplicationCellM > 0 ? options.ReplicationCellM : config.CellSize;
        var generation = engine.PersistedRealmCatalog != null && engine.PersistedRealmCatalog.TryGetValue(realm, out var row)
            ? (ushort)row.Row.Generation
            : (ushort)0;
        return new RealmFrame(realm, generation, kindIdx, replication?.AppTag ?? 0, replication?.PositionBits ?? Codec.DefaultPositionBits, cellM,
            deep: !ReplicationGrid.IsFlat(config, cellM), [config.WorldMin.X, config.WorldMin.Y, config.WorldMin.Z],
            [config.WorldMax.X, config.WorldMax.Y, config.WorldMax.Z]);
    }

    /// <summary>The per-archetype replication state, parallel to <see cref="Plans"/>. Empty on an inactive runtime.</summary>
    public ArchetypeReplicationState[] ReplicationStates => _replicationStates;

    /// <summary>The declared profiles, resolved to plan indices. <see langword="null"/> on an inactive runtime.</summary>
    public SubscriptionProfiles Profiles { get; }

    /// <summary>
    /// S2b: the pass that turns each session's share of the push events into a <c>TICK</c> message and hands it to the send side. <see langword="null"/> on
    /// an inactive runtime.
    /// </summary>
    /// <remarks>
    /// The one field P1-13b adds here, for the reason the class remarks give: everything the frame stage reaches at tick time hangs off this object, so the
    /// tick-scoped <see cref="SubscriptionsContext"/> stays frozen and the slices built beside this one do not meet in it.
    /// </remarks>
    public FrameAssembler Frames => _frames;

    /// <summary>The push path, or <see langword="null"/> when no profile observes anything.</summary>
    internal PushReplication Push { get; private set; }

    /// <summary>The owner routing (11 § 2.2), when an archetype declares owner fields; <see langword="null"/> otherwise.</summary>
    internal SelfTracker Self { get; private set; }

    /// <summary>The replication grid resolved at <c>Start</c>, or <see langword="null"/> when no profile observes anything.</summary>
    internal ReplicationGrid Grid { get; private set; }

    /// <summary>The send side: what carries a published frame to a link.</summary>
    public SendPump SendPump => _sendPump;

    /// <summary>The nominal tick period in seconds, <c>1 / BaseTickRate</c> — what the velocity codecs were sized against.</summary>
    public double NominalTickPeriodSeconds { get; }

    /// <summary>The same period in microseconds: what the catalog declares, and what the published period is a multiple of under overload dilation.</summary>
    public uint NominalTickPeriodUs { get; } = 1;

    /// <summary>
    /// The acceptor every transport reaches replication through. <see langword="null"/> on an inactive runtime, which therefore accepts nothing.
    /// </summary>
    public ISubscriptionAcceptor Acceptor { get; }

    /// <summary>
    /// <c>COMMANDS</c> messages received with no ingress path to take them — an inactive runtime, which has no session table and therefore no rings.
    /// </summary>
    /// <remarks>
    /// Counted rather than ignored, and it should stay at zero: a session cannot exist without a session table, so a message arriving here at all means a
    /// connection outlived the runtime that admitted it. A client sending a well-formed, in-state message is never disconnected for it.
    /// </remarks>
    public long CommandMessagesDropped => Volatile.Read(ref _commandMessagesDropped);

    /// <summary>The largest tick multiplier the runtime may fall back to, from <see cref="OverloadDetector.MaxTickMultiplier"/>.</summary>
    public int LargestTickMultiplier { get; } = 1;

    /// <summary>Whether this object would hand a transport a connection: it was built with declarations, and it has not been disposed.</summary>
    public bool IsAccepting => IsActive && !Volatile.Read(ref _disposed);

    // ── ISubscriptionsHost — explicitly, see the class remarks ──────────────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    SubscriptionsSessions ISubscriptionsHost.Sessions => Registry.Sessions;

    /// <inheritdoc />
    SessionTable ISubscriptionsHost.SessionTable => _sessions;

    /// <inheritdoc />
    byte[] ISubscriptionsHost.CatalogJson => Catalog?.Utf8;

    /// <inheritdoc />
    ulong ISubscriptionsHost.CatalogHash => Catalog?.Hash ?? 0;

    /// <inheritdoc />
    /// <remarks>
    /// Read off the emitted catalog rather than off the registry's declarations, because the catalog carries the built-in metrics too: an application that
    /// declares none still publishes tick and per-system figures, and a <c>STATS</c> capability refused on the strength of the registry would deny a client
    /// the eleven built-ins it was entitled to.
    /// </remarks>
    bool ISubscriptionsHost.HasMetrics => Catalog?.Canonical?.Metrics is { Length: > 0 };

    /// <inheritdoc />
    bool ISubscriptionsHost.IsAccepting => IsAccepting;

    /// <inheritdoc />
    uint ISubscriptionsHost.CurrentTick => Volatile.Read(ref _currentTick);

    /// <inheritdoc />
    uint ISubscriptionsHost.TickPeriodUs => Volatile.Read(ref _tickPeriodUs);

    /// <inheritdoc />
    /// <remarks>
    /// Derived at the moment it is asked for, from the timestamp the tick published — the only shape that can answer the question. It is NOT clamped to the
    /// period: a tick that ran long genuinely is further into itself than its nominal period, and reporting the period back would hide exactly the overrun a
    /// client uses this value to notice.
    /// </remarks>
    uint ISubscriptionsHost.MicrosecondsIntoTick
    {
        get
        {
            var origin = Volatile.Read(ref _tickOriginTimestamp);
            if (origin == 0)
            {
                // No tick has started yet: the honest answer is "at its beginning", not a duration measured from the process's own epoch.
                return 0;
            }

            var elapsed = Stopwatch.GetTimestamp() - origin;
            if (elapsed <= 0)
            {
                return 0;
            }

            var microseconds = elapsed * MicrosecondsPerStopwatchTick;
            return microseconds >= uint.MaxValue ? uint.MaxValue : (uint)microseconds;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Straight into the session's ingress ring (P1-05). A <see cref="WireFormatException"/> out of the decode is deliberately not caught here: the connection
    /// is what turns it into the close code the protocol names, and swallowing it would leave a malformed client connected.
    /// </remarks>
    bool ISubscriptionsHost.RequestPong(SessionId session, uint clientMs) => _sendPump != null && _sendPump.RequestPong(session, clientMs);

    /// <inheritdoc />
    bool ISubscriptionsHost.RequestKick(SessionId session, ushort code, string reason) => _sendPump != null && _sendPump.RequestKick(session, code, reason);

    /// <inheritdoc />
    IngressPolicy ISubscriptionsHost.IngressPolicy => _ingressPolicy;

    /// <summary>
    /// Refuses inbound rails that would not do what they say (11 § 4.2): no budget when clients can send commands, a budget below the largest message a
    /// session may send (it would be refused forever), and an abuse rule that is off or cannot be converted to the clock.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="commandTypes">The command types the catalog declares, <c>ClientRegion</c> included.</param>
    internal static void ValidateIngressRails(SubscriptionsOptions options, int commandTypes)
    {
        if (commandTypes > 0 && options.IngressBytesPerSecond <= 0)
        {
            throw new InvalidOperationException(
                $"The catalog has {commandTypes} command type(s), so clients can send commands, and SubscriptionsOptions.IngressBytesPerSecond is not set. "
                + "It is each session's inbound budget and has no default: size it from your commands' rate and size (a player sending 20 small "
                + "commands a second needs a few KiB/s).");
        }

        if (options.IngressBytesPerSecond > 0 && options.IngressBytesPerSecond < options.ClientMessageBytes)
        {
            throw new InvalidOperationException(
                $"SubscriptionsOptions.IngressBytesPerSecond ({options.IngressBytesPerSecond}) is below ClientMessageBytes ({options.ClientMessageBytes}): "
                + "the budget is one second deep, so a message of the largest allowed size would be refused forever. Raise the budget or lower the cap.");
        }

        if (options.AbuseWindow <= TimeSpan.Zero || options.AbuseWindow > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException($"SubscriptionsOptions.AbuseWindow ({options.AbuseWindow}) must be positive and at most an hour.");
        }

        if (options.AbuseRefusalsPerWindow <= 0 || options.AbuseWindows <= 0)
        {
            throw new InvalidOperationException(
                $"SubscriptionsOptions.AbuseRefusalsPerWindow ({options.AbuseRefusalsPerWindow}) and AbuseWindows ({options.AbuseWindows}) must be positive: "
                + "the abuse rule is always on. To make it lenient, raise them.");
        }
    }

    /// <inheritdoc />
    long ISubscriptionsHost.PolicyRefusalsOf(SessionId session) => _ingress?.RowOf(session)?.PolicyRefusals ?? 0;

    /// <inheritdoc />
    int ISubscriptionsHost.RefuseCommands(SessionId session, ReadOnlySpan<byte> message) => _ingress?.RefuseCommands(session, message) ?? 0;

    /// <inheritdoc />
    void ISubscriptionsHost.BindSessionLink(SessionId session, ISubscriptionLink link)
    {
        if (link == null)
        {
            _sendPump?.DetachLink(session);
            return;
        }

        // The hand-off counters are zeroed HERE, on the admitting thread, before the client can send anything and before any tick prepares the slot. Doing it
        // in the tick's prologue instead put a second writer on the send-side line: the link is bound before WELCOME, so a PING can be calling NotePing while
        // the tick is clearing the whole struct — which is not a shape SUB-05's allow-list permits, however benign a lost first acknowledgement is.
        var frames = _frames;
        if (frames != null && session.IsValid && session.Slot < Options.MaxSessions)
        {
            var send = frames.SendStateOf(session.Slot);
            SessionSendState.Initialize(send);

            // Silence is measured from the handshake, not from the first PING: a client that completes HELLO and then says nothing must be closed on the same
            // schedule as one that stops mid-session, and a zero here would exempt it forever.
            send->NotePing(Volatile.Read(ref _currentTick));
        }

        _sendPump?.AttachLink(session, link);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Onto the slot's send-side line, where the producer reads it. It lands after <see cref="ISubscriptionsHost.BindSessionLink"/> has zeroed the struct —
    /// the connection calls the two in that order on one thread — so the grant is never cleared by the slot's own initialisation.
    /// </remarks>
    void ISubscriptionsHost.NoteCapsGranted(SessionId session, Capabilities caps)
    {
        var frames = _frames;
        if (frames == null || !session.IsValid || session.Slot >= Options.MaxSessions)
        {
            return;
        }

        // The slot must still name THIS session, for the reason NoteSessionPing gives: a HELLO that completes after its session has closed and its row has
        // been re-leased would otherwise grant STATS to whoever holds the slot now — a session that never asked for the capability, and whose client closes
        // 1002 when a block it did not negotiate arrives.
        if (_sessions == null || _sessions.IdAt(session.Slot) != session)
        {
            return;
        }

        frames.SendStateOf(session.Slot)->NoteCapsGranted(caps);
    }

    /// <inheritdoc />
    void ISubscriptionsHost.NoteSessionPing(SessionId session, uint appliedTick)
    {
        var frames = _frames;
        if (frames == null || !session.IsValid || session.Slot >= Options.MaxSessions)
        {
            return;
        }

        // The slot must still name THIS session. A late PING from a socket whose session has closed would otherwise land its acknowledgement and its
        // heard-from mark on whoever holds the slot now, suppressing that session's lag skip and its silence close — the one transport-side entry point that
        // did not validate what every other one does.
        if (_sessions == null || _sessions.IdAt(session.Slot) != session)
        {
            return;
        }

        var send = frames.SendStateOf(session.Slot);
        send->NotePing(Volatile.Read(ref _currentTick));
        send->ReportAppliedTick(appliedTick);
    }

    /// <inheritdoc />
    void ISubscriptionsHost.OnCommands(SessionId session, ReadOnlySpan<byte> message)
    {
        var ingress = _ingress;
        if (ingress == null)
        {
            Interlocked.Increment(ref _commandMessagesDropped);
            return;
        }

        ingress.OnCommands(session, message);
    }

    /// <summary>
    /// Publishes where the tick is, for the transport threads that answer <c>WELCOME</c> and <c>PONG</c>.
    /// </summary>
    /// <param name="tickNumber">The tick about to run. Truncated to the wire's <c>u32</c>, which wraps after 2³² ticks — 1.4 years at 100 Hz.</param>
    /// <param name="tickOriginTimestamp">The <see cref="Stopwatch"/> timestamp the tick started at; the driver already took it for its delta time.</param>
    /// <param name="tickMultiplier">The overload multiplier this tick was scheduled at, so the published period is the dilated one, not the nominal.</param>
    /// <remarks>
    /// <para>
    /// Called once per tick on the tick driver, before any worker wakes. Three volatile writes, no allocation, no lock — and nothing at all on a runtime whose
    /// application declared no subscriptions, which is what "an unused subsystem costs a database exactly nothing" has to mean on the tick path as well.
    /// </para>
    /// <para>
    /// <b>The origin is published before the tick number, and a reader can still straddle the pair.</b> Both orders leave the same one-tick worst case — an old
    /// number against a new origin reads as a tick that started just now, a new number against an old origin as one that started a period ago — so the origin
    /// goes first and the tick number, which NAMES the interval, becomes visible last. A client's clock is a min-offset estimator over many samples
    /// (<c>design/Subscriptions/05-sdks.md</c>), so one straddled <c>PONG</c> is filtered rather than believed.
    /// </para>
    /// </remarks>
    internal void PublishTickState(long tickNumber, long tickOriginTimestamp, int tickMultiplier)
    {
        if (!IsActive)
        {
            return;
        }

        Volatile.Write(ref _tickOriginTimestamp, tickOriginTimestamp);
        Volatile.Write(ref _tickPeriodUs, NominalTickPeriodUs * (uint)Math.Max(1, tickMultiplier));
        _frames?.SetTickState(NominalTickPeriodUs * (uint)Math.Max(1, tickMultiplier), tickMultiplier);
        Volatile.Write(ref _currentTick, (uint)tickNumber);

        // Before any system of this tick emits: what a tick no frame stage encoded left behind is discarded, not delivered later.
        Events?.OnTickStart((uint)tickNumber);
    }

    /// <summary>The replication state of a replicated archetype, or <see langword="null"/> when it is not replicated.</summary>
    /// <param name="archetypeCatalogId">The archetype's process-global catalog id.</param>
    /// <returns>The state, or <see langword="null"/>.</returns>
    public ArchetypeReplicationState StateOf(ushort archetypeCatalogId)
    {
        for (var i = 0; i < Plans.Length; i++)
        {
            if (Plans[i].ArchetypeCatalogId == archetypeCatalogId)
            {
                return _replicationStates[i];
            }
        }

        return null;
    }

    /// <summary>The compiled plan of a replicated archetype by its wire name, or <see langword="null"/>.</summary>
    /// <param name="name">The archetype's wire name.</param>
    /// <returns>The plan, or <see langword="null"/>.</returns>
    public CompiledProjectionPlan PlanNamed(string name)
    {
        foreach (var plan in Plans)
        {
            if (string.Equals(plan.Name, name, StringComparison.Ordinal))
            {
                return plan;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds one replication state per replicated archetype and publishes it to that archetype's cluster state.
    /// </summary>
    /// <remarks>
    /// <see cref="ArchetypeReplicationState.AttachTo"/> rather than a raw field assignment: it remembers the attachment so disposal clears the ECS's own
    /// reference, and the ECS holds that reference through a cluster drain that must never throw.
    /// </remarks>
    private ArchetypeReplicationState[] AttachReplicationStates(DatabaseEngine engine, IResource parent, NetIdAllocator netIds)
    {
        var states = new ArchetypeReplicationState[Plans.Length];

        // Assigned to the field as they are created rather than at the end: a throw half way through has to reach Dispose with the states already built, or
        // their pools' native slabs and their registry nodes outlive the runtime that failed to start.
        _replicationStates = states;
        for (var i = 0; i < Plans.Length; i++)
        {
            var plan = Plans[i];
            var clusterState = ClusterStateOf(engine, plan);
            states[i] = new ArchetypeReplicationState($"Subscriptions.Replication.{plan.Name}", parent, engine.MemoryAllocator, plan.BlockLayout, Options,
                netIds);

            // The motion rule's teleport threshold and its heartbeat are both expressed in ticks, so the state carries the nominal period rather than
            // assuming one: a 10 Hz runtime left at the default would get a threshold six times too tight and a heartbeat six times too long.
            states[i].TickPeriodSeconds = NominalTickPeriodSeconds;
            states[i].AttachTo(clusterState);

            // Narrowed HERE and nowhere else, because this is the only place a compiled plan and its cluster state are both in hand. Until this runs the
            // mask is all ones, which suppresses nothing — an archetype whose plan has not been published yet must not have its writes dropped.
            clusterState.ProjectedComponentMask = ProjectedComponentMaskOf(plan);

            // The membership signal, on for every replicated archetype: the engine's own pushes — spawns, destroys, WriteSpatial, migrations — ride it.
            clusterState.TrackStructureChanges = true;
        }

        return states;
    }

    /// <summary>
    /// The component slots one archetype's projection reads, as a bitmask.
    /// </summary>
    /// <param name="plan">The compiled plan.</param>
    /// <returns>Bit <c>s</c> set for every component slot the plan reads; all ones when any slot is too wide for the mask to describe.</returns>
    /// <remarks>
    /// <para>
    /// <b>Every reader of a component value has to appear here, and the cost of forgetting one is a change no client is told about.</b> The three are the
    /// public fields, the owner fields, and the position — whose velocity may live in a component of its own when <c>VelocityFrom</c> named one, which is a
    /// second slot the position contributes and the easiest of the three to miss.
    /// </para>
    /// <para>
    /// <b>All ones on a slot at or past 64.</b> The mask cannot describe it, and admitting everything is the direction whose failure is wasted work rather
    /// than a silent loss. Cluster layouts today are far narrower than that; the guard is for the day one is not.
    /// </para>
    /// </remarks>
    private static ulong ProjectedComponentMaskOf(CompiledProjectionPlan plan)
    {
        var mask = 0UL;

        static bool Add(ref ulong mask, int slot)
        {
            if ((uint)slot >= 64u)
            {
                return false;
            }

            mask |= 1UL << slot;
            return true;
        }

        var fields = plan.Fields;
        for (var i = 0; fields != null && i < fields.Length; i++)
        {
            if (!Add(ref mask, fields[i].ComponentSlot))
            {
                return ulong.MaxValue;
            }
        }

        var owners = plan.OwnerFields;
        for (var i = 0; owners != null && i < owners.Length; i++)
        {
            if (!Add(ref mask, owners[i].ComponentSlot))
            {
                return ulong.MaxValue;
            }
        }

        var position = plan.Position;
        if (position != null)
        {
            if (!Add(ref mask, position.ComponentSlot))
            {
                return ulong.MaxValue;
            }

            // 0xFF is "the velocity is measured from successive positions", not a slot.
            if (position.VelocityComponentSlot != 0xFF && !Add(ref mask, position.VelocityComponentSlot))
            {
                return ulong.MaxValue;
            }
        }

        return mask;
    }

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine engine, CompiledProjectionPlan plan)
    {
        var states = engine._archetypeStates;
        var clusterState = states != null && plan.ArchetypeCatalogId < states.Length ? states[plan.ArchetypeCatalogId]?.ClusterState : null;
        if (clusterState == null)
        {
            throw new InvalidOperationException(
                $"Archetype '{plan.Name}' is replicated and this engine holds no cluster state for it. Replication follows entities through their clusters, " +
                "so the archetype has to be cluster-backed and initialised — call DatabaseEngine.InitializeArchetypes before TyphonRuntime.Start().");
        }

        return clusterState;
    }

    /// <summary>Whether the application declared anything at all. Nothing declared means nothing built.</summary>
    private static bool HasDeclarations(SubscriptionsRegistry registry) =>
        registry.Archetypes.Count > 0
        || registry.Profiles.Count > 0
        || registry.Commands.Count > 0
        || registry.Events.Count > 0
        || registry.Metrics.Count > 0
        || registry.Sessions.DeclaredKinds.Count > 0;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>The reverse of construction, and it is load-bearing.</b> Each replication state detaches itself from its cluster state before it frees its
    /// directory and returns its pool's slabs, so no drain that runs afterwards can reach freed native memory. The session table goes last because a session
    /// row outlives the blocks it was watching, never the other way round.
    /// </para>
    /// <para>
    /// <b>The identity allocator is not touched.</b> It is shared with nothing here and owned by <see cref="TyphonRuntime"/>, whose tick end drains its
    /// quarantine — disposing it from this object would free it while a tick past the shutdown check could still reach it.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        // Published, not merely assigned: a transport thread reads it through IsAccepting to stop taking connections, and that read is on another core.
        Volatile.Write(ref _disposed, true);

        // Disposed, not cleared. A disposed state refuses every call that could reach its freed memory, and keeping the array intact leaves the pool counters
        // and the drain-fault count readable afterwards — which is what a shutdown leak is diagnosed from, and what this slice's teardown test asserts on.
        var states = _replicationStates;
        for (var i = 0; i < states.Length; i++)
        {
            states[i]?.Dispose();
        }

        // Disposed and KEPT, where it used to be nulled. A transport thread can be inside a HELLO while this runs, and the table is built for exactly that —
        // it latches, waits for the callers already inside and then answers every later call as it would for a slot that is gone. Nulling the field instead
        // turned that designed-for race into a NullReferenceException on a network thread, which is the one outcome neither side can do anything about.
        _sessions?.Dispose();

        // After the table, and in this order for the same reason as the states above: the table is what stops new sessions reaching a ring, so the rings go
        // once nothing can take one. The ingress drops its rows first, so the pool's Dispose frees slabs no row still names.
        _ingress?.Dispose();
        _ingressRings?.Dispose();

        // Last of all: a frame block outlives the session that produced it only until its send completes, and the assembler returns every block a slot
        // still names before it frees the pool's slabs.
        // Before the assembler, because a running pump holds a pointer into the assembler's frame pool for the duration of one send. Its Dispose quiesces.
        _sendPump?.Dispose();

        _frames?.Dispose();
    }

    /// <summary>
    /// Releases the tick as durable and starts the send of every frame it produced.
    /// </summary>
    /// <param name="tick">The tick whose unit of work has flushed.</param>
    /// <remarks>
    /// Called by the tick driver at the publication gate, after the flush and only on a tick that earned publication — SUB-02's whole content in one call.
    /// </remarks>
    internal void PublishFrames(long tick) => _sendPump?.PublishAndWake(tick);

    /// <summary>
    /// Throws away what the tick produced, closing every session that produced a frame.
    /// </summary>
    /// <returns>How many sessions were closed.</returns>
    /// <remarks>
    /// The other half of the gate: a tick that aborted, failed its fence, faulted a replication stage or failed its flush has frames in slots that can never
    /// be sent, and the clients holding their baselines have to be told so.
    /// </remarks>
    internal int DiscardFrames() => _sendPump?.DiscardProduced() ?? 0;
}
