using SwgTatooine.Replication;
using System;
using System.IO;
using System.Threading.Tasks;

namespace SwgTatooine;

/// <summary>Serving the simulation to clients, as opposed to measuring it.</summary>
/// <remarks>
/// <para>
/// <b>Why this is a second entry point and not a flag inside <c>Run</c>.</b> The measurement path is built around a fixed tick count, a warm-up window and a
/// summary; a server has none of those and would have to opt out of all three. Keeping them apart means the benchmark keeps measuring exactly what it always
/// measured, and the server is free to run forever.
/// </para>
/// <para>
/// Everything else is shared: the same world build, the same systems, the same schedule. The only additions are the replication declaration, the system that
/// binds an opened session to a profile, and the web host.
/// </para>
/// </remarks>
public sealed partial class TatooineSim
{
    /// <summary>
    /// Builds the views and the schedule, starts the runtime, and serves it over WebSocket until the process stops.
    /// </summary>
    /// <param name="port">The port to listen on.</param>
    /// <param name="clientRoot">A directory of built client files to serve at the root, or <see langword="null"/>.</param>
    /// <returns>A task that completes when the host has stopped.</returns>
    public async Task ServeAsync(int port, string clientRoot)
    {
        StartReplication();
        try
        {
            await TatooineHost.ServeAsync(_runtime, port, clientRoot).ConfigureAwait(false);
        }
        finally
        {
            _runtime.Shutdown();
        }
    }

    /// <summary>
    /// The runtime this simulation is ticking, once <see cref="StartReplication"/> or <see cref="Run"/> has started one.
    /// </summary>
    /// <remarks>
    /// Public for the same reason <see cref="Dbe"/> is: this is a demo whose purpose is to be driven from outside — by <c>Program</c>, by a sweep, and by
    /// the checks beside it. A test that has to reach a private field through reflection is a test that breaks when the field is renamed, and stops
    /// testing when the field is removed.
    /// </remarks>
    public TyphonRuntime Runtime => _runtime;

    /// <summary>
    /// Everything <see cref="ServeAsync"/> does except the web host: the views, the bridge, the replication declarations and a started runtime.
    /// </summary>
    /// <remarks>
    /// <b>Split out so that replication can be exercised without a socket.</b> A transport is a seam
    /// (<c>design/Subscriptions/04-transport.md § 2</c>): a test starts its own in-process transport against
    /// <see cref="TyphonRuntime.StartSubscriptionTransport"/> and receives the same frames a WebSocket would carry. Going through the web host instead would
    /// test Kestrel, bind a port, and make a check that is about a <c>KICK</c> fail for a reason that has nothing to do with one.
    /// </remarks>
    public void StartReplication()
    {
        BuildViews();

        _bridge = new SimBridge(_config, Map, Index)
        {
            Dbe = Dbe,
            PlanetIndexes = Indexes,
            InteriorsPerPlanet = InteriorsPerPlanet,
            FirstDungeonRealm = FirstDungeonRealm,
            PlayerView = _playerView,
            CreatureView = _creatureView,
            NpcView = _npcView,
            ShipView = _shipView,
            LairView = _lairView,
            StructureView = _structureView,
        };

        _runtime = TyphonRuntime.Create(Dbe, BuildServeSchedule, new RuntimeOptions
        {
            BaseTickRate = _config.TickRateHz,
            WorkerCount = _config.ResolveWorkerCount(),
            ParallelQueryMinChunkSize = _config.ParallelQueryMinChunkSize,
            CostBasedChunking = _config.CostBasedChunking,
            EnableParallelFence = _config.ParallelFence,

            // The same strict policy the measured path takes (P-3), and for the server the argument is if anything stronger. Under the engine's default a
            // faulting system's branch is skipped and the tick is reported as a SUCCESS, so a server whose combat resolution threw would keep publishing frames
            // of a world in which nothing resolves — clients would see a frozen fight and no operator would learn why. A terminal stop is diagnosable and
            // 08-hosting's systemd unit already answers it with Restart=on-failure, which restarts from a checkpoint rather than limping on.
            SystemExceptionPolicy = SystemExceptionPolicy.AbortTickAndStop,

            // --subs-pipeline and --subs-mode. Every other field of the options is left at its default: these are the ones an A/B moves, and moving another
            // would make the two arms differ in more than the thing being measured.
            Subscriptions = new SubscriptionsOptions
            {
                CollapseBelowWorkUnits = _config.SubscriptionsCollapseWorkUnits,
                AllowAutomaticPushDetection = _config.SubscriptionsPushAutomatic,

                // A third of the players' 192 m radius: an 11 x 11 window per session.
                ReplicationCellM = TatooineReplication.ReplicationCellM,

                // Each session's inbound budget (--ingress-budget): required once clients can send commands, and the god camera's ClientRegion is one.
                IngressBytesPerSecond = _config.IngressBytesPerSecond,
            },
        });

        // Before Start, because the catalog a client negotiates against is compiled there and the declarations are its source.
        TatooineReplication.PlayerLeaveM = _config.PlayerLeaveM;
        TatooineReplication.GodRegionMaxEdgeM = _config.GodRegionMaxEdgeM;
        TatooineReplication.GodNearBudget = _config.GodNearBudget;
        TatooineReplication.Planets = _config.Planets;
        TatooineReplication.MaxClients = _config.MaxClients;
        TatooineReplication.MaxSpectators = _config.MaxSpectators;

        // What an intent is validated against: the world it must stay inside, and the tick it gets one step of (SWG-01). Required rather than defaulted, so a
        // path that forgot it would refuse to start rather than clamp to the wrong world silently.
        TatooineReplication.ConfigureIntents(Dbe, _config.WorldEdgeM, _config.TickRateHz);
        TatooineReplication.Declare(_runtime.Subscriptions, _config.SubscriptionsPushAutomatic);
        TatooineReplication.PlayerBudgetBytesPerSecond = _config.SessionBudgetBytesPerSecond;

        var aborts = 0;
        _runtime.OnTickAborted += (_, outcome) =>
        {
            Console.WriteLine($"  !! tick {outcome.TickNumber} aborted: {outcome.Reason} in '{outcome.FailedSystemName}': {outcome.FailedSystemException}");

            // The artefact, on the first abort only, exactly as the measured path does it (P-3). For a server this is the whole of the diagnosis: there is no
            // report at the end of a run that never ends, so a fault that is not written down is a fault nobody can look at afterwards.
            if (++aborts == 1)
            {
                CrashArtefactPath = WriteCrashArtefact(
                    $"tick aborted: {outcome.Reason} in system '{outcome.FailedSystemName}'", outcome.FailedSystemException, outcome.TickNumber);
            }
        };

        TatooineReplication.Scheduler = _runtime.Scheduler;
        _runtime.Start();

        // The catalog every client negotiates against, by its hash: two builds that print the same one serve the same wire (AC-25 compares the attribute
        // declarations against the builder ones this way).
        var catalog = _runtime.SubscriptionsCatalogJson;
        Console.WriteLine($"  catalog {Typhon.Protocol.CatalogSerializer.HashBytes(catalog.Span):X16}, {catalog.Length} B");
    }

    /// <summary>The simulation's own schedule, plus the two systems a server needs that a benchmark does not.</summary>
    /// <remarks>
    /// <b>They join the simulation's DAG rather than a DAG of their own, which they had until SWG-01.</b> Two DAGs on one track have no barrier between them
    /// and cannot carry an edge to each other, so a session system in its own DAG could run concurrently with <c>PlayerThink</c> — which matters the moment it
    /// applies an intent, because both write <c>PlayerMotion</c> — and concurrently with a second session system, which matters because session requests share
    /// one unsynchronized segment. See <see cref="TatooineReplication.SessionTick"/>.
    /// </remarks>
    private void BuildServeSchedule(RuntimeSchedule schedule)
        => BuildSchedule(schedule, replicating: true);

    /// <summary>Where the built browser client is expected, relative to the repository root.</summary>
    /// <param name="baseDirectory">The process's base directory.</param>
    /// <returns>The directory, whether or not it exists.</returns>
    /// <remarks>
    /// The client is a Vite build under <c>demo/SwgTatooine.Client/dist</c>, and <c>dotnet build</c> does not run Vite — so a fresh checkout has no client to
    /// serve and the host says so rather than answering 404 for the root and leaving the operator guessing.
    /// </remarks>
    public static string DefaultClientRoot(string baseDirectory)
    {
        var directory = new DirectoryInfo(baseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "demo", "SwgTatooine.Client", "dist");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
