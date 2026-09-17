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
        _viewTx = Dbe.CreateQuickTransaction();
        _playerView = _viewTx.Query<Player>().ToView();
        _creatureView = _viewTx.Query<Creature>().ToView();
        _npcView = _viewTx.Query<CityNpc>().ToView();
        _lairView = _viewTx.Query<CreatureLair>().ToView();
        _structureView = _viewTx.Query<WorldObject>().ToView();

        _bridge = new SimBridge(_config, Map, Index)
        {
            Dbe = Dbe,
            PlayerView = _playerView,
            CreatureView = _creatureView,
            NpcView = _npcView,
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
        });

        // Before Start, because the catalog a client negotiates against is compiled there and the declarations are its source.
        TatooineReplication.Declare(_runtime.Subscriptions);

        _runtime.OnTickAborted += (_, outcome)
            => Console.WriteLine($"  !! tick {outcome.TickNumber} aborted: {outcome.Reason} in '{outcome.FailedSystemName}'");

        _runtime.Start();

        try
        {
            await TatooineHost.ServeAsync(_runtime, port, clientRoot).ConfigureAwait(false);
        }
        finally
        {
            _runtime.Shutdown();
        }
    }

    /// <summary>The simulation's own schedule, plus the one system a server needs that a benchmark does not.</summary>
    private void BuildServeSchedule(RuntimeSchedule schedule)
    {
        BuildSchedule(schedule);

        // A session with no profile is in no tick's session set and receives nothing, so this is what turns a connection into a viewer. It runs on the public
        // track like any other system, which is the point: binding a session is application work, not engine work.
        schedule.PublicTrack.DeclareDag("Replication").CallbackSystem("BindSessions", TatooineReplication.BindOpenedSessions);
    }

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
